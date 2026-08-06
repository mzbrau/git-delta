using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.App.Services;

/// <summary>
/// Path-keyed single-flight cache of in-flight / completed <see cref="FileDiff"/> loads.
/// Supports stale-while-revalidate: soft-invalidated entries stay readable while a background
/// refetch replaces them.
///
/// Distinct from content-addressed <see cref="GitDelta.Core.Caching.MemoryDiffCache"/>:
/// <list type="bullet">
/// <item><see cref="DiffWarmStore"/> keys by repository path + pathspec + scope for UI prefetch.</item>
/// <item><c>MemoryDiffCache</c> keys by <see cref="FileDiffKey"/> (blob content + options) and is shared across scopes.</item>
/// </list>
/// Prefer warming through this store from the App layer; prefer <c>MemoryDiffCache</c> inside Diff orchestration.
/// </summary>
public sealed class DiffWarmStore : IDisposable
{
    public const int MinConcurrency = 1;
    public const int MaxConcurrency = 8;
    public const int DefaultConcurrency = 4;

    private readonly object _sync = new();
    private readonly Dictionary<DiffWarmKey, WarmEntry> _entries = new();
    private SemaphoreSlim _concurrency;
    private int _maxConcurrency;
    private int _inFlight;
    private CancellationTokenSource _generation = new();
    private bool _disposed;

