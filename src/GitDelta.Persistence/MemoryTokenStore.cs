using GitDelta.Core.Abstractions;

namespace GitDelta.Persistence;

/// <summary>In-memory token store for tests and non-secure environments.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default)
    {
        lock (_lock)
            _tokens[MakeKey(host, login)] = token;
        return Task.CompletedTask;
    }

    public Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _tokens.TryGetValue(MakeKey(host, login), out var token);
            return Task.FromResult(token);
        }
    }

    public Task DeleteTokenAsync(string host, string login, CancellationToken ct = default)
    {
        lock (_lock)
            _tokens.Remove(MakeKey(host, login));
        return Task.CompletedTask;
    }

    internal static string MakeKey(string host, string login) => $"{host}|{login}";
}
