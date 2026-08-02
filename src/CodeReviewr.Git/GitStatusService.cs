using System.Diagnostics;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Reads repository status via `git status --porcelain=v2 -z` and detects in-progress
/// merge/rebase/cherry-pick/revert state from the filesystem, independent of conflicted files,
/// so a paused rebase with a clean index is still reported honestly.
/// </summary>
public sealed class GitStatusService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitStatusService
{
    public Task<RepositoryStatus> GetStatusAsync(string repositoryPath, CancellationToken ct = default)
    {
        var gate = gates.For(repositoryPath);
        return gate.RunReadAsync(async token =>
        {
            // Captured at the start of the guarded read window: this is the epoch this status
            // is guaranteed to be at least as fresh as. Capturing it any later would let a
            // concurrent index write make the result appear newer than what was actually read.
            var epochAtStart = gate.CurrentEpoch;

            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(
                repositoryPath,
                ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"],
                options: null,
                token).ConfigureAwait(false);
            CodeReviewrMeters.StatusRefreshMs.Record(sw.Elapsed.TotalMilliseconds);

            var parsed = PorcelainStatusParser.Parse(result.Stdout);
            var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);

            return new RepositoryStatus(
                parsed.Staged,
                parsed.Unstaged,
                parsed.Conflicted,
                inProgress,
                parsed.CurrentBranch,
                epochAtStart);
        }, ct);
    }
}
