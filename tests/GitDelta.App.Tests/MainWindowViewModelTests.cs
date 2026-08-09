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

public sealed class MainWindowViewModelTests
{
    private IGitStatusService _status = null!;
    private ISettingsStore _settings = null!;
    private IAccountService _accounts = null!;
    private IRepositoryLocator _repositoryLocator = null!;
    private IPullRequestService _pullRequests = null!;
    private IRepositoryWatcher _watcher = null!;
    private NotificationService _notifications = null!;
    private AppSettings _appSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _appSettings = new AppSettings();
        _settings = Substitute.For<ISettingsStore>();
        _settings.Current.Returns(_appSettings);
        _settings
            .When(s => s.Update(Arg.Any<Action<AppSettings>>()))
            .Do(call => call.Arg<Action<AppSettings>>()(_appSettings));

        _status = Substitute.For<IGitStatusService>();
        _accounts = Substitute.For<IAccountService>();
        _repositoryLocator = Substitute.For<IRepositoryLocator>();
        _pullRequests = Substitute.For<IPullRequestService>();
        _watcher = Substitute.For<IRepositoryWatcher>();
        _notifications = new NotificationService();

        _accounts.ListAccounts().Returns(_ => _appSettings.Accounts);
        _pullRequests.GetInboxAsync(Arg.Any<CancellationToken>()).Returns([]);
        _status.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private MainWindowViewModel CreateVm()
    {
        var branches = Substitute.For<IGitBranchService>();
        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var stash = Substitute.For<IGitStashService>();
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var history = Substitute.For<IGitHistoryService>();
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var discard = Substitute.For<IGitDiscardService>();
        discard.RecentlyDiscarded.Returns([]);
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var confirm = new AlwaysConfirmDialog();

        var workingCopy = new WorkingCopyViewModel(
            _status,
            Substitute.For<IGitDiffService>(),
            Substitute.For<IGitStagingService>(),
            discard,
            Substitute.For<IGitObjectReader>(),
            Substitute.For<IGitCommitService>(),
            branches,
            Substitute.For<IGitRemoteService>(),
            Substitute.For<IGitConflictService>(),
            Substitute.For<IGitRebaseService>(),
            stash,
            history,
            _settings,
            _notifications,
            confirm,
            new FakeStashDialog(new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true)),
            new IntraLineDiffer(),
            Substitute.For<IFsmonitorService>(),
            _watcher,
            new PendingChangesReviewViewModel(
                NullAIReviewService.Instance,
                localComments,
                _settings,
                confirm,
                _notifications,
                Substitute.For<IGitHistoryService>()));

        var outbox = Substitute.For<IReviewOutbox>();
        outbox.IsOffline.Returns(false);

        var review = new ReviewViewModel(
            _pullRequests,
            Substitute.For<IReviewService>(),
            Substitute.For<IReviewCommentService>(),
            outbox,
            Substitute.For<IDurableUserStore>(),
            Substitute.For<IGitCloneService>(),
            confirm,
            new FixedReviewSubmitDialog(""),
            _settings,
            _notifications,
            new IntraLineDiffer(),
            Substitute.For<IGitObjectReader>(),
            Substitute.For<IGitHistoryService>(),
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
            _accounts,
            _repositoryLocator,
            confirm,
            Substitute.For<IGitHistoryService>(),
            localLocator,
            _status,
            Substitute.For<IGitCloneService>(),
            new AlwaysConfirmCheckoutPullRequestDialog());
    }

    [Test]
    public async Task OpenRepositoryPathAsync_Opens_WorkingCopy_And_Adds_Recent()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenRepositoryPathAsync(repo);

            Assert.That(vm.WorkingCopy.RepositoryPath, Is.EqualTo(repo));
            Assert.That(vm.WorkingCopy.HasRepository, Is.True);
            Assert.That(vm.RecentRepositories[0], Is.EqualTo(repo));
            Assert.That(_appSettings.RecentRepositories[0], Is.EqualTo(repo));
            await _status.Received().GetStatusAsync(repo, Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task AddGitHubAccount_Refreshes_Accounts_And_Inbox()
    {
        _accounts.AddAccountAsync("github.com", "tok", Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var account = new GitHubAccountSettings
                {
                    Host = "github.com",
                    Login = "octocat",
                    AvatarUrl = "https://example.com/a.png",
                };
                _appSettings.Accounts.Add(account);
                return account;
            });

        var vm = CreateVm();
        Assert.That(vm.AddGitHubAccountCommand.CanExecute(null), Is.True);

        vm.NewGitHubHost = "github.com";
        vm.NewGitHubToken = "tok";
        await vm.AddGitHubAccountCommand.ExecuteAsync(null);

        Assert.That(vm.GitHubAccounts, Has.Count.EqualTo(1));
        Assert.That(vm.GitHubAccounts[0].Login, Is.EqualTo("octocat"));
        Assert.That(vm.NewGitHubToken, Is.Empty);
        await _accounts.Received(1).AddAccountAsync("github.com", "tok", Arg.Any<CancellationToken>());
        await _pullRequests.Received(1).GetInboxAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void SelectRepositoryCommand_CanExecute_With_Path()
    {
        var vm = CreateVm();
        Assert.That(vm.SelectRepositoryCommand.CanExecute("/tmp/repo"), Is.True);
        Assert.That(vm.RefreshCommand.CanExecute(null), Is.True);
    }

    [Test]
    public void TryHandleKeyboardShortcut_Toggles_Navigator()
    {
        var vm = CreateVm();
        Assert.That(vm.IsNavigatorCollapsed, Is.False);
        Assert.That(vm.TryHandleKeyboardShortcut(GitDelta.Core.Settings.KeyboardShortcutIds.ToggleNavigator), Is.True);
        Assert.That(vm.IsNavigatorCollapsed, Is.True);
    }

    [Test]
    public void ReloadShortcutBindingsUi_Loads_Catalog_Defaults()
    {
        var vm = CreateVm();
        vm.ReloadShortcutBindingsUi();
        Assert.That(vm.ShortcutBindings, Is.Not.Empty);
        Assert.That(
            vm.ShortcutBindings.Any(r => r.Id == GitDelta.Core.Settings.KeyboardShortcutIds.Push
                && r.Gesture == "Ctrl+Shift+P"),
            Is.True);
    }

    private sealed class FixedReviewSubmitDialog(string? result) : IReviewSubmitDialog
    {
        public Task<string?> ShowAsync(string title, string confirmLabel) =>
            Task.FromResult(result);
    }
}
