using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.GitHub;
using CodeReviewr.Review;
using CodeReviewr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.Review.Tests;

public sealed class CommentAnchorMapperTests
{
    [Test]
    public async Task MapThreadAsync_RIGHT_side_uses_new_content_and_line()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "one\n")
            .WithInitialCommit("base")
            .WithFile("a.txt", "one\ntwo\nthree\n")
            .WithCommit("head");
        var repoPath = repo.Build();
        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        var newBlob = repo.RunGit("rev-parse", $"{headOid}:a.txt").Trim();
        var oldBlob = repo.RunGit("rev-parse", $"{baseOid}:a.txt").Trim();

        await using var sp = BuildServices(repoPath);
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var mapper = sp.GetRequiredService<CommentAnchorMapper>();
        var session = BuildSession(repoPath, baseOid, headOid);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(baseOid), CommitId.FromSha(headOid)),
            FilePath.From("a.txt"),
            FilePath.From("a.txt"),
            ChangeKind.Modified,
            ContentId.FromSha(oldBlob),
            ContentId.FromSha(newBlob),
            false,
            [],
            string.Empty);

        var thread = new ReviewThread(
            "T1", "a.txt", 2, null, false, false,
            [],
            Side: DiffSide.New,
            CommitOid: headOid,
            OriginalCommitOid: headOid,
            DiffHunk: "@@ hidden");

        var mapped = await mapper.MapThreadAsync(session, thread, FilePath.From("a.txt"), fileDiff);

        Assert.That(mapped.IsUnplaceable, Is.False);
        Assert.That(mapped.Anchor, Is.Not.Null);
        Assert.That(mapped.Anchor!.Value.Start.Side, Is.EqualTo(DiffSide.New));
        Assert.That(mapped.Anchor.Value.Start.Content.Value, Is.EqualTo(newBlob));
        Assert.That(mapped.Anchor.Value.Start.Line, Is.EqualTo(2));
    }

    [Test]
    public async Task MapThreadAsync_LEFT_side_uses_old_content_and_line()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "alpha\nbeta\n")
            .WithInitialCommit("base")
            .WithFile("a.txt", "alpha\nbeta\ngamma\n")
            .WithCommit("head");
        var repoPath = repo.Build();
        var baseOid = repo.RunGit("rev-parse", "HEAD~1").Trim();
        var headOid = repo.RunGit("rev-parse", "HEAD").Trim();
        var newBlob = repo.RunGit("rev-parse", $"{headOid}:a.txt").Trim();
        var oldBlob = repo.RunGit("rev-parse", $"{baseOid}:a.txt").Trim();

        await using var sp = BuildServices(repoPath);
        await sp.GetRequiredService<IGitEnvironment>().DetectAsync();
        var mapper = sp.GetRequiredService<CommentAnchorMapper>();
        var session = BuildSession(repoPath, baseOid, headOid);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(baseOid), CommitId.FromSha(headOid)),
            FilePath.From("a.txt"),
            FilePath.From("a.txt"),
            ChangeKind.Modified,
            ContentId.FromSha(oldBlob),
            ContentId.FromSha(newBlob),
            false,
            [],
            string.Empty);

        var thread = new ReviewThread(
            "T2", "a.txt", 1, null, false, false,
            [],
            Side: DiffSide.Old,
            CommitOid: baseOid,
            OriginalCommitOid: baseOid);

        var mapped = await mapper.MapThreadAsync(session, thread, FilePath.From("a.txt"), fileDiff);

        Assert.That(mapped.Anchor!.Value.Start.Side, Is.EqualTo(DiffSide.Old));
        Assert.That(mapped.Anchor.Value.Start.Content.Value, Is.EqualTo(oldBlob));
        Assert.That(mapped.Anchor.Value.Start.Line, Is.EqualTo(1));
    }

    private static ReviewSession BuildSession(string repoPath, string baseOid, string headOid)
    {
        var summary = new PullRequestSummary(
            "PR", "github.com", "dev", "R", "acme", "demo", "acme/demo", 1, "t", "u",
            false, DateTimeOffset.UtcNow, null, "main", "feature", baseOid, headOid, "dev", 1,
            InboxSection.NeedsMyReview);
        var detail = new PullRequestDetail(summary, null, [], null);
        return new ReviewSession(
            repoPath,
            detail,
            CommitId.FromSha(baseOid),
            CommitId.FromSha(headOid),
            Substitute.For<IReviewTree>(),
            []);
    }

    private static ServiceProvider BuildServices(string repoPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IPullRequestService>());
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        services.AddCodeReviewrReview();
        return services.BuildServiceProvider();
    }
}
