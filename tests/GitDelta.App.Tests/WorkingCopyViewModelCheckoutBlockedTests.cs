using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class WorkingCopyViewModelCheckoutBlockedTests
{
    private IGitStatusService _status = null!;
    private IGitDiffService _diff = null!;
    private IGitStagingService _staging = null!;
    private IGitDiscardService _discard = null!;
    private IGitCommitService _commit = null!;
    private IGitBranchService _branches = null!;
    private IGitRemoteService _remotes = null!;
    private IGitConflictService _conflicts = null!;
    private IGitStashService _stash = null!;
    private IGitHistoryService _history = null!;
    private ISettingsStore _settings = null!;
    private IFsmonitorService _fsmonitor = null!;
    private NotificationService _notifications = null!;
    private AlwaysConfirmDialog _confirm = null!;
    private FakeStashDialog _stashDialog = null!;
    private IRepositoryWatcher _watcher = null!;
    private ILocalCommentStore _localComments = null!;

    [SetUp]
    public void SetUp()
    {
        _status = Substitute.For<IGitStatusService>();
        _diff = Substitute.For<IGitDiffService>();
        _staging = Substitute.For<IGitStagingService>();
        _discard = Substitute.For<IGitDiscardService>();
        _commit = Substitute.For<IGitCommitService>();
        _branches = Substitute.For<IGitBranchService>();
        _remotes = Substitute.For<IGitRemoteService>();
        _conflicts = Substitute.For<IGitConflictService>();
        _stash = Substitute.For<IGitStashService>();
        _history = Substitute.For<IGitHistoryService>();
        _settings = Substitute.For<ISettingsStore>();
        _fsmonitor = Substitute.For<IFsmonitorService>();
        _notifications = new NotificationService();
        _confirm = new AlwaysConfirmDialog();
        _stashDialog = new FakeStashDialog(null);
        _watcher = Substitute.For<IRepositoryWatcher>();
        _localComments = Substitute.For<ILocalCommentStore>();
        _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        _settings.Current.Returns(new AppSettings());
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _discard.RecentlyDiscarded.Returns([]);
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private WorkingCopyViewModel CreateVm(ICheckoutBlockedDialog checkoutBlocked) =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history, _settings, _notifications, _confirm, _stashDialog,
            new IntraLineDiffer(), _fsmonitor, _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()),
            checkoutBlockedDialog: checkoutBlocked);

    private static RepositoryStatus CleanStatus(string branch = "main") =>
        new([], [], [], InProgressOperation.None, branch, 1);

    private static BranchInfo CreateBranchInfo(string name, bool isCurrent = false) =>
        new(name, isCurrent, IsRemote: false, Upstream: null, TipOid: "abc1234", TipCommitterDate: DateTimeOffset.MinValue);

    [Test]
    public async Task CheckoutBranch_Dirty_Cancel_Does_Not_Stash_Or_Checkout_Again()
    {
        var dialog = new FakeCheckoutBlockedDialog(CheckoutBlockedChoice.Cancel);
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(CleanStatus());
            _branches.CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new GitException(
                    "Your local changes to the following files would be overwritten by checkout",
                    stderr: "error: Your local changes to the following files would be overwritten by checkout"));

            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.CheckoutBranchCommand.ExecuteAsync(CreateBranchInfo("feature"));

            Assert.That(dialog.CallCount, Is.EqualTo(1));
            Assert.That(dialog.LastTargetRef, Is.EqualTo("feature"));
            await _stash.DidNotReceive()
                .StashPushAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await _stash.DidNotReceive()
                .StashPopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _branches.Received(1).CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task CheckoutBranch_Dirty_StashOnly_Does_Not_Pop()
    {
        var dialog = new FakeCheckoutBlockedDialog(CheckoutBlockedChoice.StashOnly);
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(CleanStatus());
            _branches.CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromException(new GitException(
                        "Your local changes to the following files would be overwritten by checkout",
                        stderr: "error: Your local changes to the following files would be overwritten by checkout")),
                    _ => Task.CompletedTask);

            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.CheckoutBranchCommand.ExecuteAsync(CreateBranchInfo("feature"));

            Assert.That(dialog.CallCount, Is.EqualTo(1));
            await _stash.Received(1)
                .StashPushAsync(repo, "GitDelta auto-stash", false, Arg.Any<CancellationToken>());
            await _branches.Received(2).CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>());
            await _stash.DidNotReceive()
                .StashPopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task CheckoutBranch_Dirty_StashAndRestore_Pops_After_Checkout()
    {
        var dialog = new FakeCheckoutBlockedDialog(CheckoutBlockedChoice.StashAndRestore);
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(CleanStatus());
            _branches.CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>())
                .Returns(
                    _ => Task.FromException(new GitException(
                        "Your local changes to the following files would be overwritten by checkout",
                        stderr: "error: Your local changes to the following files would be overwritten by checkout")),
                    _ => Task.CompletedTask);

            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.CheckoutBranchCommand.ExecuteAsync(CreateBranchInfo("feature"));

            Assert.That(dialog.CallCount, Is.EqualTo(1));
            await _stash.Received(1)
                .StashPushAsync(repo, "GitDelta auto-stash", false, Arg.Any<CancellationToken>());
            await _branches.Received(2).CheckoutAsync(repo, "feature", Arg.Any<CancellationToken>());
            await _stash.Received(1).StashPopAsync(repo, Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
