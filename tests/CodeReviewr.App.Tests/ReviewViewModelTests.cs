using System.Collections.Specialized;
using CodeReviewr.App.Controls;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
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
    public async Task OpeningPullRequest_OnlyChecksAiCache_NeverStartsReview()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings { AiAssistanceEnabled = true });

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        await ai.Received(1).GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>());
        await ai.DidNotReceive().StartReviewAsync(Arg.Any<AiReviewRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AiProgressDialog_DismissDoesNotCancel_AndButtonReopensWhileRunning()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings
        {
            AiAssistanceEnabled = true,
            AiDisclosureAcknowledged = true,
        });

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ObserveProgress(Arg.Any<string>(), Arg.Any<Action<AiRunProgress>>())
            .Returns(Substitute.For<IDisposable>());
        ai.ObserveActivityLog(Arg.Any<string>(), Arg.Any<Action<string>>())
            .Returns(Substitute.For<IDisposable>());

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        vm.AiRunState = AiRunState.Running;
        vm.ShowAiProgressDialog = true;
        vm.NotifyAiButtonStateChanged();

        Assert.That(vm.AiButtonEnabled, Is.True);
        Assert.That(vm.RequestAiReviewCommand.CanExecute(null), Is.True);
        Assert.That(vm.AiButtonTooltip, Is.EqualTo("Show AI review status"));

        vm.DismissAiProgressDialogCommand.Execute(null);
        Assert.That(vm.ShowAiProgressDialog, Is.False);
        await ai.DidNotReceive().CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await vm.RequestAiReviewCommand.ExecuteAsync(null);
        Assert.That(vm.ShowAiProgressDialog, Is.True);
        Assert.That(vm.ShowAiInstructionsDialog, Is.False);
        await ai.DidNotReceive().StartReviewAsync(Arg.Any<AiReviewRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AiProgressDialog_FailedSnapshot_KeepsDialogOpenWithDiagnostics()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings
        {
            AiAssistanceEnabled = true,
            AiDisclosureAcknowledged = true,
            AiTurnTimeoutSeconds = 180,
            AiRunTimeoutSeconds = 0,
        });

        var now = DateTimeOffset.UtcNow;
        var failed = new AiRunSnapshot(
            "run1", summary.NodeId, sha, sha, AiRunState.Failed, "session-abc",
            TurnsUsed: 1, AdHocInstructions: null, ChangeBriefing: null,
            ErrorMessage: "AI review timed out after 180s with no Copilot activity (turn idle timeout).",
            now, now);

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ObserveProgress(Arg.Any<string>(), Arg.Any<Action<AiRunProgress>>())
            .Returns(Substitute.For<IDisposable>());
        ai.ObserveActivityLog(Arg.Any<string>(), Arg.Any<Action<string>>())
            .Returns(Substitute.For<IDisposable>());
        ai.StartReviewAsync(Arg.Any<AiReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(failed);

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await vm.ConfirmStartAiReviewCommand.ExecuteAsync(null);

        Assert.That(vm.AiRunState, Is.EqualTo(AiRunState.Failed));
        Assert.That(vm.ShowAiProgressDialog, Is.True);
        Assert.That(vm.AiLastError, Does.Contain("timed out"));
        Assert.That(vm.AiDiagnosticsText, Does.Contain("Turn idle timeout: 180s"));
        Assert.That(vm.AiDiagnosticsText, Does.Contain("Run timeout: unlimited"));
        Assert.That(vm.AiDiagnosticsText, Does.Contain("session-abc"));
        Assert.That(vm.AiStatusDialogTitle, Is.EqualTo("AI review failed"));
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

    [Test]
    public async Task CancelledDiffLoad_ThenFilterRebuild_DoesNotLeaveLoadingPlaceholder()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/a.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var hunk = new DiffHunk(
            OldStart: 1,
            OldCount: 1,
            NewStart: 1,
            NewCount: 1,
            Header: "@@ -1,1 +1,1 @@",
            Lines:
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "old\n".AsMemory()),
                new DiffLine(DiffLineKind.Added, null, 1, "new\n".AsMemory()),
            ]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path,
            path,
            ChangeKind.Modified,
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

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(
                session,
                path,
                Arg.Any<DiffOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(3);
                await gate.Task.WaitAsync(ct);
                return fileDiff;
            });

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

        var openTask = vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.IsLoadingDiff);

        // Simulate selection churn cancelling the in-flight load while leaving the same file selected.
        var selected = vm.SelectedFile!;
        vm.SelectedFile = null;
        vm.SelectedFile = selected;
        Assert.That(vm.DiffEmptyMessage, Is.EqualTo("Loading pull request…").Or.EqualTo("Select a file to view its diff"));

        gate.SetResult();
        await openTask;
        await WaitUntilAsync(() => vm.DiffRows.Count > 0 && vm.DiffEmptyMessage == "");

        Assert.That(vm.SelectedFile, Is.Not.Null);
        Assert.That(vm.DiffEmptyMessage, Is.Not.EqualTo("Loading pull request…"));
        Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
        Assert.That(vm.IsLoadingDiff, Is.False);
    }

    [Test]
    public async Task SelectFile_Marks_Viewed_When_LocalCache_Has_Matching_Head()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var pathA = FilePath.From("src/a.cs");
        var pathB = FilePath.From("src/b.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(pathA, ChangeKind.Modified), (pathB, ChangeKind.Modified)]);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, Arg.Any<FilePath>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = ci.ArgAt<FilePath>(1);
                return new FileDiff(
                    new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
                    path, path, ChangeKind.Modified,
                    ContentId.Empty, ContentId.Empty, false, [], "");
            });

        var viewed = new List<LocalViewedEntry>();
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(true);
        comments.ResolveAnchorsAsync(
                session, Arg.Any<IReadOnlyList<ReviewThread>>(), Arg.Any<FilePath>(), Arg.Any<FileDiff>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        comments.When(c => c.MarkFileViewedAsync(session, Arg.Any<FilePath>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var path = ci.ArgAt<FilePath>(1).Value;
                viewed.RemoveAll(e => e.Path == path);
                viewed.Add(new LocalViewedEntry(summary.NodeId, path, sha, DateTimeOffset.UtcNow));
            });
        comments.When(c => c.UnmarkFileViewedAsync(session, Arg.Any<FilePath>(), Arg.Any<CancellationToken>()))
            .Do(ci => viewed.RemoveAll(e => e.Path == ci.ArgAt<FilePath>(1).Value));

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns(_ => viewed.ToList());

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.SelectedFile.IsViewed);

        Assert.That(vm.SelectedFile!.Path.Value, Is.EqualTo(pathA.Value));
        await comments.Received().MarkFileViewedAsync(session, pathA, Arg.Any<CancellationToken>());

        // Selecting another unviewed file marks it; already-viewed file is not unmarked.
        vm.SelectedFile = vm.FilteredPrFiles.First(f => f.Path.Value == pathB.Value);
        await WaitUntilAsync(() => vm.SelectedFile!.IsViewed);
        await comments.Received().MarkFileViewedAsync(session, pathB, Arg.Any<CancellationToken>());

        var markCountForA = comments.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IReviewCommentService.MarkFileViewedAsync)
                        && Equals(c.GetArguments()[1], pathA));
        vm.SelectedFile = vm.FilteredPrFiles.First(f => f.Path.Value == pathA.Value);
        await WaitUntilAsync(() => vm.SelectedFile!.Path.Value == pathA.Value && vm.SelectedFile.IsViewed);
        Assert.That(
            comments.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IReviewCommentService.MarkFileViewedAsync)
                            && Equals(c.GetArguments()[1], pathA)),
            Is.EqualTo(markCountForA));
        await comments.DidNotReceive().UnmarkFileViewedAsync(session, pathA, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SelectFile_Marks_Viewed_Keeps_Selected_Instance_When_Filter_All()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var pathA = FilePath.From("src/a.cs");
        var pathB = FilePath.From("src/b.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(pathA, ChangeKind.Modified), (pathB, ChangeKind.Modified)]);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, Arg.Any<FilePath>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = ci.ArgAt<FilePath>(1);
                return new FileDiff(
                    new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
                    path, path, ChangeKind.Modified,
                    ContentId.Empty, ContentId.Empty, false, [], "");
            });

        var viewed = new List<LocalViewedEntry>();
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(true);
        comments.ResolveAnchorsAsync(
                session, Arg.Any<IReadOnlyList<ReviewThread>>(), Arg.Any<FilePath>(), Arg.Any<FileDiff>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        comments.When(c => c.MarkFileViewedAsync(session, Arg.Any<FilePath>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var path = ci.ArgAt<FilePath>(1).Value;
                viewed.RemoveAll(e => e.Path == path);
                viewed.Add(new LocalViewedEntry(summary.NodeId, path, sha, DateTimeOffset.UtcNow));
            });

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns(_ => viewed.ToList());

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.SelectedFile.IsViewed);

        Assert.That(vm.FilterViewed, Is.EqualTo(ViewedFilter.All));

        var selectedBefore = vm.FilteredPrFiles.First(f => f.Path.Value == pathB.Value);
        var entryBefore = vm.PrFileEntries.First(e => e.File == selectedBefore);
        vm.SelectedFile = selectedBefore;
        await WaitUntilAsync(() => vm.SelectedFile!.IsViewed && !vm.SelectedFile.IsViewedPending);

        Assert.That(vm.SelectedFile, Is.SameAs(selectedBefore));
        Assert.That(vm.PrFileEntries.First(e => e.File?.Path.Value == pathB.Value), Is.SameAs(entryBefore));
        Assert.That(vm.FilteredPrFiles.Select(f => f.Path.Value).ToArray(),
            Is.EqualTo(new[] { pathA.Value, pathB.Value }));
    }

    [Test]
    public async Task NotViewed_Filter_Keeps_Selected_Sticky_Without_Cascade()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var pathA = FilePath.From("src/a.cs");
        var pathB = FilePath.From("src/b.cs");
        var pathC = FilePath.From("src/c.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(pathA, ChangeKind.Modified), (pathB, ChangeKind.Modified), (pathC, ChangeKind.Modified)]);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, Arg.Any<FilePath>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = ci.ArgAt<FilePath>(1);
                return new FileDiff(
                    new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
                    path, path, ChangeKind.Modified,
                    ContentId.Empty, ContentId.Empty, false, [], "");
            });

        var viewed = new List<LocalViewedEntry>();
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(true);
        comments.ResolveAnchorsAsync(
                session, Arg.Any<IReadOnlyList<ReviewThread>>(), Arg.Any<FilePath>(), Arg.Any<FileDiff>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        comments.When(c => c.MarkFileViewedAsync(session, Arg.Any<FilePath>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var path = ci.ArgAt<FilePath>(1).Value;
                viewed.RemoveAll(e => e.Path == path);
                viewed.Add(new LocalViewedEntry(summary.NodeId, path, sha, DateTimeOffset.UtcNow));
            });

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns(_ => viewed.ToList());

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.SelectedFile.IsViewed);
        Assert.That(vm.SelectedFile!.Path.Value, Is.EqualTo(pathA.Value));

        vm.FilterViewed = ViewedFilter.NotViewed;
        Assert.That(vm.SelectedFile!.Path.Value, Is.EqualTo(pathA.Value));
        Assert.That(vm.FilteredPrFiles.Select(f => f.Path.Value).ToArray(),
            Is.EqualTo(new[] { pathA.Value, pathB.Value, pathC.Value }));

        vm.SelectedFile = vm.FilteredPrFiles.First(f => f.Path.Value == pathB.Value);
        await WaitUntilAsync(() =>
            vm.SelectedFile!.Path.Value == pathB.Value &&
            vm.SelectedFile.IsViewed &&
            !vm.SelectedFile.IsViewedPending);

        Assert.That(vm.SelectedFile!.Path.Value, Is.EqualTo(pathB.Value));
        Assert.That(vm.FilteredPrFiles.Select(f => f.Path.Value).ToArray(),
            Is.EqualTo(new[] { pathB.Value, pathC.Value }));
        await comments.DidNotReceive().MarkFileViewedAsync(session, pathC, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnmarkSelectedViewed_Clears_Viewed_State()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/a.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, path, Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FileDiff(
                new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
                path, path, ChangeKind.Modified,
                ContentId.Empty, ContentId.Empty, false, [], ""));

        var viewed = new List<LocalViewedEntry>();
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(true);
        comments.ResolveAnchorsAsync(
                session, Arg.Any<IReadOnlyList<ReviewThread>>(), path, Arg.Any<FileDiff>(), Arg.Any<CancellationToken>())
            .Returns([]);
        comments.When(c => c.MarkFileViewedAsync(session, path, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                viewed.RemoveAll(e => e.Path == path.Value);
                viewed.Add(new LocalViewedEntry(summary.NodeId, path.Value, sha, DateTimeOffset.UtcNow));
            });
        comments.When(c => c.UnmarkFileViewedAsync(session, path, Arg.Any<CancellationToken>()))
            .Do(_ => viewed.RemoveAll(e => e.Path == path.Value));

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns(_ => viewed.ToList());

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.SelectedFile.IsViewed);

        await vm.UnmarkSelectedViewedCommand.ExecuteAsync(null);
        Assert.That(vm.SelectedFile!.IsViewed, Is.False);
        await comments.Received().UnmarkFileViewedAsync(session, path, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ToggleViewed_Persists_When_LocalCache_Has_Matching_Head()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/a.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, path, Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FileDiff(
                new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
                path, path, ChangeKind.Modified,
                ContentId.Empty, ContentId.Empty, false, [], ""));

        var viewed = new List<LocalViewedEntry>();
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns([]);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(true);
        comments.ResolveAnchorsAsync(
                session, Arg.Any<IReadOnlyList<ReviewThread>>(), path, Arg.Any<FileDiff>(), Arg.Any<CancellationToken>())
            .Returns([]);
        comments.When(c => c.MarkFileViewedAsync(session, path, Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                viewed.RemoveAll(e => e.Path == path.Value);
                viewed.Add(new LocalViewedEntry(summary.NodeId, path.Value, sha, DateTimeOffset.UtcNow));
            });
        comments.When(c => c.UnmarkFileViewedAsync(session, path, Arg.Any<CancellationToken>()))
            .Do(_ => viewed.RemoveAll(e => e.Path == path.Value));

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns(_ => viewed.ToList());

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.SelectedFile.IsViewed);

        // Toggle off, then on again.
        await vm.ToggleViewedCommand.ExecuteAsync(vm.SelectedFile);
        Assert.That(vm.SelectedFile!.IsViewed, Is.False);

        await vm.ToggleViewedCommand.ExecuteAsync(vm.SelectedFile);
        Assert.That(vm.SelectedFile.IsViewed, Is.True);

        // Stale content_id (different head) must not count as viewed after refresh.
        viewed.Clear();
        viewed.Add(new LocalViewedEntry(summary.NodeId, path.Value, new string('b', 40), DateTimeOffset.UtcNow));
        await vm.ToggleViewedCommand.ExecuteAsync(vm.SelectedFile);
        Assert.That(vm.SelectedFile.IsViewed, Is.False);
    }

    [Test]
    public async Task AddComment_Creates_Clickable_PendingSync_Annotation()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/a.cs");
        var content = ContentId.FromSha(new string('c', 40));
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var hunk = new DiffHunk(
            OldStart: 1, OldCount: 1, NewStart: 1, NewCount: 1,
            Header: "@@ -1,1 +1,1 @@",
            Lines:
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "old\n".AsMemory()),
                new DiffLine(DiffLineKind.Added, null, 1, "new\n".AsMemory()),
            ]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path, path, ChangeKind.Modified,
            content, content, false, [hunk], "");

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, path, Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
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
            .Returns(ci =>
            {
                var threads = ci.ArgAt<IReadOnlyList<ReviewThread>>(1);
                return threads
                    .Where(t => string.Equals(t.Path, path.Value, StringComparison.Ordinal))
                    .Select(t => t.Anchor is not null
                        ? t
                        : t with
                        {
                            Side = DiffSide.New,
                            Anchor = new AnnotationRange(
                                new DiffAnchor(DiffSide.New, content, t.Line ?? 1),
                                new DiffAnchor(DiffSide.New, content, t.Line ?? 1)),
                        })
                    .ToList();
            });

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && !vm.IsLoadingDiff);

        vm.BeginLineCommentCommand.Execute(new LineCommentRequest(DiffSide.New, 1, null));
        vm.NewCommentBody = "needs a test";
        await vm.AddCommentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() =>
            vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        var annotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        Assert.That(annotation.Thread.IsPendingSync, Is.True);
        Assert.That(annotation.Thread.Comments[0].Body, Is.EqualTo("needs a test"));

        vm.SelectedAnnotation = annotation;
        Assert.That(vm.SelectedThread, Is.Not.Null);
        Assert.That(vm.IsSelectedThreadPendingSync, Is.True);
        Assert.That(vm.HasExpandedInlineThread, Is.True);
    }

    [Test]
    public async Task InsertSuggestion_AppendsSuggestionFenceWithLineText()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();

        vm.BeginLineCommentCommand.Execute(new LineCommentRequest(DiffSide.New, 1, null));
        vm.InsertSuggestionCommand.Execute(null);

        Assert.That(vm.NewCommentBody, Does.Contain("```suggestion"));
        Assert.That(vm.NewCommentBody, Does.Contain("new"));
        Assert.That(vm.NewCommentBody.TrimEnd().EndsWith("```", StringComparison.Ordinal), Is.True);
    }

    [Test]
    public async Task BeginEditComment_PrefillsBody_AndSaveCallsEdit()
    {
        var comment = new ReviewComment(
            "RC_own",
            "original body",
            "dev",
            ViewerDidAuthor: true,
            DateTimeOffset.UtcNow,
            Url: null);
        var content = ContentId.FromSha(new string('c', 40));
        var thread = new ReviewThread(
            "RT_1",
            "src/a.cs",
            Line: 1,
            StartLine: null,
            IsResolved: false,
            IsOutdated: false,
            Comments: [comment],
            Side: DiffSide.New,
            Anchor: new AnnotationRange(
                new DiffAnchor(DiffSide.New, content, 1),
                new DiffAnchor(DiffSide.New, content, 1)),
            IsPendingSync: false);

        var (vm, comments, session) = await CreateOpenDiffSessionAsync(initialThreads: [thread]);
        await WaitUntilAsync(() => vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        vm.SelectedAnnotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        Assert.That(vm.CanMutateSelectedThreadComments, Is.True);

        vm.BeginEditCommentCommand.Execute(comment);
        Assert.That(vm.IsEditingComment, Is.True);
        Assert.That(vm.NewCommentBody, Is.EqualTo("original body"));
        Assert.That(vm.DraftPrimaryActionLabel, Is.EqualTo("Update comment"));
        Assert.That(vm.HasDraftCommentAnchor, Is.True);

        vm.NewCommentBody = "updated body";
        await vm.AddCommentCommand.ExecuteAsync(null);

        await comments.Received(1).EditCommentAsync(
            session,
            "RC_own",
            "updated body",
            Arg.Any<CancellationToken>());
        Assert.That(vm.IsEditingComment, Is.False);
    }

    [Test]
    public async Task DeleteComment_ConfirmsAndQueuesDelete()
    {
        var comment = new ReviewComment(
            "RC_own",
            "to delete",
            "dev",
            ViewerDidAuthor: true,
            DateTimeOffset.UtcNow,
            Url: null);
        var content = ContentId.FromSha(new string('c', 40));
        var thread = new ReviewThread(
            "RT_1",
            "src/a.cs",
            Line: 1,
            StartLine: null,
            IsResolved: false,
            IsOutdated: false,
            Comments: [comment],
            Side: DiffSide.New,
            Anchor: new AnnotationRange(
                new DiffAnchor(DiffSide.New, content, 1),
                new DiffAnchor(DiffSide.New, content, 1)),
            IsPendingSync: false);

        var (vm, comments, session) = await CreateOpenDiffSessionAsync(initialThreads: [thread]);
        await WaitUntilAsync(() => vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        vm.SelectedAnnotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        await vm.DeleteCommentCommand.ExecuteAsync(comment);

        await comments.Received(1).DeleteCommentAsync(
            session,
            "RC_own",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReplyToThread_QueuesReply()
    {
        var comment = new ReviewComment(
            "RC_other",
            "hello",
            "alice",
            ViewerDidAuthor: false,
            DateTimeOffset.UtcNow,
            Url: null);
        var content = ContentId.FromSha(new string('c', 40));
        var thread = new ReviewThread(
            "RT_1",
            "src/a.cs",
            Line: 1,
            StartLine: null,
            IsResolved: false,
            IsOutdated: false,
            Comments: [comment],
            Side: DiffSide.New,
            Anchor: new AnnotationRange(
                new DiffAnchor(DiffSide.New, content, 1),
                new DiffAnchor(DiffSide.New, content, 1)),
            IsPendingSync: false);

        var (vm, comments, session) = await CreateOpenDiffSessionAsync(initialThreads: [thread]);
        await WaitUntilAsync(() => vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        vm.SelectedAnnotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        vm.ReplyBody = "a reply";
        await vm.ReplyToThreadCommand.ExecuteAsync(null);

        await comments.Received(1).ReplyCommentAsync(
            session,
            "RT_1",
            "a reply",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BeginEditComment_IgnoredForPendingSyncThread()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();
        vm.BeginLineCommentCommand.Execute(new LineCommentRequest(DiffSide.New, 1, null));
        vm.NewCommentBody = "pending";
        await vm.AddCommentCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        var annotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        vm.SelectedAnnotation = annotation;
        var pendingComment = annotation.Thread.Comments[0];

        vm.BeginEditCommentCommand.Execute(pendingComment);
        Assert.That(vm.IsEditingComment, Is.False);
        Assert.That(vm.CanMutateSelectedThreadComments, Is.False);
    }

    [Test]
    public async Task BeginFileComment_OpensDraftWithoutLine()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();

        Assert.That(vm.IsUnplaceableSectionExpanded, Is.False);

        vm.BeginFileCommentCommand.Execute(null);

        Assert.That(vm.HasDraftCommentAnchor, Is.True);
        Assert.That(vm.DraftCommentLine, Is.Null);
        Assert.That(vm.DraftCommentStartLine, Is.Null);
        Assert.That(vm.DraftCommentSide, Is.Null);
        Assert.That(vm.DraftCommentTargetLabel, Is.EqualTo("Commenting on file"));
    }

    [Test]
    public async Task AddFileComment_QueuesNullLineAndBucketsFileLevel()
    {
        var (vm, comments, session) = await CreateOpenDiffSessionAsync();

        vm.BeginFileCommentCommand.Execute(null);
        vm.NewCommentBody = "file-level note";
        await vm.AddCommentCommand.ExecuteAsync(null);

        await comments.Received(1).AddPendingCommentAsync(
            session,
            "file-level note",
            Arg.Any<FilePath>(),
            null,
            null,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.That(vm.HasDraftCommentAnchor, Is.False);
        Assert.That(
            vm.FileLevelThreads.Count(t => t.IsFileLevel && t.Comments.Any(c => c.Body == "file-level note")),
            Is.EqualTo(1));
    }

    [Test]
    public async Task AddFileComment_RefreshWithRemoteTwin_DoesNotDuplicate()
    {
        var (vm, comments, session) = await CreateOpenDiffSessionAsync();
        var remote = new ReviewThread(
            "TH_FILE_1",
            "src/a.cs",
            Line: null,
            StartLine: null,
            IsResolved: false,
            IsOutdated: false,
            Comments:
            [
                new ReviewComment(
                    "C_FILE_1",
                    "file-level note",
                    AuthorLogin: "octocat",
                    ViewerDidAuthor: true,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Url: null),
            ],
            SubjectType: ReviewThreadSubjectType.File,
            IsFileLevel: true);

        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>())
            .Returns(_ => new List<ReviewThread> { remote });

        vm.BeginFileCommentCommand.Execute(null);
        vm.NewCommentBody = "file-level note";
        await vm.AddCommentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() =>
            vm.FileLevelThreads.Count == 1 &&
            vm.FileLevelThreads.Any(t => t.NodeId == "TH_FILE_1"));

        Assert.That(vm.FileLevelThreads.Count, Is.EqualTo(1));
        Assert.That(vm.FileLevelThreads[0].IsPendingSync, Is.False);
    }

    [Test]
    public async Task ToggleFileCommentsSection_ExpandsAndCollapses()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();
        Assert.That(vm.IsFileCommentsSectionExpanded, Is.False);
        vm.ToggleFileCommentsSectionCommand.Execute(null);
        Assert.That(vm.IsFileCommentsSectionExpanded, Is.True);
        vm.ToggleFileCommentsSectionCommand.Execute(null);
        Assert.That(vm.IsFileCommentsSectionExpanded, Is.False);
    }

    [Test]
    public async Task OpenSelectedThreadInSidebar_SwitchesPresentation()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();
        vm.BeginLineCommentCommand.Execute(new LineCommentRequest(DiffSide.New, 1, null));
        vm.NewCommentBody = "line note";
        await vm.AddCommentCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Any());

        vm.SelectedAnnotation = vm.DiffAnnotations.OfType<ReviewThreadAnnotation>().Single();
        Assert.That(vm.HasExpandedInlineThread, Is.True);
        Assert.That(vm.ShowSideThreadPanel, Is.False);

        vm.OpenSelectedThreadInSidebarCommand.Execute(null);

        Assert.That(vm.ForceSideThreadPanel, Is.True);
        Assert.That(vm.HasExpandedInlineThread, Is.False);
        Assert.That(vm.ShowSideThreadPanel, Is.True);
        Assert.That(vm.SelectedThread, Is.Not.Null);
        Assert.That(vm.ShowAiSidePanel, Is.True);
        Assert.That(vm.IsAiCommentsTabSelected, Is.True);
    }

    [Test]
    public async Task BeginFileComment_ClearsMentionTargetsReply()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();
        vm.MentionTargetsReply = true;
        vm.BeginFileCommentCommand.Execute(null);
        Assert.That(vm.MentionTargetsReply, Is.False);
    }

    [Test]
    public async Task ToggleUnplaceableSection_ExpandsAndCollapses()
    {
        var (vm, _, _) = await CreateOpenDiffSessionAsync();
        Assert.That(vm.IsUnplaceableSectionExpanded, Is.False);
        vm.ToggleUnplaceableSectionCommand.Execute(null);
        Assert.That(vm.IsUnplaceableSectionExpanded, Is.True);
        vm.ToggleUnplaceableSectionCommand.Execute(null);
        Assert.That(vm.IsUnplaceableSectionExpanded, Is.False);
    }

    private static async Task<(ReviewViewModel Vm, IReviewCommentService Comments, ReviewSession Session)>
        CreateOpenDiffSessionAsync(IReadOnlyList<ReviewThread>? initialThreads = null)
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/a.cs");
        var content = ContentId.FromSha(new string('c', 40));
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var hunk = new DiffHunk(
            OldStart: 1, OldCount: 1, NewStart: 1, NewCount: 1,
            Header: "@@ -1,1 +1,1 @@",
            Lines:
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "old\n".AsMemory()),
                new DiffLine(DiffLineKind.Added, null, 1, "new\n".AsMemory()),
            ]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path, path, ChangeKind.Modified,
            content, content, false, [hunk], "");

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, path, Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
            .Returns(fileDiff);

        var threads = initialThreads ?? [];
        var comments = Substitute.For<IReviewCommentService>();
        comments.GetThreadsAsync(session, Arg.Any<CancellationToken>()).Returns(_ => threads);
        comments.SupportsRemoteViewedStateAsync(session, Arg.Any<CancellationToken>()).Returns(false);
        comments.ResolveAnchorsAsync(
                session,
                Arg.Any<IReadOnlyList<ReviewThread>>(),
                path,
                Arg.Any<FileDiff>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var incoming = ci.ArgAt<IReadOnlyList<ReviewThread>>(1);
                return incoming
                    .Where(t => string.Equals(t.Path, path.Value, StringComparison.Ordinal))
                    .Select(t =>
                    {
                        if (t.IsFileLevel || t.IsUnplaceable || t.Anchor is not null || t.Line is null)
                            return t;
                        return t with
                        {
                            Side = DiffSide.New,
                            Anchor = new AnnotationRange(
                                new DiffAnchor(DiffSide.New, content, t.Line.Value),
                                new DiffAnchor(DiffSide.New, content, t.Line.Value)),
                        };
                    })
                    .ToList();
            });

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && !vm.IsLoadingDiff);
        return (vm, comments, session);
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

    [Test]
    public void CanShowMarkdownPreview_Depends_On_Selected_File_Extension()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        Assert.That(vm.CanShowMarkdownPreview, Is.False);

        vm.SelectedFile = new FileItemViewModel(FilePath.From("README.md"), ChangeKind.Modified, isStagedList: false);
        Assert.That(vm.IsMarkdownFile, Is.True);
        Assert.That(vm.CanShowMarkdownPreview, Is.True);

        vm.SelectedFile = new FileItemViewModel(FilePath.From("Program.cs"), ChangeKind.Modified, isStagedList: false);
        Assert.That(vm.IsMarkdownFile, Is.False);
        Assert.That(vm.CanShowMarkdownPreview, Is.False);
    }

    [Test]
    public async Task ToggleShowMarkdownPreview_Loads_New_Blob_Text()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("docs/guide.md");
        var content = ContentId.FromSha(new string('c', 40));
        var markdown = "# Guide\n\nHello world.\n";
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var hunk = new DiffHunk(
            OldStart: 1, OldCount: 1, NewStart: 1, NewCount: 1,
            Header: "@@ -1,1 +1,1 @@",
            Lines:
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "old\n".AsMemory()),
                new DiffLine(DiffLineKind.Added, null, 1, "# Guide\n".AsMemory()),
            ]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path, path, ChangeKind.Modified,
            content, content, false, [hunk], "");

        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetPendingReviewCommentCountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var reviewService = Substitute.For<IReviewService>();
        reviewService.OpenAsync(summary, Arg.Any<CancellationToken>()).Returns(session);
        reviewService.GetDiffAsync(session, path, Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
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

        var objects = Substitute.For<IGitObjectReader>();
        objects.ReadBlobAsync(session.RepositoryPath, content, Arg.Any<CancellationToken>())
            .Returns(System.Text.Encoding.UTF8.GetBytes(markdown));

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        outbox.ListPendingAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns([]);

        var durable = Substitute.For<IDurableUserStore>();
        durable.GetNoteAsync(summary.NodeId, Arg.Any<CancellationToken>()).Returns((string?)null);
        durable.ListAsync(summary.NodeId).Returns([]);

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });

        var vm = CreateViewModel(pullRequests, settings, reviewService, comments, outbox, durable, objects: objects);
        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && !vm.IsLoadingDiff);

        Assert.That(vm.CanShowMarkdownPreview, Is.True);
        Assert.That(vm.ShowMarkdownPreviewPane, Is.False);
        Assert.That(vm.ShowDiffViewer, Is.True);

        vm.ToggleShowMarkdownPreviewCommand.Execute(null);
        await WaitUntilAsync(() => vm.MarkdownPreviewText is not null);

        Assert.That(vm.ShowMarkdownPreviewPane, Is.True);
        Assert.That(vm.ShowDiffViewer, Is.False);
        Assert.That(vm.MarkdownPreviewText, Is.EqualTo(markdown));

        vm.ToggleShowMarkdownPreviewCommand.Execute(null);
        Assert.That(vm.ShowMarkdownPreviewPane, Is.False);
        Assert.That(vm.ShowDiffViewer, Is.True);
        Assert.That(vm.MarkdownPreviewText, Is.Null);
    }

    [Test]
    public void AiChatPlaceholder_Reflects_SelectedFile()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        Assert.That(vm.AiChatSelectedFileLabel, Is.EqualTo("No file selected"));
        Assert.That(vm.AiChatPlaceholder, Is.EqualTo("Ask about this pull request…"));

        var file = new FileItemViewModel(FilePath.From("src/Auth.cs"), ChangeKind.Modified, isStagedList: false);
        vm.SelectedFile = file;

        Assert.That(vm.AiChatSelectedFileLabel, Is.EqualTo("src/Auth.cs"));
        Assert.That(vm.AiChatPlaceholder, Is.EqualTo("Ask about Auth.cs…"));
    }

    [Test]
    public void CanSendAiChat_Is_False_While_Busy()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        vm.AiChatInput = "What changed?";
        Assert.That(vm.CanSendAiChat, Is.True);

        vm.IsAiChatBusy = true;
        Assert.That(vm.CanSendAiChat, Is.False);

        vm.IsAiChatBusy = false;
        Assert.That(vm.CanSendAiChat, Is.True);
    }

    [Test]
    public async Task SendAiChat_Sets_And_Clears_IsAiChatBusy()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings { AiAssistanceEnabled = true });

        var ai = Substitute.For<IAIReviewService>();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ChatAsync(Arg.Any<AiQuestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        vm.AiChatInput = "Explain this PR";
        Assert.That(vm.IsAiChatBusy, Is.False);

        var sendTask = vm.SendAiChatCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsAiChatBusy);
        Assert.That(vm.CanSendAiChat, Is.False);

        gate.SetResult("Because of X.");
        await sendTask;

        Assert.That(vm.IsAiChatBusy, Is.False);
        Assert.That(vm.AiChatMessages.Count, Is.EqualTo(2));
        Assert.That(vm.AiChatMessages[^1].Content, Is.EqualTo("Because of X."));
    }

    [Test]
    public async Task ClearAiChat_ClearsMessages_AndCallsService()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings { AiAssistanceEnabled = true });

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ClearChatHistoryAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);

        vm.AiChatMessages.Add(new AiChatMessage("user", "Hello", DateTimeOffset.UtcNow));
        vm.AiChatMessages.Add(new AiChatMessage("assistant", "Hi", DateTimeOffset.UtcNow));
        Assert.That(vm.CanClearAiChat, Is.True);

        await vm.ClearAiChatCommand.ExecuteAsync(null);

        Assert.That(vm.AiChatMessages, Is.Empty);
        Assert.That(vm.CanClearAiChat, Is.False);
        await ai.Received(1).ClearChatHistoryAsync(summary.NodeId, Arg.Any<CancellationToken>());
    }

    [Test]
    public void PrFileListRebuild_Raises_SelectionClear_Before_Mutation()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        var file = new FileItemViewModel(FilePath.From("src/a.cs"), ChangeKind.Modified, isStagedList: false);
        vm.PrFiles.Add(file);
        vm.FilteredPrFiles.Add(file);

        // Seed entries so the subsequent rebuild mutates a non-empty collection.
        vm.PullRequestFileListLayout = FileListLayoutMode.Tree;
        Assert.That(vm.PrFileEntries.Count, Is.GreaterThan(0));

        var events = new List<string>();
        var entriesAtClear = -1;
        vm.SelectionClearRequested += () =>
        {
            events.Add("clear");
            entriesAtClear = vm.PrFileEntries.Count;
            // Mimic MainWindow ClearPrFileListSelection TwoWay write.
            vm.SelectedPrFileEntry = null;
        };
        vm.PrFileEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                events.Add("mutate");
        };

        vm.PullRequestFileListLayout = FileListLayoutMode.Flat;

        var clearIndex = events.IndexOf("clear");
        var mutateIndex = events.FindIndex(e => e == "mutate");
        Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0), "SelectionClearRequested should fire on rebuild");
        Assert.That(mutateIndex, Is.GreaterThanOrEqualTo(0), "PrFileEntries should mutate on rebuild");
        Assert.That(clearIndex, Is.LessThan(mutateIndex), "Clear must precede ItemsSource mutation");
        Assert.That(entriesAtClear, Is.GreaterThan(0), "Clear should fire before the collection is emptied");
    }

    [Test]
    public void PrFileListRebuild_SelectionClear_DoesNotClearSelectedFile()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings { PullRequestFileListLayout = FileListLayoutMode.Flat });
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        var file = new FileItemViewModel(FilePath.From("src/a.cs"), ChangeKind.Modified, isStagedList: false);
        vm.PrFiles.Add(file);
        vm.FilteredPrFiles.Add(file);
        vm.PullRequestFileListLayout = FileListLayoutMode.Tree;
        vm.SelectedFile = file;
        Assert.That(vm.SelectedFile, Is.SameAs(file));
        Assert.That(vm.SelectedPrFileEntry, Is.Not.Null);

        // Mimic ClearPrFileListSelection: TwoWay SelectedItem=null → SelectedPrFileEntry=null.
        // Must not cascade to SelectedFile=null (that races PrFileEntries.Clear / LoadDiff).
        vm.SelectionClearRequested += () => vm.SelectedPrFileEntry = null;

        vm.PullRequestFileListLayout = FileListLayoutMode.Flat;

        Assert.That(vm.SelectedFile, Is.SameAs(file));
        Assert.That(vm.DiffEmptyMessage, Does.Not.Contain("Source array was not long enough"));
    }

    [Test]
    public async Task ApplyAiRunSnapshot_Complete_PreservesSelection_SingleRebuild()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
        var detail = new PullRequestDetail(summary, Body: null, Files: [], CheckRollupState: null);
        var sha = new string('a', 40);
        var path = FilePath.From("src/auth.cs");
        var session = new ReviewSession(
            "/tmp/repo",
            detail,
            CommitId.FromSha(sha),
            CommitId.FromSha(sha),
            Substitute.For<IReviewTree>(),
            [(path, ChangeKind.Modified)]);

        var hunk = new DiffHunk(
            OldStart: 1,
            OldCount: 1,
            NewStart: 1,
            NewCount: 1,
            Header: "@@ -1,1 +1,1 @@",
            Lines:
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "old\n".AsMemory()),
                new DiffLine(DiffLineKind.Added, null, 1, "new\n".AsMemory()),
            ]);
        var fileDiff = new FileDiff(
            new DiffScope.Revisions(CommitId.FromSha(sha), CommitId.FromSha(sha)),
            path,
            path,
            ChangeKind.Modified,
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
        settings.Current.Returns(new AppSettings
        {
            AiAssistanceEnabled = true,
            AiDisclosureAcknowledged = true,
            PullRequestFileListLayout = FileListLayoutMode.Flat,
        });

        var briefing = new AiChangeBriefingResult(
            ExecutiveSummary: "Summary",
            Risk: AiRiskLevel.Medium,
            RiskDrivers: ["Touches auth"],
            WhatChanged: ["Authentication"],
            ReviewFocus: [path.Value],
            TestingStatus: new AiTestingStatus("Needs review", []),
            Dependencies: [],
            Measured: new AiMeasuredFacts(1, 1, 1));

        var finished = DateTimeOffset.UtcNow;
        var complete = new AiRunSnapshot(
            "run1", summary.NodeId, sha, sha, AiRunState.Complete, "session-abc",
            TurnsUsed: 2, AdHocInstructions: null, ChangeBriefing: briefing,
            ErrorMessage: null, finished.AddMinutes(-2), finished);

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ObserveProgress(Arg.Any<string>(), Arg.Any<Action<AiRunProgress>>())
            .Returns(Substitute.For<IDisposable>());
        ai.ObserveActivityLog(Arg.Any<string>(), Arg.Any<Action<string>>())
            .Returns(Substitute.For<IDisposable>());
        ai.StartReviewAsync(Arg.Any<AiReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(complete);

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        // Mimic ListBox TwoWay clear on SelectionClearRequested and on ItemsSource Clear.
        vm.SelectionClearRequested += () => vm.SelectedPrFileEntry = null;
        vm.PrFileEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                vm.SelectedPrFileEntry = null;
        };

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await WaitUntilAsync(() => vm.SelectedFile is not null && vm.DiffRows.Count > 0);

        var selectedBefore = vm.SelectedFile;
        Assert.That(selectedBefore, Is.Not.Null);
        Assert.That(vm.PullRequestFileListLayout, Is.EqualTo(FileListLayoutMode.Flat));

        await vm.ConfirmStartAiReviewCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.AiRunState == AiRunState.Complete);

        // Completing a run switches the side panel to the change briefing tab, which
        // clears the file selection; selecting the file again should still work cleanly.
        Assert.That(vm.IsChangeBriefingSelected, Is.True);
        Assert.That(vm.SelectedFile, Is.Null);

        vm.SelectedFile = selectedBefore;
        Assert.That(vm.SelectedFile, Is.SameAs(selectedBefore));
        Assert.That(vm.PullRequestFileListLayout, Is.EqualTo(FileListLayoutMode.Flat));
        Assert.That(vm.HasAiRun, Is.True);
        Assert.That(vm.DiffEmptyMessage, Does.Not.Contain("Source array was not long enough"));
        Assert.That(vm.DiffEmptyMessage, Does.Not.Contain("Failed to load diff"));
    }

    [Test]
    public async Task ApplyAiRunSnapshot_Sets_FinishedUtc_For_Conversation_Timestamp()
    {
        var summary = CreateSummary(InboxSection.NeedsMyReview, "demo", authorLogin: "octocat");
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
        settings.Current.Returns(new AppSettings
        {
            AiAssistanceEnabled = true,
            AiDisclosureAcknowledged = true,
        });

        var finished = DateTimeOffset.UtcNow.AddMinutes(-12);
        var started = finished.AddMinutes(-3);
        var complete = new AiRunSnapshot(
            "run1", summary.NodeId, sha, sha, AiRunState.Complete, "session-abc",
            TurnsUsed: 2, AdHocInstructions: null, ChangeBriefing: CreateChangeBriefing(AiRiskLevel.Low),
            ErrorMessage: null, started, finished);

        var ai = Substitute.For<IAIReviewService>();
        ai.GetCachedRunAsync(summary.NodeId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AiRunSnapshot?>((AiRunSnapshot?)null));
        ai.ObserveProgress(Arg.Any<string>(), Arg.Any<Action<AiRunProgress>>())
            .Returns(Substitute.For<IDisposable>());
        ai.ObserveActivityLog(Arg.Any<string>(), Arg.Any<Action<string>>())
            .Returns(Substitute.For<IDisposable>());
        ai.StartReviewAsync(Arg.Any<AiReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(complete);

        var vm = CreateViewModel(
            pullRequests,
            settings,
            reviewService: reviewService,
            comments: comments,
            outbox: outbox,
            durable: durable,
            ai: ai);

        await vm.SelectPullRequestCommand.ExecuteAsync(summary);
        await vm.ConfirmStartAiReviewCommand.ExecuteAsync(null);

        Assert.That(vm.AiRunState, Is.EqualTo(AiRunState.Complete));
        Assert.That(vm.AiReviewFinishedUtc, Is.EqualTo(finished));
        Assert.That(vm.HasAiRun, Is.True);
    }

    [Test]
    public void AiFileBriefing_Visible_For_Selection_And_SidePanelToggle_KeepsData()
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var vm = CreateViewModel(Substitute.For<IPullRequestService>(), settings);

        Assert.That(vm.HasAiFileBriefing, Is.False);
        Assert.That(vm.ShowAiSidePanel, Is.True);

        vm.SelectedFile = new FileItemViewModel(FilePath.From("src/Empty.cs"), ChangeKind.Modified, isStagedList: false);
        Assert.That(vm.HasAiFileBriefing, Is.False);

        vm.AiFileBriefing = new AiFileBriefingResult(
            "src/Empty.cs",
            "Purpose",
            AiChangeClassification.RefactorOnly,
            ["Interesting finding"]);
        Assert.That(vm.HasAiFileBriefing, Is.True);

        vm.ToggleAiSidePanelCommand.Execute(null);
        Assert.That(vm.ShowAiSidePanel, Is.False);
        Assert.That(vm.HasAiFileBriefing, Is.True);
    }

    private static AiChangeBriefingResult CreateChangeBriefing(AiRiskLevel risk) =>
        new(
            ExecutiveSummary: "Summary",
            Risk: risk,
            RiskDrivers: [],
            WhatChanged: [],
            ReviewFocus: [],
            TestingStatus: new AiTestingStatus("", []),
            Dependencies: [],
            Measured: new AiMeasuredFacts(0, 0, 0));

    private static ReviewViewModel CreateViewModel(
        IPullRequestService pullRequests,
        ISettingsStore settings,
        IReviewService? reviewService = null,
        IReviewCommentService? comments = null,
        IReviewOutbox? outbox = null,
        IDurableUserStore? durable = null,
        IReviewSubmitDialog? reviewSubmit = null,
        IGitObjectReader? objects = null,
        IGitHistoryService? history = null,
        IAIReviewService? ai = null)
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
            objects ?? Substitute.For<IGitObjectReader>(),
            history ?? Substitute.For<IGitHistoryService>(),
            ai: ai ?? NullAIReviewService.Instance);
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
