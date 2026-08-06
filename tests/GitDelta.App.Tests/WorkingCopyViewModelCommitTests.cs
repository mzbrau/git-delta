using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class WorkingCopyViewModelCommitTests
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

    private WorkingCopyViewModel CreateVm() =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history, _settings, _notifications, _confirm, _stashDialog,
            new IntraLineDiffer(), _fsmonitor, _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()));

    private static StatusEntry Staged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false);

    private static RepositoryStatus Status(
        IReadOnlyList<StatusEntry>? staged = null,
        IReadOnlyList<StatusEntry>? unstaged = null,
        long epoch = 1) =>
        new(staged ?? [], unstaged ?? [], [], InProgressOperation.None, "main", epoch);

    private static string NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        return repo;
    }

    [Test]
    public async Task Overlapping_Commit_Only_Invokes_Git_Once()
    {
        var repo = NewRepo();
        try
        {
            // Open sees staged files; post-commit refresh sees a clean tree.
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    Status(staged: [Staged("a.txt")], epoch: 1),
                    Status(epoch: 2));

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commitCalls = 0;
            _commit.CommitAsync(
                    repo,
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    Interlocked.Increment(ref commitCalls);
                    started.TrySetResult();
                    await release.Task;
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.CommitMessage = "test commit";

            Assert.That(vm.CanCommit, Is.True);

            var first = vm.CommitCommand.ExecuteAsync(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(vm.IsCommitting, Is.True);
            Assert.That(vm.CanCommit, Is.False);
            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);

            // Overlapping execute must be a no-op while the first commit is in flight.
            var second = vm.CommitCommand.ExecuteAsync(null);
            await second;

            release.SetResult();
            await first;

            Assert.That(commitCalls, Is.EqualTo(1));
            await _commit.Received(1).CommitAsync(
                repo,
                "test commit",
                false,
                false,
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());
            Assert.That(vm.IsCommitting, Is.False);
            Assert.That(vm.StagedFiles, Is.Empty);
            Assert.That(
                _notifications.Notifications.Any(n => n.Message.StartsWith("Commit failed:", StringComparison.Ordinal)),
                Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Commit_RefreshFailure_Is_Not_Labeled_As_Commit_Failure()
    {
        var repo = NewRepo();
        try
        {
            var statusCalls = 0;
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    statusCalls++;
                    if (statusCalls == 1)
                        return Task.FromResult(Status(staged: [Staged("a.txt")], epoch: 1));
                    throw new InvalidOperationException("status boom");
                });

            _commit.CommitAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.CommitMessage = "test commit";

            await vm.CommitCommand.ExecuteAsync(null);

            await _commit.Received(1).CommitAsync(
                repo,
                "test commit",
                false,
                false,
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());
            Assert.That(vm.CommitMessage, Is.Empty);
            Assert.That(
                _notifications.Notifications.Any(n => n.Message.StartsWith("Commit failed:", StringComparison.Ordinal)),
                Is.False);
            Assert.That(
                _notifications.Notifications.Any(n =>
                    n.IsError && n.Message.StartsWith("Failed to refresh after commit:", StringComparison.Ordinal)),
                Is.True);
            Assert.That(
                _notifications.Notifications.First(n => n.Message.StartsWith("Failed to refresh after commit:", StringComparison.Ordinal)).Action,
                Is.Null,
                "Refresh failure must not offer a Retry that re-commits");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Commit_Success_Clears_Staged_Files()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    Status(staged: [Staged("a.txt"), Staged("b.txt")], epoch: 1),
                    Status(epoch: 2));

            _commit.CommitAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            Assert.That(vm.StagedFiles, Has.Count.EqualTo(2));
            vm.CommitMessage = "ship it";

            await vm.CommitCommand.ExecuteAsync(null);

            Assert.That(vm.StagedFiles, Is.Empty);
            Assert.That(vm.ShowCommitDock, Is.False);
            Assert.That(vm.CanCommit, Is.False);
            Assert.That(_notifications.Notifications.Any(n => n.IsError), Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task CommitMessage_Change_Raises_CanCommit_And_Enables_Commit()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.CanCommit, Is.False);
            Assert.That(vm.CommitCommand.CanExecute(null), Is.False);

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null)
                    changed.Add(e.PropertyName);
            };

            vm.CommitMessage = "ready to commit";

            Assert.That(changed, Does.Contain(nameof(WorkingCopyViewModel.CanCommit)));
            Assert.That(vm.CanCommit, Is.True);
            Assert.That(vm.CommitCommand.CanExecute(null), Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task IsCommitting_Change_Raises_CanCommit()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    Status(staged: [Staged("a.txt")], epoch: 1),
                    Status(staged: [Staged("a.txt")], epoch: 2));

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _commit.CommitAsync(
                    repo,
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    started.TrySetResult();
                    await release.Task;
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.CommitMessage = "in flight";

            var changed = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not null)
                    changed.Add(e.PropertyName);
            };

            var commit = vm.CommitCommand.ExecuteAsync(null);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(changed, Does.Contain(nameof(WorkingCopyViewModel.CanCommit)));
            Assert.That(vm.CanCommit, Is.False);

            changed.Clear();
            // Keep staged files after "commit" so CanCommit can become true again.
            release.SetResult();
            await commit;

            Assert.That(changed, Does.Contain(nameof(WorkingCopyViewModel.CanCommit)));
            Assert.That(vm.IsCommitting, Is.False);
            // Message cleared on success; CanCommit stays false until a new message.
            Assert.That(vm.CanCommit, Is.False);
            changed.Clear();
            vm.CommitMessage = "again";
            Assert.That(changed, Does.Contain(nameof(WorkingCopyViewModel.CanCommit)));
            Assert.That(vm.CanCommit, Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task FileListRebuild_Raises_SelectionClear_Before_Sync()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")], epoch: 1));

            var vm = CreateVm();
            var events = new List<string>();
            vm.SelectionClearRequested += () => events.Add("clear");
            vm.SelectionSyncRequested += () => events.Add("sync");

            await vm.OpenAsync(repo);
            Assert.That(events, Does.Contain("clear"));

            events.Clear();
            vm.SetFileSelection([vm.StagedFiles[0]]);
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")], epoch: 2));
            await vm.RefreshAsync();

            var clearIndex = events.IndexOf("clear");
            var syncIndex = events.IndexOf("sync");
            Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0), "SelectionClearRequested should fire on rebuild");
            Assert.That(syncIndex, Is.GreaterThanOrEqualTo(0), "SelectionSyncRequested should fire when selection is restored");
            Assert.That(clearIndex, Is.LessThan(syncIndex), "Clear must precede sync restore");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Commit_WithUnresolvedComments_Cancelled_DoesNotCommit()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")], epoch: 1));

            _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([
                    new LocalCommentRecord(
                        "c1", repo, "a.txt", 1, 1, DiffSide.New, "fix me",
                        IsResolved: false, ContentId: null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ]);

            var confirm = new AlwaysConfirmDialog(result: false);
            var vm = new WorkingCopyViewModel(
                _status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
                _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history, _settings, _notifications, confirm, _stashDialog,
                new IntraLineDiffer(), _fsmonitor, _watcher,
                new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, confirm, _notifications, Substitute.For<IGitHistoryService>()));

            await vm.OpenAsync(repo);
            await vm.PendingReview.RefreshLocalCommentsAsync();
            Assert.That(vm.PendingReview.HasUnresolvedComments, Is.True);

            vm.CommitMessage = "should not commit";
            await vm.CommitCommand.ExecuteAsync(null);

            await _commit.DidNotReceive().CommitAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<bool>(),
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());
            Assert.That(vm.CommitMessage, Is.EqualTo("should not commit"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Commit_WithUnresolvedComments_Confirmed_Proceeds()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    Status(staged: [Staged("a.txt")], epoch: 1),
                    Status(epoch: 2));

            _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([
                    new LocalCommentRecord(
                        "c1", repo, "a.txt", 1, 1, DiffSide.New, "fix me",
                        IsResolved: false, ContentId: null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ]);

            _commit.CommitAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            await vm.PendingReview.RefreshLocalCommentsAsync();
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(1));

            vm.CommitMessage = "commit anyway";
            await vm.CommitCommand.ExecuteAsync(null);

            await _commit.Received(1).CommitAsync(
                repo,
                "commit anyway",
                false,
                false,
                Arg.Any<IProgress<string>?>(),
                Arg.Any<CancellationToken>());

            // Post-commit evaluation prunes comments for the committed path and resets the counter.
            await _localComments.Received(1).DeleteAsync("c1", Arg.Any<CancellationToken>());
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(0));
            Assert.That(vm.PendingReview.LocalComments, Is.Empty);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Commit_Partial_Clears_Ai_And_Prunes_Committed_Path_Comments()
    {
        var repo = NewRepo();
        try
        {
            var remain = new StatusEntry(
                FilePath.From("remain.txt"), null, ChangeKind.Modified,
                IsStaged: false, IsUnstaged: true, IsConflicted: false);

            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    new RepositoryStatus(
                        [Staged("committed.txt")],
                        [remain],
                        [], InProgressOperation.None, "main", 1),
                    new RepositoryStatus(
                        [],
                        [remain],
                        [], InProgressOperation.None, "main", 2));

            _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns([
                    new LocalCommentRecord(
                        "gone", repo, "committed.txt", 1, 1, DiffSide.New, "on committed",
                        IsResolved: false, ContentId: null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new LocalCommentRecord(
                        "keep", repo, "remain.txt", 2, 2, DiffSide.New, "on remain",
                        IsResolved: false, ContentId: null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ]);

            _commit.CommitAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<bool>(),
                    Arg.Any<bool>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            await vm.PendingReview.RefreshLocalCommentsAsync();

            vm.PendingReview.AiRunState = AiRunState.Complete;
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(2));
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("Re-run AI review"));

            vm.CommitMessage = "partial";
            await vm.CommitCommand.ExecuteAsync(null);

            await _localComments.Received(1).DeleteAsync("gone", Arg.Any<CancellationToken>());
            await _localComments.DidNotReceive().DeleteAsync("keep", Arg.Any<CancellationToken>());
            Assert.That(vm.PendingReview.LocalComments.Select(c => c.Id), Is.EqualTo(new[] { "keep" }));
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(1));
            Assert.That(vm.PendingReview.AiRunState, Is.EqualTo(AiRunState.Idle));
            Assert.That(vm.PendingReview.HasAiRun, Is.False);
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("AI review"));
            Assert.That(vm.UnstagedFiles.Select(f => f.Path.Value), Is.EqualTo(new[] { "remain.txt" }));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
