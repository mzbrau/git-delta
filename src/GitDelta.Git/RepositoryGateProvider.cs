using System.Collections.Concurrent;
using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.Git;

/// <summary>
/// Returns a per-git-common-directory <see cref="RepositoryGate"/> so worktrees sharing a
/// repository coordinate through one gate instance.
/// </summary>
public sealed class RepositoryGateProvider : IRepositoryGateProvider
{
    private readonly IGitProcessRunner _runner;
    private readonly ConcurrentDictionary<string, RepositoryGate> _gatesByCommonDir = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _commonDirByRepo = new(StringComparer.Ordinal);

    public RepositoryGateProvider(IGitProcessRunner runner) => _runner = runner;

    public IRepositoryGate For(string repositoryPath)
    {
        var absRepo = Path.GetFullPath(repositoryPath);
        var commonDir = _commonDirByRepo.GetOrAdd(absRepo, ResolveCommonDir);
        return _gatesByCommonDir.GetOrAdd(commonDir, _ => new RepositoryGate());
    }

    private string ResolveCommonDir(string absRepoPath)
    {
        var result = _runner
            .RunAsync(absRepoPath, ["rev-parse", "--git-common-dir"], options: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!result.Succeeded)
            throw new GitException($"git rev-parse --git-common-dir failed: {result.Stderr}");

        var commonDir = result.Stdout.Trim();
        if (string.IsNullOrEmpty(commonDir))
            throw new GitException("git rev-parse --git-common-dir returned empty output");

        if (!Path.IsPathRooted(commonDir))
            commonDir = Path.GetFullPath(Path.Combine(absRepoPath, commonDir));
        else
            commonDir = Path.GetFullPath(commonDir);

        return commonDir;
    }
}
