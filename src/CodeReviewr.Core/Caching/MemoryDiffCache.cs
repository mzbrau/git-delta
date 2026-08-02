using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Caching;

/// <summary>
/// In-memory content-addressed diff cache with a hard entry cap (LRU eviction).
/// </summary>
public sealed class MemoryDiffCache : IDiffCache
{
    /// <summary>Default maximum retained <see cref="FileDiff"/> entries.</summary>
    public const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly Dictionary<FileDiffKey, LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly object _lock = new();
    private int _hits;
    private int _misses;

    public MemoryDiffCache(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        _capacity = capacity;
    }

    public int HitCount { get { lock (_lock) return _hits; } }
    public int MissCount { get { lock (_lock) return _misses; } }
    public int Count { get { lock (_lock) return _map.Count; } }

    public bool TryGet(FileDiffKey key, out FileDiff? diff)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hits++;
                CodeReviewrMeters.CacheHits.Add(1);
                diff = node.Value.Diff;
                return true;
            }

            _misses++;
            CodeReviewrMeters.CacheMisses.Add(1);
            diff = null;
            return false;
        }
    }

    public void Set(FileDiffKey key, FileDiff diff)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new CacheEntry(key, diff);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, diff));
            _lru.AddFirst(node);
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    private sealed record CacheEntry(FileDiffKey Key, FileDiff Diff);
}
