using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Tiered reader/writer gate coordinating this application's own `git` invocations against a
/// single repository. See Plan.md, "Concurrency".
///
/// | Class | Commands | Gate |
/// | --- | --- | --- |
/// | Read | diff, status, log, cat-file, show | Shared, unbounded |
/// | Index write | apply --cached, add, reset, commit | Exclusive against other writes, does not block reads |
/// | Worktree write | checkout, pull, merge, discard | Exclusive against everything |
/// | Network | fetch, push | No gate, single flight |
///
/// This coordinates only processes launched by this application. A terminal, an IDE, or a
/// background `git gc` can still hold `.git/index.lock`; that is a Git-level contention the
/// gate cannot see, and callers should retry with bounded backoff on that failure instead.
///
/// Network operations are deliberately excluded from the shared/worktree lock entirely: a slow
/// push touches neither the index nor the worktree and must never block a diff read. "Single
/// flight per remote" is simplified to single flight per repository, since the gate is scoped
/// per repository and remote name is not part of the call contract.
/// </summary>
public sealed class RepositoryGate : IRepositoryGate
{
    private readonly AsyncReaderWriterLock _worktreeLock = new();
    private readonly SemaphoreSlim _indexWriteLock = new(1, 1);
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private long _epoch;

    public long CurrentEpoch => Interlocked.Read(ref _epoch);

    public async Task<T> RunReadAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var _ = await _worktreeLock.AcquireReadAsync(ct).ConfigureAwait(false);
        return await action(ct).ConfigureAwait(false);
    }

    public async Task<T> RunIndexWriteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        // Index writes hold a "reader" slot against the worktree lock (so they never block, or
        // are blocked by, plain reads) but additionally serialise against each other so two
        // index-mutating commands never race for the index.
        await using var readHandle = await _worktreeLock.AcquireReadAsync(ct).ConfigureAwait(false);
        await _indexWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await action(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _epoch);
            return result;
        }
        finally
        {
            _indexWriteLock.Release();
        }
    }

    public async Task<T> RunWorktreeWriteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await using var writeHandle = await _worktreeLock.AcquireWriteAsync(ct).ConfigureAwait(false);
        var result = await action(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _epoch);
        return result;
    }

    public async Task RunNetworkAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await _networkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _networkLock.Release();
        }
    }
}
