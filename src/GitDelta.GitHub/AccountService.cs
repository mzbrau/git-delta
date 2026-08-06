using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.GitHub;

public sealed class AccountService(
    IGitHubClient gitHubClient,
    ITokenStore tokenStore,
    ISettingsStore settingsStore) : IAccountService
{
    public async Task<GitHubAccountSettings> AddAccountAsync(string host, string token, CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        var viewer = await gitHubClient.GetViewerAsync(normalizedHost, token, ct).ConfigureAwait(false);

        await tokenStore.SetTokenAsync(normalizedHost, viewer.Login, token, ct).ConfigureAwait(false);

        var account = new GitHubAccountSettings
        {
            Host = normalizedHost,
            Login = viewer.Login,
            AvatarUrl = viewer.AvatarUrl,
            NeedsReauth = false,
        };

        settingsStore.Update(s =>
        {
            s.Accounts.RemoveAll(a =>
                string.Equals(a.Host, account.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, account.Login, StringComparison.OrdinalIgnoreCase));
            s.Accounts.Add(account);
        });

        await settingsStore.SaveAsync(ct).ConfigureAwait(false);
        return account;
    }

    public async Task<GitHubAccountSettings> ReauthAccountAsync(
        string host,
        string login,
        string token,
        CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);
        var viewer = await gitHubClient.GetViewerAsync(normalizedHost, token, ct).ConfigureAwait(false);
        if (!string.Equals(viewer.Login, login, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Token belongs to {viewer.Login}, not {login}. Add a new account instead.");
        }

        await tokenStore.SetTokenAsync(normalizedHost, login, token, ct).ConfigureAwait(false);

        var account = new GitHubAccountSettings
        {
            Host = normalizedHost,
            Login = login,
            AvatarUrl = viewer.AvatarUrl,
            NeedsReauth = false,
        };

        settingsStore.Update(s =>
        {
            var index = s.Accounts.FindIndex(a =>
                string.Equals(a.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                s.Accounts[index] = account;
        });

        await settingsStore.SaveAsync(ct).ConfigureAwait(false);
        return account;
    }

    public async Task RemoveAccountAsync(string host, string login, CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);

        await tokenStore.DeleteTokenAsync(normalizedHost, login, ct).ConfigureAwait(false);

        settingsStore.Update(s =>
        {
            s.Accounts.RemoveAll(a =>
                string.Equals(a.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            s.RepositoryBindings.RemoveAll(b =>
                string.Equals(b.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.AccountLogin, login, StringComparison.OrdinalIgnoreCase));
        });

        await settingsStore.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkNeedsReauthAsync(string host, string login, CancellationToken ct = default)
    {
        var normalizedHost = GitHubClient.NormalizeHost(host);

        settingsStore.Update(s =>
        {
            var account = s.Accounts.FirstOrDefault(a =>
                string.Equals(a.Host, normalizedHost, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
            if (account is null)
                return;

            var index = s.Accounts.IndexOf(account);
            s.Accounts[index] = account with { NeedsReauth = true };
        });

        await settingsStore.SaveAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<GitHubAccountSettings> ListAccounts() =>
        settingsStore.Current.Accounts;
}
