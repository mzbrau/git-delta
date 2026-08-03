using System.Net;
using System.Text;
using System.Text.Json;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Git;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;
using CodeReviewr.Review;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class ReviewOutboxTests
{
    [Test]
    public async Task DrainAsync_SkipsSubmitReview()
    {
        var executed = new List<string>();
        var services = BuildServices(request => CaptureMutation(request, executed));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var outbox = provider.GetRequiredService<IReviewOutbox>();
        var store = provider.GetRequiredService<IDurableUserStore>();

        await store.EnqueueAsync(CreateEntry(OutboxKind.MarkFileViewed, "mark"));
        await store.EnqueueAsync(CreateEntry(OutboxKind.SubmitReview, "submit"));

        await outbox.DrainAsync();

        Assert.That(executed, Has.Count.EqualTo(1));
        Assert.That(executed[0], Does.Contain("markFileAsViewed").IgnoreCase);
        Assert.That(await store.ListAsync(OutboxState.Pending), Has.Count.EqualTo(1));
        Assert.That((await store.ListAsync(OutboxState.Pending))[0].Kind, Is.EqualTo(OutboxKind.SubmitReview));
    }

    [Test]
    public async Task DrainSubmitAsync_ExecutesSubmitOnly()
    {
        var executed = new List<string>();
        var services = BuildServices(request => CaptureMutation(request, executed));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var outbox = provider.GetRequiredService<IReviewOutbox>();
        var store = provider.GetRequiredService<IDurableUserStore>();

        var submit = CreateEntry(OutboxKind.SubmitReview, "submit");
        await store.EnqueueAsync(submit);

        await outbox.DrainSubmitAsync(submit.Id);

        Assert.That(executed.Any(q => q.Contains("submitPullRequestReview", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(await store.ListAsync(OutboxState.Pending), Is.Empty);
    }

    [Test]
    public async Task SubmitReview_ThrowsHeadMovedException()
    {
        var services = BuildServices(_ => PendingReviewHeadMovedResponse());
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var executor = provider.GetRequiredService<ReviewMutationExecutor>();
        var entry = CreateEntry(OutboxKind.SubmitReview, "submit");

        var ex = Assert.ThrowsAsync<HeadMovedException>(() =>
            executor.ExecuteOutboxEntryAsync(entry, CancellationToken.None));
        Assert.That(ex!.ExpectedSha, Is.EqualTo("deadbeef"));
        Assert.That(ex.ActualSha, Is.EqualTo("cafebabe"));
    }

    [Test]
    public async Task MarkFileViewed_WhenCapabilityFalse_UsesLocalStoreOnly()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("token");

        var gitHub = Substitute.For<IGitHubClient>();
        gitHub.ProbeCapabilitiesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubCapabilities(MarkFileAsViewed: false));

        var path = Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");
        var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();

        var outbox = Substitute.For<IReviewOutbox>();
        var comments = new ReviewCommentService(
            gitHub,
            tokenStore,
            new CapabilityCache(),
            outbox,
            durable,
            new CommentAnchorMapper(
                Substitute.For<IGitProcessRunner>(),
                Substitute.For<IRepositoryGateProvider>(),
                Substitute.For<IGitObjectReader>()));

        var session = CreateSession();
        await comments.MarkFileViewedAsync(session, new Core.FilePath("src/a.cs"));

        await outbox.DidNotReceive().EnqueueAsync(Arg.Any<OutboxEntry>(), Arg.Any<CancellationToken>());
        Assert.That(await durable.IsViewedAsync(session.Detail.Summary.NodeId, "src/a.cs"), Is.True);
    }

    [Test]
    public async Task MarkFileViewed_WhenCapabilityTrue_WritesLocalAndEnqueues()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("token");

        var gitHub = Substitute.For<IGitHubClient>();
        gitHub.ProbeCapabilitiesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubCapabilities(MarkFileAsViewed: true));

        var path = Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");
        var durable = new SqliteDurableUserStore(path);
        durable.EnsureSchema();

        var outbox = Substitute.For<IReviewOutbox>();
        var comments = new ReviewCommentService(
            gitHub,
            tokenStore,
            new CapabilityCache(),
            outbox,
            durable,
            new CommentAnchorMapper(
                Substitute.For<IGitProcessRunner>(),
                Substitute.For<IRepositoryGateProvider>(),
                Substitute.For<IGitObjectReader>()));

        var session = CreateSession();
        await comments.MarkFileViewedAsync(session, new Core.FilePath("src/a.cs"));

        await outbox.Received(1).EnqueueAsync(
            Arg.Is<OutboxEntry>(e => e.Kind == OutboxKind.MarkFileViewed),
            Arg.Any<CancellationToken>());
        Assert.That(await durable.IsViewedAsync(session.Detail.Summary.NodeId, "src/a.cs"), Is.True);
    }

    [Test]
    public async Task MarkFileViewed_Mutation_Omits_CommitOid()
    {
        string? requestBody = null;
        var services = BuildServices(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(requestBody);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";
            return MutationSuccess(query);
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var executor = provider.GetRequiredService<ReviewMutationExecutor>();
        var entry = new OutboxEntry(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            OutboxKind.MarkFileViewed,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<MarkFileViewedPayload>(
                "acme",
                "demo",
                1,
                new MarkFileViewedPayload("src/a.cs")), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

        await executor.ExecuteOutboxEntryAsync(entry, CancellationToken.None);

        Assert.That(requestBody, Is.Not.Null);
        using var body = JsonDocument.Parse(requestBody!);
        Assert.That(body.RootElement.GetProperty("query").GetString(), Does.Contain("markFileAsViewed").IgnoreCase);
        var input = body.RootElement.GetProperty("variables").GetProperty("input");
        Assert.That(input.GetProperty("path").GetString(), Is.EqualTo("src/a.cs"));
        Assert.That(input.GetProperty("pullRequestId").GetString(), Is.EqualTo("PR_NODE"));
        Assert.That(input.TryGetProperty("commitOid", out _), Is.False);
    }

    [Test]
    public async Task UnmarkFileViewed_Mutation_Omits_CommitOid()
    {
        string? requestBody = null;
        var services = BuildServices(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(requestBody);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";
            return MutationSuccess(query);
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var executor = provider.GetRequiredService<ReviewMutationExecutor>();
        var entry = new OutboxEntry(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            OutboxKind.UnmarkFileViewed,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<UnmarkFileViewedPayload>(
                "acme",
                "demo",
                1,
                new UnmarkFileViewedPayload("src/a.cs")), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

        await executor.ExecuteOutboxEntryAsync(entry, CancellationToken.None);

        Assert.That(requestBody, Is.Not.Null);
        using var body = JsonDocument.Parse(requestBody!);
        Assert.That(body.RootElement.GetProperty("query").GetString(), Does.Contain("unmarkFileAsViewed").IgnoreCase);
        var input = body.RootElement.GetProperty("variables").GetProperty("input");
        Assert.That(input.GetProperty("path").GetString(), Is.EqualTo("src/a.cs"));
        Assert.That(input.GetProperty("pullRequestId").GetString(), Is.EqualTo("PR_NODE"));
        Assert.That(input.TryGetProperty("commitOid", out _), Is.False);
    }

    [Test]
    public async Task AddComment_CreatesPendingReviewWithoutEventPending()
    {
        var requests = new List<string>();
        var services = BuildServices(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add(body);
            using var doc = JsonDocument.Parse(body);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";

            if (query.Contains("PendingReviewQuery", StringComparison.Ordinal))
            {
                return JsonOk("""
                    {
                      "data": {
                        "repository": {
                          "pullRequest": {
                            "id": "PR_1",
                            "headRefOid": "deadbeef",
                            "reviews": { "nodes": [] }
                          }
                        }
                      }
                    }
                    """);
            }

            if (query.Contains("addPullRequestReview", StringComparison.Ordinal) &&
                !query.Contains("addPullRequestReviewThread", StringComparison.Ordinal) &&
                !query.Contains("addPullRequestReviewComment", StringComparison.Ordinal))
            {
                Assert.That(body, Does.Not.Contain("PENDING"));
                using var vars = JsonDocument.Parse(body);
                if (vars.RootElement.TryGetProperty("variables", out var variables) &&
                    variables.TryGetProperty("input", out var input))
                {
                    Assert.That(input.TryGetProperty("event", out _), Is.False);
                }

                return JsonOk("""
                    {
                      "data": {
                        "addPullRequestReview": {
                          "pullRequestReview": { "id": "RV_NEW" }
                        }
                      }
                    }
                    """);
            }

            if (query.Contains("addPullRequestReviewThread", StringComparison.Ordinal))
            {
                Assert.That(body, Does.Contain("RV_NEW"));
                return JsonOk("""{ "data": { "addPullRequestReviewThread": { "thread": { "id": "TH_1" } } } }""");
            }

            return MutationSuccess(query);
        });

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var outbox = provider.GetRequiredService<IReviewOutbox>();
        await outbox.EnqueueAsync(CreateAddCommentEntry());

        Assert.That(requests.Any(r => r.Contains("addPullRequestReview", StringComparison.Ordinal)), Is.True);
        Assert.That(await provider.GetRequiredService<IDurableUserStore>().ListAsync(OutboxState.Pending), Is.Empty);
        Assert.That(await provider.GetRequiredService<IDurableUserStore>().ListAsync(OutboxState.Failed), Is.Empty);
    }

    [Test]
    public async Task ReplyComment_UsesThreadReplyMutation()
    {
        string? requestBody = null;
        var services = BuildServices(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";
            if (query.Contains("addPullRequestReviewThreadReply", StringComparison.Ordinal))
                requestBody = body;
            return MutationSuccess(query);
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var executor = provider.GetRequiredService<ReviewMutationExecutor>();
        var entry = new OutboxEntry(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            OutboxKind.ReplyComment,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<ReplyCommentPayload>(
                "acme",
                "demo",
                1,
                new ReplyCommentPayload(Guid.NewGuid().ToString("N"), "TH_1", "thanks")), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

        await executor.ExecuteOutboxEntryAsync(entry, CancellationToken.None);

        Assert.That(requestBody, Is.Not.Null);
        using var parsed = JsonDocument.Parse(requestBody!);
        Assert.That(
            parsed.RootElement.GetProperty("query").GetString(),
            Does.Contain("addPullRequestReviewThreadReply"));
        var input = parsed.RootElement.GetProperty("variables").GetProperty("input");
        Assert.That(input.GetProperty("pullRequestReviewThreadId").GetString(), Is.EqualTo("TH_1"));
        Assert.That(input.GetProperty("body").GetString(), Is.EqualTo("thanks"));
        Assert.That(input.GetProperty("pullRequestReviewId").GetString(), Is.EqualTo("RV_1"));
        Assert.That(input.TryGetProperty("inReplyTo", out _), Is.False);
    }

    [Test]
    public async Task AddComment_FileLevel_SendsSubjectTypeFileAndOmitsLine()
    {
        string? requestBody = null;
        var services = BuildServices(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";
            if (query.Contains("addPullRequestReviewThread", StringComparison.Ordinal) &&
                !query.Contains("addPullRequestReviewThreadReply", StringComparison.Ordinal))
            {
                requestBody = body;
            }

            return MutationSuccess(query);
        });
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var executor = provider.GetRequiredService<ReviewMutationExecutor>();
        var entry = new OutboxEntry(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            OutboxKind.AddComment,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<AddCommentPayload>(
                "acme",
                "demo",
                1,
                new AddCommentPayload(
                    Guid.NewGuid().ToString("N"),
                    "src/a.cs",
                    Line: null,
                    StartLine: null,
                    Side: "RIGHT",
                    Body: "file note",
                    HeadSha: "deadbeef")), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

        await executor.ExecuteOutboxEntryAsync(entry, CancellationToken.None);

        Assert.That(requestBody, Is.Not.Null);
        using var parsed = JsonDocument.Parse(requestBody!);
        var input = parsed.RootElement.GetProperty("variables").GetProperty("input");
        Assert.That(input.GetProperty("subjectType").GetString(), Is.EqualTo("FILE"));
        Assert.That(input.GetProperty("path").GetString(), Is.EqualTo("src/a.cs"));
        Assert.That(input.GetProperty("body").GetString(), Is.EqualTo("file note"));
        Assert.That(input.TryGetProperty("line", out _), Is.False);
        Assert.That(input.TryGetProperty("side", out _), Is.False);
        Assert.That(input.TryGetProperty("startLine", out _), Is.False);
    }

    [Test]
    public async Task EnqueueAsync_PermanentGraphQlFailure_ThrowsAndMarksFailed()
    {
        var services = BuildServices(_ => JsonOk("""
            {
              "errors": [ { "message": "Value `PENDING` does not exist in enum" } ]
            }
            """));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();

        var outbox = provider.GetRequiredService<IReviewOutbox>();
        var store = provider.GetRequiredService<IDurableUserStore>();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            outbox.EnqueueAsync(CreateEntry(OutboxKind.MarkFileViewed, "mark")));
        Assert.That(ex!.Message, Does.Contain("PENDING"));
        Assert.That(await store.ListAsync(OutboxState.Failed), Has.Count.EqualTo(1));
    }

    private static ServiceCollection BuildServices(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("token");

        var services = new ServiceCollection();
        services.AddSingleton<ITokenStore>(tokenStore);
        services.AddSingleton<ICapabilityCache, CapabilityCache>();
        services.AddCodeReviewrGit();
        services.AddSingleton<IDurableUserStore>(_ => new SqliteDurableUserStore(CreateDbPath()));
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        services.AddSingleton<ILocalViewedStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        services.AddSingleton<IGitHubClient>(sp =>
            new GitHubClient(new HttpClient(new StubHandler(responder)), sp.GetRequiredService<ICapabilityCache>()));
        services.AddSingleton<CommentAnchorMapper>();
        services.AddSingleton<ReviewMutationExecutor>();
        services.AddSingleton<IReviewOutbox, ReviewOutbox>();
        return services;
    }

    private static HttpResponseMessage CaptureMutation(HttpRequestMessage request, List<string> executed)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("query").GetString() ?? "";
        executed.Add(query);
        return MutationSuccess(query);
    }

    private static HttpResponseMessage PendingReviewHeadMovedResponse()
    {
        var body = """
            {
              "data": {
                "repository": {
                  "pullRequest": {
                    "id": "PR_1",
                    "headRefOid": "cafebabe",
                    "reviews": { "nodes": [ { "id": "RV_1", "state": "PENDING" } ] }
                  }
                }
              }
            }
            """;
        return JsonOk(body);
    }

    private static HttpResponseMessage MutationSuccess(string query)
    {
        if (query.Contains("PendingReviewQuery", StringComparison.Ordinal))
        {
            return JsonOk("""
                {
                  "data": {
                    "repository": {
                      "pullRequest": {
                        "id": "PR_1",
                        "headRefOid": "deadbeef",
                        "reviews": { "nodes": [ { "id": "RV_1", "state": "PENDING" } ] }
                      }
                    }
                  }
                }
                """);
        }

        return JsonOk("""{ "data": { "ok": true } }""");
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static OutboxEntry CreateAddCommentEntry() =>
        new(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            OutboxKind.AddComment,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<AddCommentPayload>(
                "acme",
                "demo",
                1,
                new AddCommentPayload(
                    Guid.NewGuid().ToString("N"),
                    "src/a.cs",
                    10,
                    null,
                    "RIGHT",
                    "looks good",
                    "deadbeef")), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

    private static OutboxEntry CreateEntry(OutboxKind kind, string marker) =>
        new(
            Guid.NewGuid().ToString("N"),
            "github.com",
            "dev",
            "PR_NODE",
            kind,
            JsonSerializer.Serialize(new OutboxPayloadEnvelope<object>(
                "acme",
                "demo",
                1,
                kind switch
                {
                    OutboxKind.SubmitReview => (object)new SubmitReviewPayload(
                        nameof(SubmitReviewEvent.Approve),
                        null,
                        "deadbeef"),
                    OutboxKind.MarkFileViewed => (object)new MarkFileViewedPayload("src/a.cs"),
                    _ => (object)new { body = marker },
                }), ReviewJson.Options),
            DateTimeOffset.UtcNow,
            0,
            null,
            OutboxState.Pending);

    private static ReviewSession CreateSession() =>
        new(
            "/tmp/repo",
            new GitHub.PullRequestDetail(
                new GitHub.PullRequestSummary(
                    "PR_NODE",
                    "github.com",
                    "dev",
                    "R",
                    "acme",
                    "demo",
                    "acme/demo",
                    1,
                    "Title",
                    "https://example.com",
                    false,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    "main",
                    "feature",
                    null,
                    null,
                    "dev",
                    1,
                    GitHub.InboxSection.NeedsMyReview),
                null,
                [],
                null),
            new Core.CommitId("base"),
            new Core.CommitId("head"),
            Substitute.For<IReviewTree>(),
            []);

    private static string CreateDbPath() =>
        Path.Combine(Path.GetTempPath(), "CodeReviewr.Tests", Guid.NewGuid().ToString("N"), "durable.db");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
