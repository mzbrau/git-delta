using System.Net;
using System.Text;
using System.Text.Json;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.GitHub;
using GitDelta.Persistence;
using GitDelta.Review;
using GitDelta.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.Review.Tests;

public sealed class PullRequestBudgetTests
{
    private static CountingGraphQlHandler CreateHandler(string baseOid, string headOid)
    {
        var detailTemplate = File.ReadAllText(FixturePath("pull-request-detail-response.json"));
        var detailJson = detailTemplate
            .Replace("abc123base000000000000000000000000000000", baseOid, StringComparison.Ordinal)
            .Replace("def456head000000000000000000000000000000", headOid, StringComparison.Ordinal);
        var threadsJson = File.ReadAllText(FixturePath("pull-request-threads-response.json"));
        return new CountingGraphQlHandler(detailJson, threadsJson);
    }

    [Test]
    public async Task Open_Pull_Request_Performs_Bounded_GraphQL()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("src/Foo.cs", "one\n")
            .WithInitialCommit("base")
            .WithFile("src/Foo.cs", "two\n")
            .WithCommit("head");
        var repoPath = repo.Build();
        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        repo.RunGit("remote", "add", "origin", repoPath);
        repo.RunGit("update-ref", "refs/pull/42/head", headOid);

        var handler = CreateHandler(baseOid, headOid);
        var summary = CreateSummary(baseOid, headOid);
        var settings = BuildSettings(repoPath);

        await using var sp = BuildServices(handler, settings);
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();

        var review = sp.GetRequiredService<IReviewService>();
        var comments = sp.GetRequiredService<IReviewCommentService>();

        handler.ResetCounts();
        var session = await review.OpenAsync(summary);
        await comments.GetThreadsAsync(session);

        Assert.That(handler.GraphQlPostCount, Is.EqualTo(2),
            "Opening a pull request should perform detail + threads GraphQL requests.");

        handler.ResetCounts();
        var file = session.Files.First().Path;
        _ = await review.GetDiffAsync(session, file, DiffOptions.Default);

