using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.GitHub;
using CodeReviewr.Review;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class ReviewServiceTests
{
    [Test]
    public async Task OpenAsync_Builds_Session_From_Local_Commits()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("base")
            .WithFile("a.txt", "two\n")
            .WithCommit("head");
        var repoPath = repo.Build();
        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        repo.RunGit("remote", "add", "origin", repoPath);
        repo.RunGit("update-ref", "refs/pull/42/head", headOid);

        var summary = new PullRequestSummary(
            NodeId: "PR_1",
            Host: "github.com",
            AccountLogin: "dev",
            RepositoryNodeId: "R_1",
            Owner: "acme",
            Name: "demo",
            NameWithOwner: "acme/demo",
            Number: 42,
            Title: "Demo PR",
            Url: "https://github.com/acme/demo/pull/42",
            IsDraft: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReviewDecision: null,
            BaseRefName: "main",
            HeadRefName: "feature",
            BaseOid: baseOid,
            HeadOid: headOid,
            AuthorLogin: "dev",
            ChangedFiles: 1,
            Section: InboxSection.NeedsMyReview);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPullRequestAsync(
                "github.com", "dev", "acme", "demo", 42, Arg.Any<CancellationToken>())
            .Returns(new PullRequestDetail(summary, null, [], null));

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings
        {
            DevelopmentFolder = Path.GetDirectoryName(repoPath)!,
            RepositoryBindings =
            [
                new RepositoryAccountBinding
                {
                    Host = "github.com",
                    Owner = "acme",
                    Name = "demo",
                    LocalPath = repoPath,
                    AccountLogin = "dev",
                },
            ],
        });

        await using var sp = BuildServices(pullRequests, settings);
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();

        var review = sp.GetRequiredService<IReviewService>();
        var session = await review.OpenAsync(summary);

        Assert.That(session.RepositoryPath, Is.EqualTo(repoPath));
        Assert.That(session.MergeBase.Value, Is.EqualTo(baseOid));
        Assert.That(session.Files.Any(f => f.Path.Value == "a.txt"), Is.True);

        var diff = await review.GetDiffAsync(session, FilePath.From("a.txt"), DiffOptions.Default);
        Assert.That(diff.Hunks.Count, Is.GreaterThan(0));
    }

    private static ServiceProvider BuildServices(IPullRequestService pullRequests, ISettingsStore settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddSingleton(pullRequests);
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        services.AddCodeReviewrReview();
        return services.BuildServiceProvider();
    }
}
