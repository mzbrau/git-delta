using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>Lists linked worktrees via porcelain <c>git worktree list</c>.</summary>
public sealed class GitWorktreeService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitWorktreeService
{
    public async Task<string?> TryGetCommonDirectoryAsync(string repositoryPath, CancellationToken ct = default)
    {
        try
        {
            return await gates.WithGateAsync(
                repositoryPath,
                gate => gate.RunReadAsync(token => ResolveCommonDirectoryCoreAsync(repositoryPath, token), ct),
                ct).ConfigureAwait(false);
        }
        catch (GitException)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.WithGateAsync(
            repositoryPath,
            gate => gate.RunReadAsync(
                token => ListWorktreesCoreAsync(repositoryPath, token),
                ct),
            ct);

    private async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesCoreAsync(string repositoryPath, CancellationToken ct)
    {
        var result = await runner
            .RunAsync(repositoryPath, ["worktree", "list", "--porcelain"], options: null, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
            return [];

        return WorktreeListParser.Parse(result.Stdout);
    }

    private async Task<string?> ResolveCommonDirectoryCoreAsync(string repositoryPath, CancellationToken ct)
    {
        var result = await runner
            .RunAsync(repositoryPath, ["rev-parse", "--git-common-dir"], options: null, ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
            return null;

        var commonDir = result.Stdout.Trim();
        if (string.IsNullOrEmpty(commonDir))
            return null;

        return Path.IsPathRooted(commonDir)
            ? Path.GetFullPath(commonDir)
            : Path.GetFullPath(Path.Combine(repositoryPath, commonDir));
    }
}
