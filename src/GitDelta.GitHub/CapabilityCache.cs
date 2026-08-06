namespace GitDelta.GitHub;

public readonly record struct CapabilityCacheKey(string Host, string Login);

public interface ICapabilityCache
{
    bool TryGet(CapabilityCacheKey key, out GitHubCapabilities capabilities);
    void Set(CapabilityCacheKey key, GitHubCapabilities capabilities);
}

public sealed class CapabilityCache : ICapabilityCache
{
    private readonly Dictionary<CapabilityCacheKey, GitHubCapabilities> _cache = new();

    public bool TryGet(CapabilityCacheKey key, out GitHubCapabilities capabilities)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            capabilities = cached;
            return true;
        }

        capabilities = new GitHubCapabilities(MarkFileAsViewed: false);
        return false;
    }

    public void Set(CapabilityCacheKey key, GitHubCapabilities capabilities) =>
        _cache[key] = capabilities;
}
