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

public sealed class WorkingCopyViewModelSidebarBranchesTests
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

    private static BranchInfo Local(string name, DateTimeOffset tipDate, bool isCurrent = false) =>
        new(name, isCurrent, IsRemote: false, Upstream: null, TipOid: "abc", TipCommitterDate: tipDate);

    private static IReadOnlyList<BranchInfo> SixLocalsNewestFirst()
    {
        var baseDate = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        return
        [
            Local("b5", baseDate.AddDays(5)),
            Local("b4", baseDate.AddDays(4)),
            Local("b3", baseDate.AddDays(3)),
            Local("b2", baseDate.AddDays(2)),
            Local("b1", baseDate.AddDays(1)),
            Local("main", baseDate, isCurrent: true),
        ];
    }

    [Test]
    public async Task Sidebar_Shows_Top_Four_By_Default()
    {
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SixLocalsNewestFirst());

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.Branches, Has.Count.EqualTo(6));
            Assert.That(vm.VisibleSidebarBranches.Select(b => b.Name).ToList(),
                Is.EqualTo(new[] { "main", "b5", "b4", "b3" }));
            Assert.That(vm.ShowSidebarBranchToggle, Is.True);
            Assert.That(vm.SidebarBranchToggleLabel, Is.EqualTo("Show All"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task ShowAll_Then_ShowLess_Toggles_Full_List()
    {
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SixLocalsNewestFirst());

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.ToggleShowAllSidebarBranchesCommand.Execute(null);
            Assert.That(vm.VisibleSidebarBranches, Has.Count.EqualTo(6));
            Assert.That(vm.SidebarBranchToggleLabel, Is.EqualTo("Show less"));

            vm.ToggleShowAllSidebarBranchesCommand.Execute(null);
            Assert.That(vm.VisibleSidebarBranches.Select(b => b.Name).ToList(),
                Is.EqualTo(new[] { "main", "b5", "b4", "b3" }));
            Assert.That(vm.SidebarBranchToggleLabel, Is.EqualTo("Show All"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task Filter_Shows_All_Matches_Beyond_Top_Four()
    {
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SixLocalsNewestFirst());

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.BranchFilter = "b1";
            Assert.That(vm.VisibleSidebarBranches.Select(b => b.Name).ToList(), Is.EqualTo(new[] { "b1" }));
            Assert.That(vm.ShowSidebarBranchToggle, Is.False);
            Assert.That(vm.ShowSidebarBranchFilterEmpty, Is.False);

            vm.BranchFilter = "main";
            Assert.That(vm.VisibleSidebarBranches.Select(b => b.Name).ToList(), Is.EqualTo(new[] { "main" }));

            vm.BranchFilter = "zzz";
            Assert.That(vm.VisibleSidebarBranches, Is.Empty);
            Assert.That(vm.ShowSidebarBranchFilterEmpty, Is.True);

            vm.BranchFilter = "";
            Assert.That(vm.VisibleSidebarBranches, Has.Count.EqualTo(4));
            Assert.That(vm.ShowSidebarBranchToggle, Is.True);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task Fetch_Reloads_Branch_List_Into_Visible_Projection()
    {
        var initial = SixLocalsNewestFirst();
        var afterFetch = new List<BranchInfo>(initial)
        {
            Local("fresh", new DateTimeOffset(2024, 7, 1, 12, 0, 0, TimeSpan.Zero)),
        };

        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(initial, afterFetch);
        _branches.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var vm = CreateVm();
            await vm.OpenAsync(repo);
            Assert.That(vm.VisibleSidebarBranches[0].Name, Is.EqualTo("main"));

            await vm.FetchCommand.ExecuteAsync(null);

            await _branches.Received(1).FetchAsync(repo, Arg.Any<CancellationToken>());
            Assert.That(vm.Branches, Has.Count.EqualTo(7));
            Assert.That(vm.VisibleSidebarBranches.Select(b => b.Name).ToList(),
                Is.EqualTo(new[] { "main", "fresh", "b5", "b4" }));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
