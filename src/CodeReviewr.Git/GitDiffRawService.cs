using System.Diagnostics;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Core.Diff;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Raw Git-side diff data. Returns patch text and `--raw` file-level metadata only; parsing a
/// patch into a canonical <c>FileDiff</c> is <c>CodeReviewr.Diff</c>'s job, composed on top of
/// this service so that <c>CodeReviewr.Git</c> never references <c>CodeReviewr.Diff</c>.
/// </summary>
public sealed class GitDiffRawService(
    IGitProcessRunner runner,
    IRepositoryGateProvider gates,
    ISettingsStore? settings = null) : IGitDiffRawService
{
    public Task<string> GetPatchAsync(
        string repositoryPath,
        FilePath path,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default)
    {
        var args = GitDiffArgumentBuilder.BuildPatchArgs(scope, options, path);
        var maxBytes = settings?.Current.MaxDiffPatchBytes ?? 32 * 1024 * 1024;
        var processOptions = new GitProcessOptions { MaxStdoutBytes = maxBytes };

        return gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(repositoryPath, args, processOptions, token).ConfigureAwait(false);
            CodeReviewrMeters.DiffGenerationMs.Record(sw.Elapsed.TotalMilliseconds);
            return result.Stdout;
        }, ct);
    }

    public Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawFileListAsync(
        string repositoryPath,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default)
    {
        var args = GitDiffArgumentBuilder.BuildRawArgs(scope, options, path: null);

        return gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var result = await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
            return ParseRaw(result.Stdout);
        }, ct);
    }

    /// <summary>
    /// Parses `git diff --raw -z` output:
    /// <c>:oldmode newmode oldsha newsha status\0path\0</c>, or for renames/copies
    /// <c>:oldmode newmode oldsha newsha statusScore\0oldpath\0newpath\0</c>.
    /// </summary>
    private static IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)> ParseRaw(string rawOutput)
    {
        var entries = new List<(FilePath, ContentId, ContentId, ChangeKind)>();
        var tokens = rawOutput.Split('\0');

        var i = 0;
        while (i < tokens.Length)
        {
            var meta = tokens[i];
            if (meta.Length == 0 || meta[0] != ':')
            {
                i++;
                continue;
            }

            // ":100644 100644 <oldsha> <newsha> <status>"
            var fields = meta.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
            {
                i++;
                continue;
            }

            var oldOid = ContentId.FromSha(fields[2]);
            var newOid = ContentId.FromSha(fields[3]);
            var statusField = fields[4];
            var statusChar = statusField.Length > 0 ? statusField[0] : 'M';
            var kind = ToChangeKind(statusChar);

            if (statusChar is 'R' or 'C')
            {
                // Rename/copy consumes two path tokens; only the new path is reported here,
                // matching the interface contract of one path per entry.
                var newPath = i + 2 < tokens.Length ? tokens[i + 2] : string.Empty;
                if (newPath.Length > 0)
                    entries.Add((FilePath.From(newPath), oldOid, newOid, kind));
                i += 3;
            }
            else
            {
                var path = i + 1 < tokens.Length ? tokens[i + 1] : string.Empty;
                if (path.Length > 0)
                    entries.Add((FilePath.From(path), oldOid, newOid, kind));
                i += 2;
            }
        }

        return entries;
    }

    private static ChangeKind ToChangeKind(char statusChar) => statusChar switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'M' => ChangeKind.Modified,
        'T' => ChangeKind.TypeChanged,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'U' => ChangeKind.Conflicted,
        _ => ChangeKind.Modified,
    };
}
