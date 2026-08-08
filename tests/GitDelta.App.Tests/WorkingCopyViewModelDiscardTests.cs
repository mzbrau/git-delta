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

public sealed class AlwaysConfirmDialog(bool result = true) : IConfirmDialog
{
    public int CallCount { get; private set; }
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Discard")
    {
        CallCount++;
        return Task.FromResult(result);
    }
}

public sealed class FakeStashDialog(StashDialogResult? result) : IStashDialog
{
    public int CallCount { get; private set; }
    public Task<StashDialogResult?> ShowAsync()
    {
        CallCount++;
        return Task.FromResult(result);
    }
}

public sealed class FakeCheckoutBlockedDialog(CheckoutBlockedChoice choice) : ICheckoutBlockedDialog
{
    public int CallCount { get; private set; }
    public string? LastTargetRef { get; private set; }

    public Task<CheckoutBlockedChoice> ShowAsync(string targetRef)
    {
        CallCount++;
        LastTargetRef = targetRef;
        return Task.FromResult(choice);
    }
}

public sealed class WorkingCopyViewModelDiscardTests
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

    private WorkingCopyViewModel CreateVm(IConfirmDialog? confirm = null, IStashDialog? stashDialog = null) =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes, _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history,
            _settings, _notifications, confirm ?? _confirm, stashDialog ?? _stashDialog, new IntraLineDiffer(), _fsmonitor, _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, confirm ?? _confirm, _notifications, Substitute.For<IGitHistoryService>()));

    private static StatusEntry Unstaged(string path, ChangeKind kind = ChangeKind.Modified) =>
        new(FilePath.From(path), null, kind, IsStaged: false, IsUnstaged: true, IsConflicted: false);

    private static StatusEntry Staged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false);

    private static RepositoryStatus Status(
        IReadOnlyList<StatusEntry>? staged = null,
        IReadOnlyList<StatusEntry>? unstaged = null,
        long epoch = 1) =>
        new(staged ?? [], unstaged ?? [], [], InProgressOperation.None, "main", epoch);

    [Test]
    public async Task DiscardSelected_Discards_Unstaged_And_Staged()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(
                    staged: [Staged("staged.txt")],
                    unstaged: [Unstaged("work.txt"), Unstaged("other.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.UnstagedFiles, Has.Count.EqualTo(2));
            Assert.That(vm.StagedFiles, Has.Count.EqualTo(1));

            vm.SetFileSelection([vm.UnstagedFiles[0], vm.StagedFiles[0]]);
            Assert.That(vm.CanDiscardSelection, Is.True);

            await vm.DiscardSelectedFilesCommand.ExecuteAsync(null);

            Assert.That(_confirm.CallCount, Is.EqualTo(1));
            await _discard.Received(1).DiscardFileAsync(repo, FilePath.From("work.txt"), Arg.Any<CancellationToken>());
            await _discard.Received(1).DiscardStagedFileAsync(repo, FilePath.From("staged.txt"), Arg.Any<CancellationToken>());
            await _discard.DidNotReceive().DiscardFileAsync(repo, FilePath.From("staged.txt"), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DiscardSelected_Staged_Only_Calls_DiscardStaged()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("staged.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.StagedFiles[0]]);
            Assert.That(vm.CanDiscardSelection, Is.True);

            await vm.DiscardSelectedFilesCommand.ExecuteAsync(null);

            await _discard.Received(1).DiscardStagedFileAsync(repo, FilePath.From("staged.txt"), Arg.Any<CancellationToken>());
            await _discard.DidNotReceive().DiscardFileAsync(Arg.Any<string>(), Arg.Any<FilePath>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DiscardSelected_Cancelled_Does_Not_Call_Discard()
    {
        var confirm = new AlwaysConfirmDialog(result: false);
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged: [Unstaged("work.txt")]));

            var vm = new WorkingCopyViewModel(
                _status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes, _conflicts, Substitute.For<IGitRebaseService>(), _stash, _history,
                _settings, _notifications, confirm, _stashDialog, new IntraLineDiffer(), _fsmonitor, _watcher,
                new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, confirm, _notifications, Substitute.For<IGitHistoryService>()));
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);

            await vm.DiscardSelectedFilesCommand.ExecuteAsync(null);

            await _discard.DidNotReceive().DiscardFileAsync(Arg.Any<string>(), Arg.Any<FilePath>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task StageSelected_Stages_Unstaged_Files()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged: [Unstaged("a.txt"), Unstaged("b.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0], vm.UnstagedFiles[1]]);

            await vm.StageSelectedCommand.ExecuteAsync(null);

            await _staging.Received(1).StageFilesAsync(
                repo,
                Arg.Is<IReadOnlyList<FilePath>>(p => p.Count == 2),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DiscardHunk_Calls_DiscardPatch_When_Worktree_Diff()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var file = FilePath.From("a.txt");
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged: [Unstaged("a.txt")]));

            var patch =
                """
                diff --git a/a.txt b/a.txt
                --- a/a.txt
                +++ b/a.txt
                @@ -1,2 +1,2 @@
                 keep
                -x
                +y
                """;
            var fileDiff = PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
            _diff.GetDiffAsync(repo, file, DiffTarget.IndexToWorktree.AsWorkingCopy(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(fileDiff);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);

            for (var i = 0; i < 100 && (vm.IsLoadingDiff || !vm.CanDiscardLines); i++)
                await Task.Delay(10);

            Assert.That(vm.CanDiscardLines, Is.True);

            await vm.DiscardHunkAtAsync(0);

            await _discard.Received(1).DiscardPatchAsync(repo, Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
