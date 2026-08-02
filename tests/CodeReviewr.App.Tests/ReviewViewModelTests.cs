using System.Collections.Specialized;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
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

    [Test]
    public void PendingCommentCount_Drives_Badge_Tooltip_And_Comment_CanExecute()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        Assert.That(vm.HasPendingComments, Is.False);
        Assert.That(vm.CanSubmitPendingComments, Is.False);
        Assert.That(vm.SubmitCommentReviewCommand.CanExecute(null), Is.False);
        Assert.That(vm.PendingCommentsTooltip, Is.EqualTo("No pending comments to submit"));

        vm.PendingCommentCount = 1;
        Assert.That(vm.HasPendingComments, Is.True);
        Assert.That(vm.CanSubmitPendingComments, Is.True);
        Assert.That(vm.SubmitCommentReviewCommand.CanExecute(null), Is.True);
        Assert.That(vm.PendingCommentsTooltip, Does.Contain("1 pending comment"));

        vm.PendingCommentCount = 3;
        Assert.That(vm.PendingCommentsTooltip, Does.Contain("3 pending comments"));
    }

    [Test]
    public async Task SelectPullRequest_Loads_Reviewers_And_Pending_Comment_Count()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(
            summary,
            Body: null,
            Files: [],
            CheckRollupState: null,
            Reviewers:
            [
                new PullRequestReviewerStatus("alice", "https://example.com/a.png", "APPROVED"),
                new PullRequestReviewerStatus("bob", null, "REQUESTED"),
            ],
            ViewerReviewState: "APPROVED");

        var sha = new string('a', 40);
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            []);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                summary.Host,
                summary.AccountLogin,
                summary.Owner,
                summary.Name,
                summary.Number,
                Arg.Any<CancellationToken>())
            .Returns(2);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);

        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(false);

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns([
                new OutboxEntry(
                    Id: "1",
                    AccountHost: summary.Host,
                    AccountLogin: summary.AccountLogin,
                    PrNodeId: summary.NodeId,
                    Kind: OutboxKind.AddComment,
                    PayloadJson: "{}",
                    CreatedUtc: DateTimeOffset.UtcNow,
                    Attempts: 0,
                    LastError: null,
                    State: OutboxState.Pending),
            ]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        Assert.That(vm.HasReviewers, Is.True);
        Assert.That(vm.Reviewers, Has.Count.EqualTo(2));
        Assert.That(vm.Reviewers[0].Login, Is.EqualTo("alice"));
        Assert.That(vm.Reviewers[0].ShowApprovedBadge, Is.True);
        Assert.That(vm.PendingCommentCount, Is.EqualTo(3)); // 2 remote + 1 local outbox
        Assert.That(vm.IsOwnPullRequest, Is.False);
        Assert.That(vm.HasApproved, Is.True);
        Assert.That(vm.CanSubmitVerdict, Is.True);
    }

    [Test]
    public async Task OwnPullRequest_Disables_Verdict_But_Not_Comment()
    {
        var summary = CreateSummary(InboxSection.MyPullRequests, "mine", authorLogin: "dev");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            []);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(1);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);

        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(false);

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        Assert.That(vm.IsOwnPullRequest, Is.True);
        Assert.That(vm.CanSubmitVerdict, Is.False);
        Assert.That(vm.SubmitApproveCommand.CanExecute(null), Is.False);
        Assert.That(vm.SubmitRequestChangesCommand.CanExecute(null), Is.False);
        Assert.That(vm.ApproveTooltip, Does.Contain("cannot review your own"));
        Assert.That(vm.CanSubmitPendingComments, Is.True);
        Assert.That(vm.SubmitCommentReviewCommand.CanExecute(null), Is.True);
    }

    [Test]
    public async Task Approve_CancelDialog_DoesNotSubmit()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var (vm, comments, _) = await CreateOpenSessionAsync(summary, reviewSubmit: new FixedReviewSubmitDialog(null));

        await vm.SubmitApproveCommand.ExecuteAsync(null);

        await comments.DidNotReceive()
            .SubmitReviewAsync(
                Arg.Any<ReviewSession>(),
                Arg.Any<SubmitReviewEvent>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        Assert.That(vm.HasApproved, Is.False);
    }

    [Test]
    public async Task Approve_ConfirmDialog_Submits_Body_And_Sets_Pressed()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var (vm, comments, _) = await CreateOpenSessionAsync(
            summary,
            reviewSubmit: new FixedReviewSubmitDialog("Looks good"));

        await vm.SubmitApproveCommand.ExecuteAsync(null);

        await comments.Received(1).SubmitReviewAsync(
            Arg.Any<ReviewSession>(),
            SubmitReviewEvent.Approve,
            "Looks good",
            Arg.Any<CancellationToken>());
        Assert.That(vm.HasApproved, Is.True);
        Assert.That(vm.HasRequestedChanges, Is.False);
    }

    [Test]
    public async Task FileListRebuild_WhileSelected_DoesNotLeave_SelectFilePlaceholder()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("prompts/auth-feature-design.md");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Added)]);

        var hunk = new DiffHunk(
            OldStart: 0,
            OldCount: 0,
            NewStart: 1,
            NewCount: 1,
            Header: "@@ -0,0 +1,1 @@",
            Lines: [new DiffLine(DiffLineKind.Added, null, 1, "hello\n".AsMemory())]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path,
            path,
            ChangeKind.Added,
            ContentId.Empty,
            ContentId.Empty,
            IsBinary: false,
            Hunks: [hunk],
            RawPatch: "");

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(
                session,
                path,
                Arg.Any<DiffOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(fileDiff);

        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(false);
        comments.ResolveAnchorsAsync(
                session,
                Arg.Any<IReadOnlyList<ReviewThread>>(),
                path,
                Arg.Any<FileDiff>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable);

        // Mimic ListBox TwoWay binding: clearing PrFileEntries nulls SelectedItem.
        vm.PrFileEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                vm.SelectedPrFileEntry = null;
        };

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.DiffRows.Count > 0);

        Assert.That(vm.SelectedFile, Is.Not.Null);
        Assert.That(vm.DiffEmptyMessage, Is.EqualTo(""));

        // Rebuild while selected (layout toggle → RebuildPrFileEntries).
        vm.PullRequestFileListLayout = FileListLayoutMode.Tree;
        await WaitUntilAsync(() => !vm.IsLoadingDiff);

        Assert.That(vm.SelectedFile, Is.Not.Null);
        Assert.That(vm.SelectedFile!.Path.Value, Is.EqualTo(path.Value));
        Assert.That(vm.DiffEmptyMessage, Is.Not.EqualTo("Select a file to view its diff"));
        Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
    }

    private static async Task<(ReviewViewModel Vm, IReviewCommentService Comments, ReviewSession Session)>
        CreateOpenSessionAsync(PullRequestSummary summary, IReviewSubmitDialog reviewSubmit)
    {
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            []);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);

        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(false);

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            reviewSubmit: reviewSubmit);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        return (vm, comments, session);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                Assert.Fail("Timed out waiting for condition.");
            await Task.Delay(20);
        }
    }

    private static ReviewViewModel CreateViewModel(
        IPullRequestService pullRequests,
        ISettingsStore settings,
        IReviewService? reviewService = null,
        IReviewCommentService? comments = null,
        IReviewOutbox? outbox = null,
        IDurableUserStore? durable = null,
        IReviewSubmitDialog? reviewSubmit = null)
    {
        outbox ??= Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        durable ??= Substitute.For<IDurableUserStore>();

        return new ReviewViewModel(
            pullRequests,
            reviewService ?? Substitute.For<IReviewService>(),
            comments ?? Substitute.For<IReviewCommentService>(),
            outbox,
            durable,
            Substitute.For<IGitCloneService>(),
            new AlwaysConfirmDialog(),
            reviewSubmit ?? new FixedReviewSubmitDialog(""),
            settings,
            new NotificationService(),
            new IntraLineDiffer(),
            Substitute.For<IGitObjectReader>());
    }

    private static PullRequestSummary CreateSummary(
        InboxSection section,
        string title,
        string authorLogin = "dev") =>
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
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReviewDecision: null,
            BaseRefName: "main",
            HeadRefName: "feature",
            BaseOid: null,
            HeadOid: null,
            AuthorLogin: authorLogin,
            ChangedFiles: 0,
            Section: section);

    private sealed class FixedReviewSubmitDialog(string? result) : IReviewSubmitDialog
    {
        public Task<string?> ShowAsync(string title, string confirmLabel) =>
            Task.FromResult(result);
    }
}
