using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Core.Caching;

public sealed class MemoryDiffCache : IDiffCache
{
    private readonly Dictionary<FileDiffKey, FileDiff> _cache = new();
    private readonly object _lock = new();
    private int _hits;
    private int _misses;

    public int HitCount { get { lock (_lock) return _hits; } }
    public int MissCount { get { lock (_lock) return _misses; } }

    public bool TryGet(FileDiffKey key, out FileDiff? diff)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out diff))
            {
                _hits++;
                CodeReviewrMeters.CacheHits.Add(1);
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
            _cache[key] = diff;
    }
}
