namespace GitDelta.Git.Internal;

/// <summary>
/// Minimal async-friendly reader/writer lock built from two semaphores. Not reentrant.
/// Writers exclude all readers and other writers; readers run concurrently with each other.
/// </summary>
internal sealed class AsyncReaderWriterLock
{
    private readonly SemaphoreSlim _readerCountGate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _readerCount;

    public async Task<AsyncLockHandle> AcquireReadAsync(CancellationToken ct)
    {
        await _readerCountGate.WaitAsync(ct).ConfigureAwait(false);
        var isFirstReader = false;
        try
        {
            _readerCount++;
            isFirstReader = _readerCount == 1;
        }
        finally
        {
            _readerCountGate.Release();
        }

        if (isFirstReader)
        {
            try
            {
                await _writerGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await RollBackReaderAsync().ConfigureAwait(false);
                throw;
            }
        }

        return new AsyncLockHandle(this, isWriter: false);
    }

    public async Task<AsyncLockHandle> AcquireWriteAsync(CancellationToken ct)
    {
        await _writerGate.WaitAsync(ct).ConfigureAwait(false);
        return new AsyncLockHandle(this, isWriter: true);
    }

    internal async ValueTask ReleaseReadAsync()
    {
        await _readerCountGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _readerCount--;
            if (_readerCount == 0)
                _writerGate.Release();
        }
        finally
        {
            _readerCountGate.Release();
        }
    }

    internal void ReleaseWrite() => _writerGate.Release();

    private async Task RollBackReaderAsync()
    {
        await _readerCountGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _readerCount--;
        }
        finally
        {
            _readerCountGate.Release();
        }
    }
}

internal readonly struct AsyncLockHandle(AsyncReaderWriterLock owner, bool isWriter) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        if (isWriter)
        {
            owner.ReleaseWrite();
            return ValueTask.CompletedTask;
        }

        return owner.ReleaseReadAsync();
    }
}
