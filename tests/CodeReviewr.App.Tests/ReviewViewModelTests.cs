using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Diff;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;
using CodeReviewr.Review;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class ReviewViewModelTests
{
    [Test]
    public async Task RefreshInbox_Populates_Sections_From_Service()
    {
        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetInboxAsync(Arg.Any<CancellationToken>()).Returns([
            CreateSummary(InboxSection.NeedsMyReview, "needs"),
            CreateSummary(InboxSection.Reviewed, "reviewed"),
            CreateSummary(InboxSection.MyPullRequests, "mine"),
        ]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());

        var vm = CreateViewModel(pullRequests, settings);

        await vm.RefreshInboxCommand.ExecuteAsync(null);

        Assert.That(vm.NeedsMyReview, Has.Count.EqualTo(1));
        Assert.That(vm.Reviewed, Has.Count.EqualTo(1));
        Assert.That(vm.MyPullRequests, Has.Count.EqualTo(1));
        Assert.That(vm.NeedsMyReview[0].Title, Is.EqualTo("needs"));
    }

    private static ReviewViewModel CreateViewModel(IPullRequestService pullRequests, ISettingsStore settings)
    {
        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        var durable = Substitute.For<IDurableUserStore>();

        return new ReviewViewModel(
            pullRequests,
            Substitute.For<IReviewService>(),
            Substitute.For<IReviewCommentService>(),
            outbox,
            durable,
            Substitute.For<IGitCloneService>(),
            new AlwaysConfirmDialog(),
            settings,
            new NotificationService(),
            new IntraLineDiffer());
    }

    private static PullRequestSummary CreateSummary(InboxSection section, string title) =>
        new(
            NodeId: Guid.NewGuid().ToString("N"),
            Host: "github.com",
            AccountLogin: "dev",
            RepositoryNodeId: "R",
            Owner: "acme",
            Name: "demo",
            NameWithOwner: "acme/demo",
            Number: 1,
            Title: title,
            Url: "https://example.com",
            IsDraft: false,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReviewDecision: null,
            BaseRefName: "main",
            HeadRefName: "feature",
            BaseOid: null,
            HeadOid: null,
            AuthorLogin: "dev",
            ChangedFiles: 0,
            Section: section);
}
