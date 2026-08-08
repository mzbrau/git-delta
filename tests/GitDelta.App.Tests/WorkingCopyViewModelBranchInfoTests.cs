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

public sealed class WorkingCopyViewModelBranchInfoTests
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

    private static BranchInfo Local(string name, bool isCurrent = false) =>
        new(name, isCurrent, IsRemote: false, Upstream: null, TipOid: $"{name}-oid",
            TipCommitterDate: DateTimeOffset.UnixEpoch);

    [Test]
    public async Task ShowBranchInfo_Defaults_Base_To_Current_And_Reloads_On_Base_Change()
    {
        var main = Local("main", isCurrent: true);
        var feature = Local("feature");
        var develop = Local("develop");
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([main, feature, develop]);
        _branches.GetDivergenceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BranchDivergence(1, 2));

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            await vm.ShowBranchInfoCommand.ExecuteAsync(feature);

            Assert.That(vm.ShowBranchInfoDialog, Is.True);
            Assert.That(vm.BranchInfoTargetName, Is.EqualTo("feature"));
            Assert.That(vm.BranchInfoSelectedBase?.Name, Is.EqualTo("main"));
            Assert.That(vm.BranchInfoCurrentName, Is.EqualTo("main"));
            Assert.That(vm.BranchInfoBaseBranches.Select(b => b.Name).ToList(),
                Is.EquivalentTo(new[] { "main", "feature", "develop" }));

            await _branches.Received(1).GetDivergenceAsync(
                repo, "main", "feature", Arg.Any<CancellationToken>());

            var developBase = vm.BranchInfoBaseBranches.Single(b => b.Name == "develop");
            vm.BranchInfoSelectedBase = developBase;

            Assert.That(async () =>
                {
                    await Task.Yield();
                    return !vm.IsBranchInfoLoading
                        && string.Equals(vm.BranchInfoCurrentName, "develop", StringComparison.Ordinal);
                },
                Is.True.After(2000, 20));

            await _branches.Received(1).GetDivergenceAsync(
                repo, "develop", "feature", Arg.Any<CancellationToken>());
            Assert.That(vm.BranchInfoCurrentName, Is.EqualTo("develop"));
            Assert.That(vm.BranchInfoAhead, Is.EqualTo(2));
            Assert.That(vm.BranchInfoBehind, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
