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

public sealed class WorkingCopyViewModelHistoryContextMenuTests
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
        _stashDialog = new FakeStashDialog(
            new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true));
        _watcher = Substitute.For<IRepositoryWatcher>();
        _localComments = Substitute.For<ILocalCommentStore>();
        _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        _settings.Current.Returns(new AppSettings());
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _discard.RecentlyDiscarded.Returns([]);
        _status.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private WorkingCopyViewModel CreateVm() =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history, _settings, _notifications, _confirm, _stashDialog,
            new IntraLineDiffer(), _fsmonitor, _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()));

    private static BranchInfo Branch(string name, bool isCurrent = false, bool isRemote = false) =>
        new(name, isCurrent, isRemote, Upstream: null, TipOid: "abc");

    [Test]
    public async Task CherryPick_Disabled_When_History_Branch_Is_Current()
    {
        var main = Branch("main", isCurrent: true);
        var feature = Branch("feature");
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([main, feature]);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.SelectedHistoryBranch?.Name, Is.EqualTo("main"));
            Assert.That(vm.CanCherryPickHistoryCommit, Is.False);
            Assert.That(vm.CherryPickCommitCommand.CanExecute(null), Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task CherryPick_Enabled_When_History_Branch_Differs_From_Current()
    {
        var main = Branch("main", isCurrent: true);
        var feature = Branch("feature");
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([main, feature]);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.SelectedHistoryBranch = feature;

            Assert.That(vm.CanCherryPickHistoryCommit, Is.True);
            Assert.That(vm.CherryPickCommitCommand.CanExecute(null), Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task HistoryBranchFilter_Matches_Branch_Names_Case_Insensitive()
    {
        var main = Branch("main", isCurrent: true);
        var feature = Branch("feature/login");
        var originMain = Branch("origin/main", isRemote: true);
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([main, feature, originMain]);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.FilteredHistoryBranches.Select(b => b.Name), Is.EquivalentTo(["main", "feature/login", "origin/main"]));

            vm.HistoryBranchFilter = "MAIN";
            Assert.That(vm.FilteredHistoryBranches.Select(b => b.Name), Is.EquivalentTo(["main", "origin/main"]));
            Assert.That(vm.ShowHistoryBranchFilterEmpty, Is.False);

            vm.HistoryBranchFilter = "feature";
            Assert.That(vm.FilteredHistoryBranches.Select(b => b.Name), Is.EquivalentTo(["feature/login"]));

            vm.HistoryBranchFilter = "zzz-no-match";
            Assert.That(vm.FilteredHistoryBranches, Is.Empty);
            Assert.That(vm.ShowHistoryBranchFilterEmpty, Is.True);

            vm.HistoryBranchFilter = "";
            Assert.That(vm.FilteredHistoryBranches.Select(b => b.Name), Is.EquivalentTo(["main", "feature/login", "origin/main"]));
            Assert.That(vm.ShowHistoryBranchFilterEmpty, Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
