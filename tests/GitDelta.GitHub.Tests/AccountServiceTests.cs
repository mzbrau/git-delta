using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.GitHub.Tests;

public sealed class AccountServiceTests
{
    private static ISettingsStore CreateSettingsStore(AppSettings settings)
    {
        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.Current.Returns(settings);
        settingsStore
            .When(s => s.Update(Arg.Any<Action<AppSettings>>()))
            .Do(call =>
            {
                var mutate = call.Arg<Action<AppSettings>>();
                mutate(settings);
            });
        return settingsStore;
    }

    [Test]
    public async Task MarkNeedsReauthAsync_SetsNeedsReauthFlag()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new GitHubAccountSettings
                {
                    Host = "github.com",
                    Login = "octocat",
                    NeedsReauth = false,
                },
            ],
        };

        var settingsStore = CreateSettingsStore(settings);
        var service = new AccountService(
            Substitute.For<IGitHubClient>(),
            Substitute.For<ITokenStore>(),
            settingsStore);

        await service.MarkNeedsReauthAsync("github.com", "octocat");

        Assert.That(settings.Accounts[0].NeedsReauth, Is.True);
        await settingsStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddAccountAsync_Stores_Token_And_Account()
    {
        var settings = new AppSettings();
        var settingsStore = CreateSettingsStore(settings);
        var client = Substitute.For<IGitHubClient>();
        var tokens = Substitute.For<ITokenStore>();
        client.GetViewerAsync("github.com", "tok", Arg.Any<CancellationToken>())
            .Returns(new GitHubViewer("octocat", "https://example.com/a.png"));

        var service = new AccountService(client, tokens, settingsStore);
        var account = await service.AddAccountAsync("github.com", "tok");

        Assert.That(account.Login, Is.EqualTo("octocat"));
        Assert.That(account.Host, Is.EqualTo("github.com"));
        Assert.That(account.NeedsReauth, Is.False);
        Assert.That(settings.Accounts, Has.Count.EqualTo(1));
        Assert.That(settings.Accounts[0].Login, Is.EqualTo("octocat"));
        await tokens.Received(1).SetTokenAsync("github.com", "octocat", "tok", Arg.Any<CancellationToken>());
        await settingsStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReauthAccountAsync_Updates_Token_When_Login_Matches()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new GitHubAccountSettings
                {
                    Host = "github.com",
                    Login = "octocat",
                    AvatarUrl = "old",
                    NeedsReauth = true,
                },
            ],
        };
        var settingsStore = CreateSettingsStore(settings);
        var client = Substitute.For<IGitHubClient>();
        var tokens = Substitute.For<ITokenStore>();
        client.GetViewerAsync("github.com", "new-tok", Arg.Any<CancellationToken>())
            .Returns(new GitHubViewer("octocat", "https://example.com/new.png"));

        var service = new AccountService(client, tokens, settingsStore);
        var account = await service.ReauthAccountAsync("github.com", "octocat", "new-tok");

        Assert.That(account.NeedsReauth, Is.False);
        Assert.That(account.AvatarUrl, Is.EqualTo("https://example.com/new.png"));
        Assert.That(settings.Accounts[0].NeedsReauth, Is.False);
        await tokens.Received(1).SetTokenAsync("github.com", "octocat", "new-tok", Arg.Any<CancellationToken>());
        await settingsStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void ReauthAccountAsync_Rejects_Token_For_Different_Login()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new GitHubAccountSettings { Host = "github.com", Login = "octocat" },
            ],
        };
        var settingsStore = CreateSettingsStore(settings);
        var client = Substitute.For<IGitHubClient>();
        client.GetViewerAsync("github.com", "tok", Arg.Any<CancellationToken>())
            .Returns(new GitHubViewer("other", ""));

        var service = new AccountService(client, Substitute.For<ITokenStore>(), settingsStore);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReauthAccountAsync("github.com", "octocat", "tok"));
    }

    [Test]
    public async Task RemoveAccountAsync_Deletes_Token_Account_And_Bindings()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new GitHubAccountSettings { Host = "github.com", Login = "octocat" },
                new GitHubAccountSettings { Host = "github.com", Login = "keeper" },
            ],
            RepositoryBindings =
            [
                new RepositoryAccountBinding
                {
                    Host = "github.com",
                    Owner = "acme",
                    Name = "demo",
                    LocalPath = "/tmp/demo",
                    AccountLogin = "octocat",
                },
                new RepositoryAccountBinding
                {
                    Host = "github.com",
                    Owner = "acme",
                    Name = "keep",
                    LocalPath = "/tmp/keep",
                    AccountLogin = "keeper",
                },
            ],
        };
        var settingsStore = CreateSettingsStore(settings);
        var tokens = Substitute.For<ITokenStore>();

        var service = new AccountService(Substitute.For<IGitHubClient>(), tokens, settingsStore);
        await service.RemoveAccountAsync("github.com", "octocat");

        Assert.That(settings.Accounts.Select(a => a.Login), Is.EqualTo(new[] { "keeper" }));
        Assert.That(settings.RepositoryBindings.Select(b => b.Name), Is.EqualTo(new[] { "keep" }));
        await tokens.Received(1).DeleteTokenAsync("github.com", "octocat", Arg.Any<CancellationToken>());
        await settingsStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }
}
