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
    private readonly ConcurrentDictionary<string, Task<string>> _inflightCommonDir = new(StringComparer.Ordinal);

    public RepositoryGateProvider(IGitProcessRunner runner) => _runner = runner;

    /// <inheritdoc />
    /// <remarks>
    /// Blocks on common-dir resolution. Prefer <see cref="ForAsync"/> when a synchronization
    /// context (e.g. UI) may be present.
    /// </remarks>
    public IRepositoryGate For(string repositoryPath) =>
        ForAsync(repositoryPath, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<IRepositoryGate> ForAsync(string repositoryPath, CancellationToken ct = default)
    {
        var absRepo = Path.GetFullPath(repositoryPath);
        if (!_commonDirByRepo.TryGetValue(absRepo, out var commonDir))
            commonDir = await ResolveCommonDirAsync(absRepo, ct).ConfigureAwait(false);

        return _gatesByCommonDir.GetOrAdd(commonDir, _ => new RepositoryGate());
    }

    private async Task<string> ResolveCommonDirAsync(string absRepoPath, CancellationToken ct)
    {
        if (_commonDirByRepo.TryGetValue(absRepoPath, out var cached))
            return cached;

        while (true)
        {
            if (_inflightCommonDir.TryGetValue(absRepoPath, out var existing))
            {
                var commonDir = await existing.WaitAsync(ct).ConfigureAwait(false);
                return _commonDirByRepo.GetOrAdd(absRepoPath, commonDir);
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightCommonDir.TryAdd(absRepoPath, tcs.Task))
                continue;

            // Shared resolution must not take a caller token — canceling one waiter must not
            // fault the inflight task for others. Each caller cancels only its own WaitAsync.
            _ = CompleteInflightCommonDirAsync(absRepoPath, tcs);

            var resolved = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            return _commonDirByRepo.GetOrAdd(absRepoPath, resolved);
        }
    }

    private async Task CompleteInflightCommonDirAsync(string absRepoPath, TaskCompletionSource<string> tcs)
    {
        try
        {
            var commonDir = await ResolveCommonDirCoreAsync(absRepoPath, CancellationToken.None)
                .ConfigureAwait(false);
            _commonDirByRepo.TryAdd(absRepoPath, commonDir);
            tcs.SetResult(commonDir);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            _inflightCommonDir.TryRemove(absRepoPath, out _);
        }
    }

    private async Task<string> ResolveCommonDirCoreAsync(string absRepoPath, CancellationToken ct)
    {
        var result = await _runner
            .RunAsync(absRepoPath, ["rev-parse", "--git-common-dir"], options: null, ct)
            .ConfigureAwait(false);

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
