using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.GitHub;
using GitDelta.Persistence;
using GitDelta.Review;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class CheckoutPullRequestBranchTests
{
    private AppSettings _appSettings = null!;
    private ISettingsStore _settings = null!;
    private IGitStatusService _status = null!;
    private IGitBranchService _branches = null!;
    private IRepositoryLocator _repositoryLocator = null!;
    private IGitCloneService _clone = null!;
    private RecordingCheckoutPrDialog _dialog = null!;
    private AlwaysConfirmDialog _confirm = null!;
    private NotificationService _notifications = null!;
    private string _repoA = null!;
    private string _repoB = null!;

    [SetUp]
    public void SetUp()
    {
        _appSettings = new AppSettings { DevelopmentFolder = Path.GetTempPath() };
        _settings = Substitute.For<ISettingsStore>();
        _settings.Current.Returns(_appSettings);
        _settings.When(s => s.Update(Arg.Any<Action<AppSettings>>()))
            .Do(c => c.Arg<Action<AppSettings>>()(_appSettings));

        _status = Substitute.For<IGitStatusService>();
        _branches = Substitute.For<IGitBranchService>();
        _repositoryLocator = Substitute.For<IRepositoryLocator>();
        _clone = Substitute.For<IGitCloneService>();
        _dialog = new RecordingCheckoutPrDialog();
        _confirm = new AlwaysConfirmDialog();
        _notifications = new NotificationService();

        _repoA = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "clone-a");
        _repoB = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"), "clone-b");
        Directory.CreateDirectory(_repoA);
        Directory.CreateDirectory(_repoB);

        _status.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = call.ArgAt<string>(0);
                IReadOnlyList<StatusEntry> staged = path == _repoB
                    ? [new StatusEntry(FilePath.From("x.txt"), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false)]
                    : [];
                return new RepositoryStatus(staged, [], [], InProgressOperation.None, "main", 1);
            });

        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _branches.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _branches.CheckoutOrCreateTrackingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        TryDelete(_repoA);
        TryDelete(_repoB);
    }

    [Test]
    public async Task CheckoutPullRequestBranch_Ambiguous_Shows_Candidates_With_Status_And_Checks_Out()
    {
        _repositoryLocator.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new LocatedRepository(_repoA, "github.com", "acme", "widgets", "https://github.com/acme/widgets.git"),
                new LocatedRepository(_repoB, "github.com", "acme", "widgets", "https://github.com/acme/widgets.git")));

        var vm = CreateVm();
        SelectPr(vm.Review, SamplePr());

        Assert.That(vm.CheckoutPullRequestBranchCommand.CanExecute(null), Is.True);
        await vm.CheckoutPullRequestBranchCommand.ExecuteAsync(null);

        Assert.That(_dialog.LastModel, Is.Not.Null);
        Assert.That(_dialog.LastModel!.Candidates, Has.Count.EqualTo(2));
        Assert.That(_dialog.LastModel.Candidates.Any(c => c.Path == _repoB && c.StatusSummary.Contains("1 staged")), Is.True);
        Assert.That(_dialog.LastModel.Candidates.Any(c => c.Path == _repoA && c.StatusSummary.Contains("Clean")), Is.True);

        Assert.That(vm.WorkingCopy.RepositoryPath, Is.EqualTo(_repoA));
        Assert.That(vm.Review.IsPullRequestMode, Is.False);
        await _branches.Received().FetchAsync(_repoA, Arg.Any<CancellationToken>());
        await _branches.Received().CheckoutOrCreateTrackingAsync(
            _repoA, "feature/pr", "origin/feature/pr", Arg.Any<CancellationToken>());
        Assert.That(_appSettings.RepositoryBindings.Any(b =>
            b.LocalPath == _repoA
            && b.Owner == "acme"
            && b.Name == "widgets"), Is.True);
    }

    [Test]
    public async Task CheckoutPullRequestBranch_Missing_Clone_Prompts_Then_Clones()
    {
        _repositoryLocator.ScanAsync(Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable());

        var suggested = LocalRepositoryLocator.BuildSuggestedPath(_appSettings, "acme", "widgets");

        _clone.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Directory.CreateDirectory(ci.ArgAt<string>(1));
                return Task.CompletedTask;
            });

        var vm = CreateVm();
        SelectPr(vm.Review, SamplePr());

        await vm.CheckoutPullRequestBranchCommand.ExecuteAsync(null);

        await _clone.Received(1).CloneAsync(
            Arg.Is<string>(u => u.Contains("acme/widgets")),
            Arg.Any<string>(),
            Arg.Any<IProgress<string>?>(),
            Arg.Any<CancellationToken>());
        Assert.That(vm.WorkingCopy.RepositoryPath, Is.EqualTo(suggested));
        await _branches.Received().CheckoutOrCreateTrackingAsync(
            suggested, "feature/pr", "origin/feature/pr", Arg.Any<CancellationToken>());
    }

    private MainWindowViewModel CreateVm()
    {
        var accounts = Substitute.For<IAccountService>();
        accounts.ListAccounts().Returns(_ => _appSettings.Accounts);
        var pullRequests = Substitute.For<IPullRequestService>();
        pullRequests.GetInboxAsync(Arg.Any<CancellationToken>()).Returns([]);
        var watcher = Substitute.For<IRepositoryWatcher>();
        var history = Substitute.For<IGitHistoryService>();
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var stash = Substitute.For<IGitStashService>();
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var discard = Substitute.For<IGitDiscardService>();
        discard.RecentlyDiscarded.Returns([]);
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        var workingCopy = new WorkingCopyViewModel(
            _status,
            Substitute.For<IGitDiffService>(),
            Substitute.For<IGitStagingService>(),
            discard,
            Substitute.For<IGitObjectReader>(),
            Substitute.For<IGitCommitService>(),
            _branches,
            Substitute.For<IGitRemoteService>(),
            Substitute.For<IGitConflictService>(),
            Substitute.For<IGitRebaseService>(),
            stash,
            history,
            _settings,
            _notifications,
            _confirm,
            new FakeStashDialog(new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true)),
            new IntraLineDiffer(),
            Substitute.For<IFsmonitorService>(),
            watcher,
            new PendingChangesReviewViewModel(
                NullAIReviewService.Instance,
                localComments,
                _settings,
                _confirm,
                _notifications,
                Substitute.For<IGitHistoryService>()),
            checkoutBlockedDialog: CancelCheckoutBlockedDialog.Instance);

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);
        var review = new ReviewViewModel(
            pullRequests,
            Substitute.For<IReviewService>(),
            Substitute.For<IReviewCommentService>(),
            outbox,
            Substitute.For<IDurableUserStore>(),
            _clone,
            _confirm,
            new FixedReviewSubmitDialog(""),
            _settings,
            _notifications,
            new IntraLineDiffer(),
            Substitute.For<IGitObjectReader>(),
            history,
            ai: NullAIReviewService.Instance);

        var localLocator = new LocalRepositoryLocator(
            _repositoryLocator,
            _settings,
            Substitute.For<IGitRemoteService>());

        return new MainWindowViewModel(
            workingCopy,
            review,
            new DiagnosticsOverlayViewModel(),
            new GitConsoleViewModel(new GitCommandLog()),
            _settings,
            _notifications,
            accounts,
            _repositoryLocator,
            _confirm,
            history,
            localLocator,
            _status,
            _clone,
            _dialog);
    }

    private static void SelectPr(ReviewViewModel vm, PullRequestSummary summary)
    {
        vm.SelectedPullRequest = summary;
        vm.WorkspaceMode = WorkspaceMode.PullRequest;
    }

    private static PullRequestSummary SamplePr() => new(
        NodeId: "PR_1",
        Host: "github.com",
        AccountLogin: "octocat",
        RepositoryNodeId: "R_1",
        Owner: "acme",
        Name: "widgets",
        NameWithOwner: "acme/widgets",
        Number: 42,
        Title: "Add widgets",
        Url: "https://github.com/acme/widgets/pull/42",
        IsDraft: false,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        ReviewDecision: null,
        BaseRefName: "main",
        HeadRefName: "feature/pr",
        BaseOid: null,
        HeadOid: null,
        AuthorLogin: "octocat",
        ChangedFiles: 1,
        Section: InboxSection.NeedsMyReview);

    private static async IAsyncEnumerable<LocatedRepository> ToAsyncEnumerable(
        params LocatedRepository[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            var parent = Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class RecordingCheckoutPrDialog : ICheckoutPullRequestDialog
    {
        public CheckoutPullRequestDialogModel? LastModel { get; private set; }

        public Task<CheckoutPullRequestDialogResult> ShowAsync(CheckoutPullRequestDialogModel model)
        {
            LastModel = model;
            var path = model.Candidates.FirstOrDefault()?.Path;
            return Task.FromResult(new CheckoutPullRequestDialogResult(path is not null, path));
        }
    }

    private sealed class FixedReviewSubmitDialog(string? result) : IReviewSubmitDialog
    {
        public Task<string?> ShowAsync(string title, string confirmLabel) =>
            Task.FromResult(result);
    }
}