    public DiffWarmStore(int maxConcurrency = DefaultConcurrency)
    {
        _maxConcurrency = ClampConcurrency(maxConcurrency);
        _concurrency = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    public int MaxConcurrencyLimit
    {
        get { lock (_sync) return _maxConcurrency; }
    }

    /// <summary>
    /// Adjusts the concurrency cap without dropping cached entries.
    /// </summary>
    public void SetMaxConcurrency(int maxConcurrency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var desired = ClampConcurrency(maxConcurrency);
        lock (_sync)
        {
            if (desired == _maxConcurrency)
                return;

            var old = _concurrency;
            var availableSlots = Math.Max(0, desired - _inFlight);
            _maxConcurrency = desired;
            _concurrency = new SemaphoreSlim(availableSlots, desired);
            try { old.Dispose(); } catch { /* ignore */ }
        }
    }

    public static int ClampConcurrency(int value) =>
        Math.Clamp(value, MinConcurrency, MaxConcurrency);

    /// <summary>
    /// Returns a completed entry (fresh or stale) if one exists.
    /// </summary>
    public bool TryGetCompleted(DiffWarmKey key, out DiffWarmEntry? entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var warm)
                && warm.CompletedDiff is not null
                && warm.CompletedAt is not null)
            {
                entry = new DiffWarmEntry(warm.CompletedDiff, warm.CompletedAt.Value, warm.IsStale);
                return true;
            }
        }

        entry = null;
        return false;
    }

    /// <summary>Convenience: completed <see cref="FileDiff"/> only (fresh or stale).</summary>
    public bool TryGetCompleted(DiffWarmKey key, out FileDiff? diff)
    {
        if (TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
        {
            diff = entry.Diff;
            return true;
        }

        diff = null;
        return false;
    }

    /// <summary>
    /// Returns an existing in-flight or completed task for <paramref name="key"/>, or starts
    /// <paramref name="factory"/> once. Concurrent callers share the same task.
    /// When <paramref name="force"/> is true, or the entry is stale, a new fetch is started
    /// (previous completed value remains readable via <see cref="TryGetCompleted"/> until replaced).
    /// </summary>
    public Task<FileDiff> GetOrStart(
        DiffWarmKey key,
        Func<CancellationToken, Task<FileDiff>> factory,
        bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                // Share an in-flight refresh.
                if (existing.RefreshTask is { IsCompleted: false } refresh)
                {
                    if (!force)
                        return refresh;
                    // force while already refreshing: share the in-flight work
                    return refresh;
                }

                // Fresh completed entry — reuse unless forced.
                if (!force
                    && !existing.IsStale
                    && existing.CompletedDiff is not null
                    && existing.RefreshTask is null)
                {
                    return Task.FromResult(existing.CompletedDiff);
                }

                // Stale or force: fall through to start a new refresh (keep CompletedDiff).
            }

            var generation = _generation.Token;
            var previous = _entries.TryGetValue(key, out var prev) ? prev : null;
            var refreshId = previous is null ? 1L : previous.RefreshId + 1;

            // Pre-register before ExecuteAsync runs — Task.FromResult factories can complete
            // synchronously before we would otherwise assign the dictionary entry.
            _entries[key] = new WarmEntry(
                CompletedDiff: previous?.CompletedDiff,
                CompletedAt: previous?.CompletedAt,
                IsStale: previous?.CompletedDiff is not null || (previous?.IsStale ?? false),
                RefreshTask: null,
                RefreshId: refreshId);

            var refreshTask = ExecuteAsync(key, factory, generation, refreshId);
            if (_entries.TryGetValue(key, out var current) && current.RefreshId == refreshId)
                _entries[key] = current with { RefreshTask = refreshTask };
            return refreshTask;
        }
    }

    public void SoftInvalidateAll()
    {
        lock (_sync)
        {
            foreach (var key in _entries.Keys.ToList())
                MarkStaleUnlocked(key);
        }
    }

    /// <summary>Soft-invalidate entries whose <see cref="DiffWarmKey.Scope"/> equals <paramref name="scope"/>.</summary>
    public void SoftInvalidateScope(string scope)
    {
        lock (_sync)
        {
            foreach (var key in _entries.Keys.Where(k => string.Equals(k.Scope, scope, StringComparison.Ordinal)).ToList())
                MarkStaleUnlocked(key);
        }
    }

    public void SoftInvalidatePath(string path)
    {
        lock (_sync)
        {
            foreach (var key in _entries.Keys.Where(k => string.Equals(k.Path, path, StringComparison.Ordinal)).ToList())
                MarkStaleUnlocked(key);
        }
    }

    private void MarkStaleUnlocked(DiffWarmKey key)
    {
        if (!_entries.TryGetValue(key, out var e)) return;
        if (e.CompletedDiff is not null)
            _entries[key] = e with { IsStale = true };
    }

    public void InvalidateAll()
    {
        lock (_sync)
        {
            _entries.Clear();
            _generation.Cancel();
            _generation.Dispose();
            _generation = new CancellationTokenSource();
        }
    }

    public void InvalidatePath(string path)
    {
        lock (_sync)
        {
            var toRemove = _entries.Keys
                .Where(k => string.Equals(k.Path, path, StringComparison.Ordinal))
                .ToList();
            foreach (var key in toRemove)
                _entries.Remove(key);
        }
    }

    private async Task<FileDiff> ExecuteAsync(
        DiffWarmKey key,
        Func<CancellationToken, Task<FileDiff>> factory,
        CancellationToken ct,
        long refreshId)
    {
        SemaphoreSlim gate;
        lock (_sync)
        {
            gate = _concurrency;
            _inFlight++;
        }

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) _inFlight = Math.Max(0, _inFlight - 1);
            throw;
        }

        try
        {
            var diff = await factory(ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.RefreshId == refreshId)
                {
                    _entries[key] = new WarmEntry(
                        CompletedDiff: diff,
                        CompletedAt: DateTimeOffset.UtcNow,
                        IsStale: false,
                        RefreshTask: null,
                        RefreshId: refreshId);
                }
            }

            return diff;
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.RefreshId == refreshId)
                {
                    if (entry.CompletedDiff is not null)
                    {
                        _entries[key] = entry with
                        {
                            RefreshTask = null,
                            IsStale = true,
                        };
                    }
                    else
                    {
                        _entries.Remove(key);
                    }
                }
            }

            throw;
        }
        catch
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var entry) && entry.RefreshId == refreshId)
                {
                    if (entry.CompletedDiff is not null)
                    {
                        _entries[key] = entry with
                        {
                            RefreshTask = null,
                            IsStale = true,
                        };
                    }
                    else
                    {
                        _entries.Remove(key);
                    }
                }
            }

            throw;
        }
        finally
        {
            try { gate.Release(); } catch (ObjectDisposedException) { /* replaced */ }
            lock (_sync) _inFlight = Math.Max(0, _inFlight - 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InvalidateAll();
        lock (_sync)
        {
            _concurrency.Dispose();
            _generation.Dispose();
        }
    }

    private sealed record WarmEntry(
        FileDiff? CompletedDiff,
        DateTimeOffset? CompletedAt,
        bool IsStale,
        Task<FileDiff>? RefreshTask,
        long RefreshId);
}

/// <summary>Snapshot of a completed warm-store entry (may be marked stale).</summary>
public sealed record DiffWarmEntry(FileDiff Diff, DateTimeOffset CompletedAt, bool IsStale);

/// <summary>
/// Warm-store key. <see cref="Scope"/> distinguishes File Status (<c>fs</c>),
/// history (<c>hist:{oid}</c>), and stash (<c>stash:{index}</c>) entries.
/// </summary>
public readonly record struct DiffWarmKey(
    string Scope,
    string Path,
    DiffScope DiffScope,
    DiffOptions Options);
