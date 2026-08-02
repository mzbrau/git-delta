using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Detects and manages in-progress merge/rebase/cherry-pick/revert state. Conflict
/// *resolution* is out of scope; this only surfaces the state honestly and offers the standard
/// exits (abort, continue, mergetool, mark resolved).
/// </summary>
public sealed class GitConflictService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitConflictService
{
    public Task<InProgressOperation> DetectInProgressAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(_ => Task.FromResult(GitRepositoryPaths.DetectInProgress(repositoryPath)), ct);

    public Task AbortAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);
            var args = inProgress switch
            {
                InProgressOperation.Merge => new[] { "merge", "--abort" },
                InProgressOperation.Rebase => new[] { "rebase", "--abort" },
                InProgressOperation.CherryPick => new[] { "cherry-pick", "--abort" },
                InProgressOperation.Revert => new[] { "revert", "--abort" },
                _ => null,
            };

            if (args is not null)
                await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
        }, ct);

    public Task ContinueAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var inProgress = GitRepositoryPaths.DetectInProgress(repositoryPath);
            var args = inProgress switch
            {
                InProgressOperation.Merge => new[] { "merge", "--continue" },
                InProgressOperation.Rebase => new[] { "rebase", "--continue" },
                InProgressOperation.CherryPick => new[] { "cherry-pick", "--continue" },
                InProgressOperation.Revert => new[] { "revert", "--continue" },
                _ => null,
            };

            if (args is not null)
                await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
        }, ct);

    public Task OpenMergetoolAsync(string repositoryPath, FilePath? path, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunWorktreeWriteAsync(async token =>
        {
            var args = new List<string> { "mergetool" };
            if (path is { } p)
            {
                args.Add("--");
                args.Add(p.Value);
            }

            await runner.RunAsync(repositoryPath, args, options: null, token).ConfigureAwait(false);
        }, ct);

    public Task MarkResolvedAsync(string repositoryPath, FilePath path, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunIndexWriteAsync(
            token => runner.RunAsync(repositoryPath, ["add", "--", path.Value], options: null, token),
            ct);
}
