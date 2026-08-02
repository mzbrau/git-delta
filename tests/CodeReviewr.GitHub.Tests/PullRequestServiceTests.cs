using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.GitHub.Tests;

public sealed class PullRequestServiceTests
{
    [Test]
    public async Task GetInboxAsync_DeduplicatesByNodeIdPreferringNeedsMyReview()
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        var call = 0;
        gitHubClient.ExecuteAsync(
                "github.com",
                "token",
                EmbeddedQueries.InboxSearchQuery,
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                call++;
                return call switch
                {
                    1 => (needsReviewPrData(), (GitHubRateLimit?)null),
                    2 => (sharedPrData(), (GitHubRateLimit?)null),
                    _ => (emptySearchData(), (GitHubRateLimit?)null),
                };
            });

        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync("github.com", "reviewer", Arg.Any<CancellationToken>())
            .Returns("token");

        var accountService = Substitute.For<IAccountService>();
        accountService.ListAccounts().Returns([
            new GitHubAccountSettings
            {
                Host = "github.com",
                Login = "reviewer",
                NeedsReauth = false,
            },
        ]);

        var service = new PullRequestService(gitHubClient, tokenStore, accountService);
        var inbox = await service.GetInboxAsync();

        Assert.That(inbox, Has.Count.EqualTo(1));
        Assert.That(inbox[0].NodeId, Is.EqualTo("PR_shared"));
        Assert.That(inbox[0].Section, Is.EqualTo(InboxSection.NeedsMyReview));
    }

    [Test]
    public async Task GetInboxAsync_MarksAccountNeedsReauthOn401()
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        gitHubClient.ExecuteAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<(System.Text.Json.JsonElement, GitHubRateLimit?)>>(_ =>
                throw new GitHubApiException(401, "Unauthorized"));

        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync("github.com", "reviewer", Arg.Any<CancellationToken>())
            .Returns("bad-token");

        var accountService = Substitute.For<IAccountService>();
        accountService.ListAccounts().Returns([
            new GitHubAccountSettings
            {
                Host = "github.com",
                Login = "reviewer",
                NeedsReauth = false,
            },
        ]);

        var service = new PullRequestService(gitHubClient, tokenStore, accountService);
        var inbox = await service.GetInboxAsync();

        Assert.That(inbox, Is.Empty);
        await accountService.Received(1)
            .MarkNeedsReauthAsync("github.com", "reviewer", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetInboxAsync_SkipsAccountsThatNeedReauth()
    {
        var gitHubClient = Substitute.For<IGitHubClient>();
        var tokenStore = Substitute.For<ITokenStore>();
        var accountService = Substitute.For<IAccountService>();
        accountService.ListAccounts().Returns([
            new GitHubAccountSettings
            {
                Host = "github.com",
                Login = "reviewer",
                NeedsReauth = true,
            },
        ]);

        var service = new PullRequestService(gitHubClient, tokenStore, accountService);
        var inbox = await service.GetInboxAsync();

        Assert.That(inbox, Is.Empty);
        await gitHubClient.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default, default);
        await tokenStore.DidNotReceiveWithAnyArgs().GetTokenAsync(default!, default!, default);
    }

    private static System.Text.Json.JsonElement needsReviewPrData()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {
              "search": {
                "issueCount": 1,
                "nodes": [{
                  "id": "PR_shared",
                  "number": 1,
                  "title": "Test",
                  "url": "https://github.com/octo/repo/pull/1",
                  "isDraft": false,
                  "createdAt": "2026-01-10T10:30:00Z",
                  "updatedAt": "2026-01-15T10:30:00Z",
                  "reviewDecision": null,
                  "baseRefName": "main",
                  "headRefName": "feature",
                  "changedFiles": 1,
                  "baseRefOid": null,
                  "headRefOid": null,
                  "author": { "login": "octocat", "avatarUrl": "https://example.com/a" },
                  "repository": {
                    "id": "R_kg",
                    "name": "repo",
                    "nameWithOwner": "octo/repo",
                    "owner": { "login": "octo" },
                    "url": "https://github.com/octo/repo"
                  },
                  "commits": { "nodes": [] }
                }]
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static System.Text.Json.JsonElement sharedPrData()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {
              "search": {
                "issueCount": 1,
                "nodes": [{
                  "id": "PR_shared",
                  "number": 1,
                  "title": "Test",
                  "url": "https://github.com/octo/repo/pull/1",
                  "isDraft": false,
                  "createdAt": "2026-01-10T10:30:00Z",
                  "updatedAt": "2026-01-14T10:30:00Z",
                  "reviewDecision": "APPROVED",
                  "baseRefName": "main",
                  "headRefName": "feature",
                  "changedFiles": 1,
                  "baseRefOid": null,
                  "headRefOid": null,
                  "author": { "login": "octocat", "avatarUrl": "https://example.com/a" },
                  "repository": {
                    "id": "R_kg",
                    "name": "repo",
                    "nameWithOwner": "octo/repo",
                    "owner": { "login": "octo" },
                    "url": "https://github.com/octo/repo"
                  },
                  "commits": { "nodes": [] }
                }]
              }
            }
            """);
        return doc.RootElement.Clone();
    }

    private static System.Text.Json.JsonElement emptySearchData()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "search": { "issueCount": 0, "nodes": [] } }
            """);
        return doc.RootElement.Clone();
    }
}
