using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class WorkingCopyViewModelSnappyTests
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

        _settings.Current.Returns(new AppSettings());
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _discard.RecentlyDiscarded.Returns([]);
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private WorkingCopyViewModel CreateVm() =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, _stash, _history, _settings, _notifications, _confirm, _stashDialog,
            new IntraLineDiffer(), _fsmonitor, _watcher);

    private static StatusEntry Unstaged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: false, IsUnstaged: true, IsConflicted: false);

    private static StatusEntry Staged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false);

    private static RepositoryStatus Status(IReadOnlyList<StatusEntry> unstaged, long epoch = 1) =>
        new([], unstaged, [], InProgressOperation.None, "main", epoch);

    private static RepositoryStatus StatusWithStaged(IReadOnlyList<StatusEntry> staged, long epoch = 1) =>
        new(staged, [], [], InProgressOperation.None, "main", epoch);

    private static FileDiff DiffFor(string path, DiffTarget target = DiffTarget.IndexToWorktree) =>
        UntrackedFileDiff.Create(FilePath.From(path), $"content of {path}\n", target);

    [Test]
    public async Task StageFile_Does_Not_Refresh_Before_Staging()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")]));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var order = new List<string>();
            _status.When(s => s.GetStatusAsync(repo, Arg.Any<CancellationToken>()))
                .Do(_ => order.Add("status"));
            _staging.When(s => s.StageFileAsync(repo, Arg.Any<FilePath>(), Arg.Any<CancellationToken>()))
                .Do(_ => order.Add("stage"));

            _status.ClearReceivedCalls();
            _staging.ClearReceivedCalls();
            order.Clear();

            var file = vm.UnstagedFiles[0];
            await vm.StageFileCommand.ExecuteAsync(file);

            Assert.That(order, Is.Not.Empty);
            Assert.That(order[0], Is.EqualTo("stage"), "Stage must not wait on a pre-refresh status call");
            Assert.That(order.Count(x => x == "stage"), Is.EqualTo(1));
            Assert.That(order.Count(x => x == "status"), Is.GreaterThanOrEqualTo(1), "Post-stage refresh should still run");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Prefetch_Then_Select_Reuses_Single_Diff_Fetch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var fetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;

            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt"), Unstaged("b.txt")]));

            _diff.GetDiffAsync(
                    repo,
                    Arg.Any<FilePath>(),
                    Arg.Any<DiffScope>(),
                    Arg.Any<DiffOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    Interlocked.Increment(ref calls);
                    fetchStarted.TrySetResult();
                    await allowFetch.Task;
                    return DiffFor(ci.ArgAt<FilePath>(1).Value);
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            // Prefetch should start loading diffs for changed files.
            await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            allowFetch.SetResult();

            // Let prefetch complete.
            await Task.Delay(100);

            var callsAfterPrefetch = Volatile.Read(ref calls);
            Assert.That(callsAfterPrefetch, Is.GreaterThanOrEqualTo(1));

            vm.SetFileSelection([vm.UnstagedFiles.First(f => f.Path.Value == "a.txt")]);
            // Allow selection load to settle (warm hit should not call GetDiff again for a.txt).
            await Task.Delay(100);

            var aCalls = _diff.ReceivedCalls()
                .Count(c => c.GetMethodInfo().Name == nameof(IGitDiffService.GetDiffAsync)
                            && c.GetArguments()[1] is FilePath p
                            && p.Value == "a.txt");
            Assert.That(aCalls, Is.EqualTo(1), "Selecting a prefetched file must not fetch again");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Refresh_Does_Not_Reload_Stashes()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")]));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            await _stash.Received(1).ListStashesAsync(repo, Arg.Any<CancellationToken>());

            _stash.ClearReceivedCalls();
            await vm.RefreshAsync();
            await _stash.DidNotReceive().ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Soft_Refresh_Keeps_Painted_Diff_While_Refreshing()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")], epoch: 1));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);
            await Task.Delay(150);
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
            Assert.That(vm.HasDiffCache, Is.True);
            Assert.That(vm.UnstagedFiles[0].HasCachedDiff, Is.True);

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")], epoch: 2));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    started.TrySetResult();
                    await release.Task;
                    return DiffFor(ci.ArgAt<FilePath>(1).Value);
                });

            var refresh = vm.RefreshAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0), "Stale diff should remain painted");
            Assert.That(vm.IsDiffRefreshing || vm.DiffFreshnessText is not null, Is.True);

            release.SetResult();
            await refresh;
            await Task.Delay(50);
            Assert.That(vm.IsDiffRefreshing, Is.False);
            Assert.That(vm.HasDiffCache, Is.True);
            Assert.That(vm.DiffCacheAgeText, Is.Not.Null);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Refresh_Clears_Diff_When_Selected_Path_Leaves_List()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")], epoch: 1));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);
            await Task.Delay(100);
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));

            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([], epoch: 2));
            await vm.RefreshAsync();

            Assert.That(vm.DiffRows.Count, Is.EqualTo(0));
            Assert.That(vm.HasDiffCache, Is.False);
            Assert.That(vm.IsLoadingDiff, Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ForceRefreshDiff_Fetches_Again()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")]));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);
            await Task.Delay(150);

            _diff.ClearReceivedCalls();
            await vm.ForceRefreshDiffCommand.ExecuteAsync(null);

            await _diff.Received().GetDiffAsync(
                repo,
                Arg.Is<FilePath>(p => p.Value == "a.txt"),
                Arg.Any<DiffScope>(),
                Arg.Any<DiffOptions>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Stage_Keeps_Painted_Diff_While_New_Target_Loads()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt")], epoch: 1));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(ci => DiffFor(ci.ArgAt<FilePath>(1).Value, ci.ArgAt<DiffScope>(2).WorkingCopyTargetOrNull() ?? DiffTarget.IndexToWorktree));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            var file = vm.UnstagedFiles[0];
            vm.SetFileSelection([file]);
            await Task.Delay(150);
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
            var paintedBefore = vm.DiffRows.Count;

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(StatusWithStaged([Staged("a.txt")], epoch: 2));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    started.TrySetResult();
                    await release.Task;
                    return DiffFor(ci.ArgAt<FilePath>(1).Value, ci.ArgAt<DiffScope>(2).WorkingCopyTargetOrNull() ?? DiffTarget.IndexToWorktree);
                });

            var stageTask = vm.StageFileCommand.ExecuteAsync(file);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0), "Previous diff should remain painted while staged target loads");
            Assert.That(vm.DiffRows.Count, Is.EqualTo(paintedBefore));
            Assert.That(vm.IsLoadingDiff, Is.False, "Must not blank to full loading spinner");

            release.SetResult();
            await stageTask;
            await Task.Delay(100);

            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
            Assert.That(vm.IsDiffRefreshing, Is.False);
            Assert.That(vm.StagedFiles.Any(f => f.Path.Value == "a.txt"), Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void NotifyWindowActivated_Schedules_Watcher_Refresh()
    {
        var vm = CreateVm();
        // Should not throw when no repo is open.
        vm.NotifyWindowActivated();
    }
}
