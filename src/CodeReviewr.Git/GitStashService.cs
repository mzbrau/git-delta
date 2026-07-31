using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// A two-operation escape hatch, not a stash feature. `git checkout` refuses to switch branches
/// when local changes would be overwritten; without a stash there is no way forward inside the
/// application. No stash browser, list, or partial stashing.
/// </summary>
public sealed class GitStashService(IGitProcessRunner runner, IRepositoryGate gate) : IGitStashService
{
    public Task StashPushAsync(string repositoryPath, string? message, CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(token =>
        {
            var args = new List<string> { "stash", "push" };
            if (!string.IsNullOrWhiteSpace(message))
            {
                args.Add("-m");
                args.Add(message);
            }

            return runner.RunAsync(repositoryPath, args, options: null, token);
        }, ct);

    public Task StashPopAsync(string repositoryPath, CancellationToken ct = default) =>
        gate.RunWorktreeWriteAsync(
            token => runner.RunAsync(repositoryPath, ["stash", "pop"], options: null, token),
            ct);
}
