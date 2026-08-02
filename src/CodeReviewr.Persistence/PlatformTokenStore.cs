using System.Runtime.InteropServices;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Persistence;

/// <summary>Selects the OS-appropriate secure token store.</summary>
public sealed class PlatformTokenStore : ITokenStore
{
    private readonly ITokenStore _inner;

    public PlatformTokenStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            _inner = new KeychainTokenStore();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _inner = new CredentialManagerTokenStore();
        else
            // Linux: no OS vault wired yet — tokens live in process memory only for the session.
            // Documented limitation until libsecret (or equivalent) is added.
            _inner = new MemoryTokenStore();
    }

    public Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default) =>
        _inner.SetTokenAsync(host, login, token, ct);

    public Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default) =>
        _inner.GetTokenAsync(host, login, ct);

    public Task DeleteTokenAsync(string host, string login, CancellationToken ct = default) =>
        _inner.DeleteTokenAsync(host, login, ct);
}
