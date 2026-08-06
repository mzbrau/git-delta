using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diagnostics;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

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

            using var activity = GitDeltaActivity.Source.StartActivity("git.status");
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await runner.RunAsync(
                    repositoryPath,
                    ["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"],
                    options: null,
                    token).ConfigureAwait(false);

                var parsed = PorcelainStatusParser.Parse(result.Stdout);
                var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);

                activity?.SetTag("status.staged_count", parsed.Staged.Count);
                activity?.SetTag("status.unstaged_count", parsed.Unstaged.Count);
                activity?.SetTag("status.conflicted_count", parsed.Conflicted.Count);

                return new RepositoryStatus(
                    parsed.Staged,
                    parsed.Unstaged,
                    parsed.Conflicted,
                    inProgress,
                    parsed.CurrentBranch,
                    epochAtStart);
            }
            finally
            {
                GitDeltaMeters.StatusRefreshMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }, ct);
    }
}
