using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Diff;
using GitDelta.Git;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class FileListSelectionHelperTests
{
    [Test]
    public void IsUnderFolder_Matches_Exact_And_Descendants()
    {
        Assert.That(FileListSelectionHelper.IsUnderFolder("src/App", "src/App"), Is.True);
        Assert.That(FileListSelectionHelper.IsUnderFolder("src/App/a.cs", "src/App"), Is.True);
        Assert.That(FileListSelectionHelper.IsUnderFolder("src/AppExtra/a.cs", "src/App"), Is.False);
        Assert.That(FileListSelectionHelper.IsUnderFolder("src/Other/a.cs", "src/App"), Is.False);
    }

    [Test]
    public void IsUnderFolder_Supports_Compressed_Tree_Keys()
    {
        // FileTreeBuilder may compress single-child chains into keys like "src/GitDelta.App".
        Assert.That(
            FileListSelectionHelper.IsUnderFolder("src/GitDelta.App/Views/MainWindow.axaml", "src/GitDelta.App"),
            Is.True);
    }

    [Test]
    public void CollectFromEntries_Expands_Folder_Against_Source_List()
    {
        var a = FileItemViewModel.From(
            new StatusEntry(FilePath.From("src/a.cs"), null, ChangeKind.Modified, false, true, false),
            isStagedList: false);
        var b = FileItemViewModel.From(
            new StatusEntry(FilePath.From("src/b.cs"), null, ChangeKind.Modified, false, true, false),
            isStagedList: false);
        var other = FileItemViewModel.From(
            new StatusEntry(FilePath.From("docs/readme.md"), null, ChangeKind.Modified, false, true, false),
            isStagedList: false);

        var folder = new FileListEntry(0, "src", "src", isExpanded: true);
        var into = new List<FileItemViewModel>();
        FileListSelectionHelper.CollectFromEntries([folder], [a, b, other], into);

        Assert.That(into.Select(f => f.Path.Value), Is.EquivalentTo(new[] { "src/a.cs", "src/b.cs" }));
    }

    [Test]
    public void CollectFromEntries_Dedupes_Folder_And_Child()
    {
        var a = FileItemViewModel.From(
            new StatusEntry(FilePath.From("src/a.cs"), null, ChangeKind.Modified, false, true, false),
            isStagedList: false);
        var folder = new FileListEntry(0, "src", "src", isExpanded: true);
        var fileRow = new FileListEntry(1, "a.cs", a);

        var into = new List<FileItemViewModel>();
        FileListSelectionHelper.CollectFromEntries([folder, fileRow], [a], into);

        Assert.That(into, Has.Count.EqualTo(1));
        Assert.That(into[0].Path.Value, Is.EqualTo("src/a.cs"));
    }
}

public sealed class WorkingCopyFolderStagingTests
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

    private static StatusEntry Unstaged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: false, IsUnstaged: true, IsConflicted: false);

    private static StatusEntry Staged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false);

    private static RepositoryStatus Status(
        IReadOnlyList<StatusEntry>? staged = null,
        IReadOnlyList<StatusEntry>? unstaged = null,
        long epoch = 1) =>
        new(staged ?? [], unstaged ?? [], [], InProgressOperation.None, "main", epoch);

    [Test]
    public async Task StageSelected_With_Folder_Descendants_Batches_One_Git_Call()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged:
                [
                    Unstaged("src/a.cs"),
                    Unstaged("src/b.cs"),
                    Unstaged("docs/readme.md"),
                ]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var underSrc = FileListSelectionHelper.FilesUnderFolder(vm.UnstagedFiles, "src").ToList();
            Assert.That(underSrc, Has.Count.EqualTo(2));
            vm.SetFileSelection(underSrc);

            await vm.StageSelectedCommand.ExecuteAsync(null);

            await _staging.Received(1).StageFilesAsync(
                repo,
                Arg.Is<IReadOnlyList<FilePath>>(p =>
                    p.Count == 2
                    && p.Any(x => x.Value == "src/a.cs")
                    && p.Any(x => x.Value == "src/b.cs")),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ToggleFileStaged_Rapid_Clicks_Coalesce_Into_One_Batch()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var tcs = new TaskCompletionSource();
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged: [Unstaged("a.txt"), Unstaged("b.txt")]));
            _staging.StageFilesAsync(repo, Arg.Any<IReadOnlyList<FilePath>>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await tcs.Task;
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var a = vm.UnstagedFiles.First(f => f.Path.Value == "a.txt");
            var b = vm.UnstagedFiles.First(f => f.Path.Value == "b.txt");

            await vm.ToggleFileStagedCommand.ExecuteAsync(a);
            await vm.ToggleFileStagedCommand.ExecuteAsync(b);

            // Optimistic UI should already reflect both pending stages.
            Assert.That(vm.StagedFiles.Any(f => f.Path.Value == "a.txt"), Is.True);
            Assert.That(vm.StagedFiles.Any(f => f.Path.Value == "b.txt"), Is.True);

            // Wait past debounce for the coalesced flush to start.
            for (var i = 0; i < 50; i++)
            {
                if (_staging.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IGitStagingService.StageFilesAsync)))
                    break;
                await Task.Delay(20);
            }

            await _staging.Received(1).StageFilesAsync(
                repo,
                Arg.Is<IReadOnlyList<FilePath>>(p => p.Count == 2),
                Arg.Any<CancellationToken>());

            // Status refresh should not have run until git completes.
            await _status.Received(1).GetStatusAsync(repo, Arg.Any<CancellationToken>());

            tcs.SetResult();
            for (var i = 0; i < 50; i++)
            {
                if (_status.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IGitStatusService.GetStatusAsync)) >= 2)
                    break;
                await Task.Delay(20);
            }

            await _status.Received(2).GetStatusAsync(repo, Arg.Any<CancellationToken>());
            await _staging.Received(1).StageFilesAsync(
                repo,
                Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ToggleFileStaged_Opposite_Before_Flush_Nets_Out()
    {
        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(unstaged: [Unstaged("a.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var a = vm.UnstagedFiles.First(f => f.Path.Value == "a.txt");
            await vm.ToggleFileStagedCommand.ExecuteAsync(a);

            // After optimistic stage the file appears in staged list.
            var stagedA = vm.StagedFiles.First(f => f.Path.Value == "a.txt");
            await vm.ToggleFileStagedCommand.ExecuteAsync(stagedA);

            await Task.Delay(250);

            await _staging.DidNotReceive().StageFilesAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<CancellationToken>());
            await _staging.DidNotReceive().UnstageFilesAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<FilePath>>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