        Assert.That(handler.GraphQlPostCount, Is.EqualTo(0),
            "Loading a file diff must not perform GraphQL requests.");
    }

    [Test]
    public async Task Switching_Files_Performs_Zero_GraphQL()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("base")
            .WithFile("a.txt", "two\n")
            .WithFile("b.txt", "new\n")
            .WithCommit("head");
        var repoPath = repo.Build();
        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        repo.RunGit("remote", "add", "origin", repoPath);
        repo.RunGit("update-ref", "refs/pull/7/head", headOid);

        var handler = CreateHandler(baseOid, headOid);

        var summary = CreateSummary(baseOid, headOid) with { Number = 7 };
        var settings = BuildSettings(repoPath);

        await using var sp = BuildServices(handler, settings);
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();

        var review = sp.GetRequiredService<IReviewService>();
        var session = await review.OpenAsync(summary);
        await sp.GetRequiredService<IReviewCommentService>().GetThreadsAsync(session);

        handler.ResetCounts();
        foreach (var (path, _) in session.Files)
            _ = await review.GetDiffAsync(session, path, DiffOptions.Default);

        Assert.That(handler.GraphQlPostCount, Is.EqualTo(0),
            "Switching files within an open pull request must not perform GraphQL requests.");
    }

    [Test]
    public async Task GraphQL_Requests_Do_Not_Run_On_Ui_SynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var uiContext = new TrackingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            using var repo = RepositoryBuilder.Create()
                .WithFile("src/Foo.cs", "one\n")
                .WithInitialCommit("base")
                .WithFile("src/Foo.cs", "two\n")
                .WithCommit("head");
            var repoPath = repo.Build();
            var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
            var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
            repo.RunGit("remote", "add", "origin", repoPath);
            repo.RunGit("update-ref", "refs/pull/42/head", headOid);

            var handler = CreateHandler(baseOid, headOid);
            var summary = CreateSummary(baseOid, headOid);
            var settings = BuildSettings(repoPath);

            await using var sp = BuildServices(handler, settings);
            await sp.GetRequiredService<IGitEnvironment>().DetectAsync();

            var review = sp.GetRequiredService<IReviewService>();
            var session = await review.OpenAsync(summary);
            await sp.GetRequiredService<IReviewCommentService>().GetThreadsAsync(session);

            Assert.That(handler.UiThreadPostCount, Is.EqualTo(0),
                "GraphQL requests must not be issued from the UI synchronization context.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static PullRequestSummary CreateSummary(string baseOid, string headOid) =>
        new(
            NodeId: "PR_kwDOABC123",
            Host: "github.com",
            AccountLogin: "dev",
            RepositoryNodeId: "R_kgDOABC",
            Owner: "octo",
            Name: "repo",
            NameWithOwner: "octo/repo",
            Number: 42,
            Title: "Fix inbox parsing",
            Url: "https://github.com/octo/repo/pull/42",
            IsDraft: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReviewDecision: "REVIEW_REQUIRED",
            BaseRefName: "main",
            HeadRefName: "feature/inbox",
            BaseOid: baseOid,
            HeadOid: headOid,
            AuthorLogin: "octocat",
            ChangedFiles: 1,
            Section: InboxSection.NeedsMyReview);

    private static ISettingsStore BuildSettings(string repoPath)
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings
        {
            DevelopmentFolder = Path.GetDirectoryName(repoPath)!,
            RepositoryBindings =
            [
                new RepositoryAccountBinding
                {
                    Host = "github.com",
                    Owner = "octo",
                    Name = "repo",
                    LocalPath = repoPath,
                    AccountLogin = "dev",
                },
            ],
        });
        return settings;
    }

    private static ServiceProvider BuildServices(CountingGraphQlHandler handler, ISettingsStore settings)
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetTokenAsync("github.com", "dev", Arg.Any<CancellationToken>())
            .Returns("token");

        var durable = Substitute.For<IDurableUserStore>();
        durable.ListAsync(Arg.Any<OutboxState>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        durable.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddSingleton(tokenStore);
        services.AddSingleton(durable);
        services.AddSingleton<ILocalViewedStore>(sp => sp.GetRequiredService<IDurableUserStore>());
        services.AddSingleton<ICapabilityCache, CapabilityCache>();
        services.AddSingleton<IAccountService>(Substitute.For<IAccountService>());
        services.AddSingleton<IGitHubClient>(_ =>
        {
            var httpClient = new HttpClient(handler);
            return new GitHubClient(httpClient, new CapabilityCache());
        });
        services.AddSingleton<IPullRequestService, PullRequestService>();
        services.AddGitDeltaGit();
        services.AddGitDeltaDiff();
        services.AddGitDeltaReview();
        return services.BuildServiceProvider();
    }

    private static string FixturePath(string name) =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", name);

    private sealed class CountingGraphQlHandler(string detailJson, string threadsJson) : HttpMessageHandler
    {
        private int _graphQlPostCount;
        private int _uiThreadPostCount;
        private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

        public int GraphQlPostCount => _graphQlPostCount;
        public int UiThreadPostCount => _uiThreadPostCount;

        public void ResetCounts()
        {
            _graphQlPostCount = 0;
            _uiThreadPostCount = 0;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _graphQlPostCount);
                if (Environment.CurrentManagedThreadId == _uiThreadId)
                    Interlocked.Increment(ref _uiThreadPostCount);
            }

            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            var responseJson = body.Contains("PullRequestThreads", StringComparison.Ordinal)
                ? WrapGraphQlResponse(threadsJson)
                : detailJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }

        private static string WrapGraphQlResponse(string json) =>
            json.TrimStart().StartsWith("{\"data\"", StringComparison.Ordinal)
                ? json
                : $"{{\"data\":{json}}}";
    }

    private sealed class TrackingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => d(state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
