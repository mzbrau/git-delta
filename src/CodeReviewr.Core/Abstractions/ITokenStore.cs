namespace CodeReviewr.Core.Abstractions;

public interface ITokenStore
{
    Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default);
    Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default);
    Task DeleteTokenAsync(string host, string login, CancellationToken ct = default);
}
