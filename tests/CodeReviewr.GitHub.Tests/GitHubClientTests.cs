using System.Net;
using System.Text;
using System.Text.Json;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.GitHub;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.GitHub.Tests;

public sealed class GitHubClientTests
{
    [TestCase("github.com", "https://api.github.com/graphql")]
    [TestCase("https://github.com", "https://api.github.com/graphql")]
    [TestCase("github.example.com", "https://github.example.com/api/graphql")]
    public void ResolveGraphQlEndpoint_NormalizesHost(string host, string expected)
    {
        Assert.That(GitHubClient.ResolveGraphQlEndpoint(host), Is.EqualTo(expected));
    }

    [TestCase("GitHub.com", "github.com")]
    [TestCase("https://github.example.com/", "github.example.com")]
    public void NormalizeHost_StripsSchemeAndTrailingSlash(string host, string expected)
    {
        Assert.That(GitHubClient.NormalizeHost(host), Is.EqualTo(expected));
    }

    [Test]
    public async Task ExecuteAsync_ParsesDataAndRateLimitFromExtensions()
    {
        var fixture = await File.ReadAllTextAsync(GetFixturePath("inbox-search-response.json"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixture, Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler);

        var (data, rateLimit) = await client.ExecuteAsync(
            "github.com",
            "ghp_test",
            EmbeddedQueries.InboxSearchQuery,
            new { query = "is:open is:pr", first = 50 });

        Assert.That(data.GetProperty("search").GetProperty("issueCount").GetInt32(), Is.EqualTo(1));
        Assert.That(rateLimit, Is.Not.Null);
        Assert.That(rateLimit!.Limit, Is.EqualTo(5000));
        Assert.That(rateLimit.Remaining, Is.EqualTo(4999));
        Assert.That(rateLimit.Used, Is.EqualTo(1));
        Assert.That(rateLimit.ResetAt, Is.Not.Null);
    }

    [Test]
    public async Task ExecuteAsync_ParsesRateLimitFromHeadersWhenExtensionsMissing()
    {
        var body = """{"data":{"viewer":{"login":"octocat","avatarUrl":"https://example.com/a"}}}""";
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("X-RateLimit-Limit", "5000");
            response.Headers.Add("X-RateLimit-Remaining", "4500");
            response.Headers.Add("X-RateLimit-Used", "500");
            response.Headers.Add("X-RateLimit-Reset", "1700000000");
            return response;
        });
        var client = CreateClient(handler);

        var (_, rateLimit) = await client.ExecuteAsync(
            "github.com", "ghp_test", EmbeddedQueries.ViewerQuery, null);

        Assert.That(rateLimit, Is.Not.Null);
        Assert.That(rateLimit!.Limit, Is.EqualTo(5000));
        Assert.That(rateLimit.Remaining, Is.EqualTo(4500));
        Assert.That(rateLimit.Used, Is.EqualTo(500));
        Assert.That(rateLimit.ResetAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1700000000)));
    }

    [Test]
    public async Task GetViewerAsync_UsesExecuteAsyncInternally()
    {
        var fixture = await File.ReadAllTextAsync(GetFixturePath("viewer-response.json"));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixture, Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler);

        var viewer = await client.GetViewerAsync("github.com", "ghp_test");

        Assert.That(viewer.Login, Is.EqualTo("octocat"));
        Assert.That(viewer.AvatarUrl, Is.EqualTo("https://avatars.githubusercontent.com/u/1"));
    }

    [Test]
    public async Task ProbeCapabilitiesAsync_FindsMarkFileAsViewed()
    {
        var viewerFixture = await File.ReadAllTextAsync(GetFixturePath("viewer-response.json"));
        var capabilityFixture = await File.ReadAllTextAsync(GetFixturePath("capability-probe-response.json"));
        var call = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            call++;
            var content = call == 1 ? viewerFixture : capabilityFixture;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            };
        });
        var cache = new CapabilityCache();
        var client = CreateClient(handler, cache);

        var capabilities = await client.ProbeCapabilitiesAsync("github.com", "ghp_test");

        Assert.That(capabilities.MarkFileAsViewed, Is.True);
        Assert.That(cache.TryGet(new CapabilityCacheKey("github.com", "octocat"), out var cached), Is.True);
        Assert.That(cached.MarkFileAsViewed, Is.True);
    }

    [Test]
    public async Task ProbeCapabilitiesAsync_ReturnsFalseOnFailure()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        var capabilities = await client.ProbeCapabilitiesAsync("github.com", "ghp_bad");

        Assert.That(capabilities.MarkFileAsViewed, Is.False);
    }

    [Test]
    public void ExecuteAsync_ThrowsGitHubApiExceptionOn401()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler);

        var ex = Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ExecuteAsync("github.com", "ghp_bad", EmbeddedQueries.ViewerQuery, null));

        Assert.That(ex!.StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task InboxSearch_ParsesPullRequestSummaries()
    {
        var fixture = await File.ReadAllTextAsync(GetFixturePath("inbox-search-response.json"));
        using var doc = JsonDocument.Parse(fixture);
        var data = doc.RootElement.GetProperty("data");

        var summaries = PullRequestGraphQLParser.ParseInboxSearch(
            data, "github.com", "reviewer", InboxSection.NeedsMyReview);

        Assert.That(summaries, Has.Count.EqualTo(1));
        var summary = summaries[0];
        Assert.That(summary.NodeId, Is.EqualTo("PR_kwDOABC123"));
        Assert.That(summary.Number, Is.EqualTo(42));
        Assert.That(summary.Title, Is.EqualTo("Fix inbox parsing"));
        Assert.That(summary.Owner, Is.EqualTo("octo"));
        Assert.That(summary.Name, Is.EqualTo("repo"));
        Assert.That(summary.NameWithOwner, Is.EqualTo("octo/repo"));
        Assert.That(summary.Section, Is.EqualTo(InboxSection.NeedsMyReview));
        Assert.That(summary.HeadOid, Is.EqualTo("def456head"));
        Assert.That(summary.BaseOid, Is.EqualTo("abc123base"));
        Assert.That(summary.AuthorLogin, Is.EqualTo("octocat"));
        Assert.That(summary.ChangedFiles, Is.EqualTo(3));
    }

    private static GitHubClient CreateClient(
        HttpMessageHandler handler,
        ICapabilityCache? cache = null) =>
        new(new HttpClient(handler), cache ?? new CapabilityCache());

    private static string GetFixturePath(string fileName) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fileName);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }
}
