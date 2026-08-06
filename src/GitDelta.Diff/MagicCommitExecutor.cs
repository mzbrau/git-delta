using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Diff;
using Microsoft.Extensions.Logging;

namespace GitDelta.Diff;

/// <summary>
/// Executes a <see cref="MagicCommitPlan"/> by rematching hunk fingerprints against fresh diffs,
/// staging via <see cref="PatchSynthesizer"/>, and committing sequentially.
/// </summary>
public sealed class MagicCommitExecutor(
    IGitDiffService diffService,
    IGitStagingService staging,
    IGitCommitService commit,
    IGitHistoryService history,
    ILogger<MagicCommitExecutor>? logger = null)
{
    public async Task<MagicCommitExecutionResult> ExecuteAsync(
        string repositoryPath,
        IReadOnlyList<MagicCommitHunkItem> inventory,
        MagicCommitPlan plan,
        DiffOptions options,
        bool noVerify,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);

        if (plan.Commits.Count == 0)
            return new MagicCommitExecutionResult([], "The plan contained no commits.");

        var byId = inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in plan.Commits)
        {
            if (string.IsNullOrWhiteSpace(entry.Message))
                return new MagicCommitExecutionResult([], "A planned commit is missing a message.");
            if (entry.HunkIds.Count == 0)
                return new MagicCommitExecutionResult([], "A planned commit has no hunks.");
            foreach (var id in entry.HunkIds)
            {
                if (!byId.ContainsKey(id))
                    return new MagicCommitExecutionResult([], $"Unknown hunk id '{id}' in plan.");
                if (!assigned.Add(id))
                    return new MagicCommitExecutionResult([], $"Hunk id '{id}' was assigned to more than one commit.");
            }
        }

        if (assigned.Count != inventory.Count)
            return new MagicCommitExecutionResult([], "Every inventory hunk must appear in exactly one commit.");

        var results = new List<MagicCommitResultEntry>();
        var commitIndex = 0;

        try
        {
            foreach (var entry in plan.Commits)
            {
                ct.ThrowIfCancellationRequested();
                commitIndex++;
                progress?.Report($"Staging commit {commitIndex}/{plan.Commits.Count}: {TruncateSubject(entry.Message)}");

                var items = entry.HunkIds.Select(id => byId[id]).ToList();
                await StageItemsAsync(repositoryPath, items, options, results.Count, ct).ConfigureAwait(false);

                progress?.Report($"Creating commit {commitIndex}/{plan.Commits.Count}…");
                await commit.CommitAsync(repositoryPath, entry.Message.Trim(), amend: false, noVerify, hookOutput: null, ct)
                    .ConfigureAwait(false);

                var created = await history.ListCommitsAsync(repositoryPath, skip: 0, take: 1, ct: ct)
                    .ConfigureAwait(false);
                if (created.Count == 0)
                    return new MagicCommitExecutionResult(results, "Commit succeeded but HEAD could not be read.");

                var head = created[0];
                results.Add(new MagicCommitResultEntry(head.Oid, head.ShortOid, head.Subject));
                progress?.Report($"Created {head.ShortOid}: {head.Subject}");
            }

            return new MagicCommitExecutionResult(results);
        }
        catch (OperationCanceledException)
        {
            return new MagicCommitExecutionResult(results, "Magic Commit was cancelled.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Magic Commit failed after {Count} commits.", results.Count);
            return new MagicCommitExecutionResult(results, ex.Message);
        }
    }

    private async Task StageItemsAsync(
        string repositoryPath,
        IReadOnlyList<MagicCommitHunkItem> items,
        DiffOptions options,
        int completedCommits,
        CancellationToken ct)
    {
        var byPath = items.GroupBy(i => i.Path, StringComparer.Ordinal);
        foreach (var group in byPath)
        {
            ct.ThrowIfCancellationRequested();
            var path = FilePath.From(group.Key);
            var whole = group.Where(i => i.WholeFile).ToList();
            var hunks = group.Where(i => !i.WholeFile).ToList();

            if (whole.Count > 0)
            {
                await staging.StageFileAsync(repositoryPath, path, ct).ConfigureAwait(false);
                continue;
            }

            var diff = await diffService.GetDiffAsync(
                    repositoryPath, path, DiffTarget.IndexToWorktree, options, ct)
                .ConfigureAwait(false);

            var indices = new List<int>();
            foreach (var item in hunks)
            {
                var index = MagicCommitInventory.FindHunkIndex(diff, item.Fingerprint);
                if (index is null)
                {
                    var suffix = completedCommits > 0
                        ? " after earlier commits"
                        : "";
                    throw new InvalidOperationException(
                        $"Could not rematch hunk {item.Id} in '{item.Path}'{suffix}.");
                }

                indices.Add(index.Value);
            }

            var patch = PatchSynthesizer.SynthesizeHunks(diff, indices);
            await staging.StagePatchAsync(repositoryPath, patch, ct).ConfigureAwait(false);
        }
    }

    private static string TruncateSubject(string message)
    {
        var subject = message.Split('\n', 2)[0].Trim();
        return subject.Length <= 72 ? subject : subject[..72] + "…";
    }
}
