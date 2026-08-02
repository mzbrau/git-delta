using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.GitHub.Tests;

public sealed class AccountServiceTests
{
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

        var settingsStore = Substitute.For<ISettingsStore>();
        settingsStore.Current.Returns(settings);
        settingsStore
            .When(s => s.Update(Arg.Any<Action<AppSettings>>()))
            .Do(call =>
            {
                var mutate = call.Arg<Action<AppSettings>>();
                mutate(settings);
            });

        var service = new AccountService(
            Substitute.For<IGitHubClient>(),
            Substitute.For<ITokenStore>(),
            settingsStore);

        await service.MarkNeedsReauthAsync("github.com", "octocat");

        Assert.That(settings.Accounts[0].NeedsReauth, Is.True);
        await settingsStore.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }
}
