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

public sealed class WorkingCopyViewModelHistoryCacheTests
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
    private IGitProcessRunner _runner = null!;
    private NotificationService _notifications = null!;
    private AlwaysConfirmDialog _confirm = null!;
    private FakeStashDialog _stashDialog = null!;
    private GitRepositoryWatcher _watcher = null!;

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
        _runner = Substitute.For<IGitProcessRunner>();
        _notifications = new NotificationService();
        _confirm = new AlwaysConfirmDialog();
        _stashDialog = new FakeStashDialog(
            new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true));
        _watcher = new GitRepositoryWatcher();

        _settings.Current.Returns(new AppSettings());
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _history.GetCommitFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _discard.RecentlyDiscarded.Returns([]);
        _status.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private WorkingCopyViewModel CreateVm() =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, _stash, _history, _settings, _notifications, _confirm, _stashDialog,
            new IntraLineDiffer(), _runner, _watcher);

    private static CommitInfo Commit(string oid, string subject) =>
        new(oid, oid[..Math.Min(7, oid.Length)], subject, "", "Test", "test@example.com",
            DateTimeOffset.UtcNow, [], []);

    private static StatusEntry Unstaged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: false, IsUnstaged: true, IsConflicted: false);

    private static RepositoryStatus Status(IReadOnlyList<StatusEntry> unstaged, long epoch = 1) =>
        new([], unstaged, [], InProgressOperation.None, "main", epoch);

    [Test]
    public async Task Delayed_Commit_Files_Load_Does_Not_Overwrite_File_Status_Selection()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var commit = Commit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "one");
            _history.ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([commit]);
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("MainWindow.axaml"), Unstaged("WorkingCopyViewModel.cs")]));

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<(FilePath Path, ChangeKind Kind)> commitFiles =
            [
                (FilePath.From("hist-a.txt"), ChangeKind.Modified),
                (FilePath.From("hist-b.txt"), ChangeKind.Modified),
            ];
            _history.GetCommitFilesAsync(repo, commit.Oid, Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    started.TrySetResult();
                    await release.Task;
                    ci.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                    return commitFiles;
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            Assert.That(vm.UnstagedFiles.Count, Is.EqualTo(2));

            vm.SelectHistoryCommand.Execute(null);
            await Task.Delay(100);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));

            vm.SelectCommit(commit);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

            vm.SelectFileStatusCommand.Execute(null);
            Assert.That(vm.IsFileStatusMode, Is.True);

            var statusFile = vm.UnstagedFiles.First(f => f.Path.Value == "MainWindow.axaml");
            vm.SetFileSelection([statusFile]);
            Assert.That(vm.SelectedFileCount, Is.EqualTo(1));
            Assert.That(vm.SelectedFile?.Path.Value, Is.EqualTo("MainWindow.axaml"));

            release.SetResult();
            await Task.Delay(150);

            Assert.That(vm.IsFileStatusMode, Is.True);
            Assert.That(vm.SelectedFileCount, Is.EqualTo(1), "Late commit-files load must not change selection count");
            Assert.That(vm.SelectedFile?.Path.Value, Is.EqualTo("MainWindow.axaml"));
            Assert.That(vm.DiffOverlayMessage, Is.Null.Or.Not.Contain("files selected"));
            Assert.That(vm.HistoryFiles.Count, Is.EqualTo(0), "History files must not be applied after leaving History");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Revisit_History_Keeps_Cached_List_Immediately()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var commits = new List<CommitInfo> { Commit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "one") };
            _history.ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(commits);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SelectHistoryCommand.Execute(null);
            await Task.Delay(100);

            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));
            Assert.That(vm.IsHistoryLoading, Is.False);

            _history.ClearReceivedCalls();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _history.ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async _ci =>
                {
                    started.TrySetResult();
                    await release.Task;
                    return (IReadOnlyList<CommitInfo>)commits;
                });

            vm.SelectFileStatusCommand.Execute(null);
            Assert.That(vm.IsFileStatusMode, Is.True);
            // Cache retained while away from History.
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));

            vm.SelectHistoryCommand.Execute(null);
            Assert.That(vm.IsHistoryMode, Is.True);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1), "Cached commits must paint immediately");
            Assert.That(vm.IsHistoryLoading, Is.False);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(vm.IsHistoryRefreshing, Is.True);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1), "Soft refresh must not clear the list");

            release.SetResult();
            await Task.Delay(100);
            Assert.That(vm.IsHistoryRefreshing, Is.False);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));
            await _history.Received().ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Soft_Refresh_Keeps_Paint_While_ListCommits_In_Flight()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var first = new List<CommitInfo> { Commit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "old tip") };
            var second = new List<CommitInfo>
            {
                Commit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "new tip"),
                Commit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "old tip"),
            };

            _history.ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(first);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SelectHistoryCommand.Execute(null);
            await Task.Delay(100);
            Assert.That(vm.HistoryCommits[0].Subject, Is.EqualTo("old tip"));

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _history.ListCommitsAsync(repo, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async _ci =>
                {
                    started.TrySetResult();
                    await release.Task;
                    return (IReadOnlyList<CommitInfo>)second;
                });

            vm.NotifyWindowActivated();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(vm.IsHistoryRefreshing, Is.True);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));
            Assert.That(vm.HistoryCommits[0].Subject, Is.EqualTo("old tip"));

            release.SetResult();
            await Task.Delay(100);
            Assert.That(vm.IsHistoryRefreshing, Is.False);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(2));
            Assert.That(vm.HistoryCommits[0].Subject, Is.EqualTo("new tip"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task OpenAsync_Hard_Clears_History_Cache()
    {
        var repo1 = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        var repo2 = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo1);
        Directory.CreateDirectory(repo2);
        try
        {
            _history.ListCommitsAsync(repo1, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([Commit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "one")]);
            _status.GetStatusAsync(repo1, Arg.Any<CancellationToken>())
                .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));
            _status.GetStatusAsync(repo2, Arg.Any<CancellationToken>())
                .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo1);
            vm.SelectHistoryCommand.Execute(null);
            await Task.Delay(100);
            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(1));

            _history.ListCommitsAsync(repo2, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<CommitInfo>());
            await vm.OpenAsync(repo2);

            Assert.That(vm.HistoryCommits.Count, Is.EqualTo(0));
            Assert.That(vm.SelectedCommit, Is.Null);
            Assert.That(vm.IsHistoryRefreshing, Is.False);
            Assert.That(vm.IsHistoryLoading, Is.False);
        }
        finally
        {
            try { Directory.Delete(repo1, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(repo2, recursive: true); } catch { /* best effort */ }
        }
    }
}
