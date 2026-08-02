using CodeReviewr.Core;

namespace CodeReviewr.GitHub;

public interface IAccountService
{
    Task<GitHubAccountSettings> AddAccountAsync(string host, string token, CancellationToken ct = default);
    Task<GitHubAccountSettings> ReauthAccountAsync(string host, string login, string token, CancellationToken ct = default);
    Task RemoveAccountAsync(string host, string login, CancellationToken ct = default);
    Task MarkNeedsReauthAsync(string host, string login, CancellationToken ct = default);
    IReadOnlyList<GitHubAccountSettings> ListAccounts();
}
