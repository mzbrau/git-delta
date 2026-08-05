using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class PendingChangesReviewUxTests
{
    private IGitStatusService _status = null!;
    private IGitDiffService _diff = null!;
    private IGitBranchService _branches = null!;
    private IGitStashService _stash = null!;
    private IGitHistoryService _history = null!;
    private IGitDiscardService _discard = null!;
    private ISettingsStore _settings = null!;
    private NotificationService _notifications = null!;
    private AlwaysConfirmDialog _confirm = null!;
    private IRepositoryWatcher _watcher = null!;
    private ILocalCommentStore _localComments = null!;

    [SetUp]
    public void SetUp()
    {
        _status = Substitute.For<IGitStatusService>();
        _diff = Substitute.For<IGitDiffService>();
        _branches = Substitute.For<IGitBranchService>();
        _stash = Substitute.For<IGitStashService>();
        _history = Substitute.For<IGitHistoryService>();
        _discard = Substitute.For<IGitDiscardService>();
        _settings = Substitute.For<ISettingsStore>();
        _notifications = new NotificationService();
        _confirm = new AlwaysConfirmDialog();
        _watcher = Substitute.For<IRepositoryWatcher>();
        _localComments = Substitute.For<ILocalCommentStore>();
        _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _settings.Current.Returns(new AppSettings());
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _discard.RecentlyDiscarded.Returns([]);
    }

    [TearDown]
    public void TearDown() => _watcher.Dispose();

    private WorkingCopyViewModel CreateVm() =>
        new(_status, _diff, Substitute.For<IGitStagingService>(), _discard,
            Substitute.For<IGitObjectReader>(), Substitute.For<IGitCommitService>(),
            _branches, Substitute.For<IGitRemoteService>(),
            Substitute.For<IGitConflictService>(), _stash, _history,
            _settings, _notifications, _confirm,
            new FakeStashDialog(new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true)),
            new IntraLineDiffer(), Substitute.For<IFsmonitorService>(), _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()));

    private static StatusEntry Unstaged(string path, string? worktreeOid = null) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: false, IsUnstaged: true, IsConflicted: false,
            WorktreeOid: worktreeOid is null ? null : new ContentId(worktreeOid));

    private static RepositoryStatus Status(IReadOnlyList<StatusEntry> unstaged, long epoch = 1) =>
        new([], unstaged, [], InProgressOperation.None, "main", epoch);

    private static FileDiff SampleDiff(string path = "a.txt")
    {
        var patch =
            $"""
            diff --git a/{path} b/{path}
            --- a/{path}
            +++ b/{path}
            @@ -1 +1 @@
            -old
            +new
            """;
        return PatchParser.Parse(patch, DiffTarget.IndexToWorktree);
    }

    private static string NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        return repo;
    }

    [Test]
    public async Task SelectComments_Clears_File_Selection()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt", "oid1")]));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(SampleDiff());

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            Assert.That(vm.UnstagedFiles, Is.Not.Empty);
            vm.SetFileSelection([vm.UnstagedFiles[0]]);

            for (var i = 0; i < 40 && vm.SelectedFile is null; i++)
                await Task.Delay(25);

            Assert.That(vm.SelectedFile, Is.Not.Null);

            vm.PendingReview.SelectCommentsCommand.Execute(null);

            Assert.That(vm.PendingReview.IsCommentsSelected, Is.True);
            Assert.That(vm.SelectedFile, Is.Null);
            Assert.That(vm.DiffEmptyMessage, Is.EqualTo("Pending changes context"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task OnFileSelectionChanged_Raises_AiChatSelectedFileLabel()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("src/Foo.cs", "oid1")]));
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(SampleDiff("src/Foo.cs"));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            Assert.That(vm.PendingReview.AiChatSelectedFileLabel, Is.EqualTo("No file selected"));

            var raised = false;
            vm.PendingReview.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PendingChangesReviewViewModel.AiChatSelectedFileLabel))
                    raised = true;
            };

            vm.SetFileSelection([vm.UnstagedFiles[0]]);
            for (var i = 0; i < 40 && !raised; i++)
                await Task.Delay(25);

            Assert.That(raised, Is.True);
            Assert.That(vm.PendingReview.AiChatSelectedFileLabel, Does.Contain("Foo.cs"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LocalComments_Update_PerFile_Unresolved_Count()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt", "oid1")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.LocalComments.Add(new LocalCommentItemViewModel(new LocalCommentRecord(
                Id: "1",
                RepositoryKey: "repo",
                Path: "a.txt",
                StartLine: 1,
                EndLine: 1,
                Side: DiffSide.New,
                Body: "fix",
                IsResolved: false,
                ContentId: null,
                CreatedUtc: DateTimeOffset.UtcNow,
                UpdatedUtc: DateTimeOffset.UtcNow)));
            vm.PendingReview.UpdateFileUnresolvedCommentCounts();

            Assert.That(vm.UnstagedFiles[0].UnresolvedThreadCount, Is.EqualTo(1));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    private static LocalCommentItemViewModel Comment(string id, string path, bool resolved = false) =>
        new(new LocalCommentRecord(
            Id: id,
            RepositoryKey: "repo",
            Path: path,
            StartLine: 1,
            EndLine: 1,
            Side: DiffSide.New,
            Body: "fix",
            IsResolved: resolved,
            ContentId: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow));

    [Test]
    public async Task SyncReviewState_Prunes_Orphan_Comments_When_Working_Copy_Empty()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.LocalComments.Add(Comment("c1", "a.txt"));
            vm.PendingReview.LocalComments.Add(Comment("c2", "b.txt", resolved: true));
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(1));

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: false);

            Assert.That(vm.PendingReview.LocalComments, Is.Empty);
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(0));
            await _localComments.Received(1).DeleteAsync("c1", Arg.Any<CancellationToken>());
            await _localComments.Received(1).DeleteAsync("c2", Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SyncReviewState_Keeps_Comments_On_Still_Pending_Paths()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("keep.txt", "oid1")], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.LocalComments.Add(Comment("keep", "keep.txt"));
            vm.PendingReview.LocalComments.Add(Comment("gone", "gone.txt"));

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: false);

            Assert.That(vm.PendingReview.LocalComments.Select(c => c.Id), Is.EqualTo(new[] { "keep" }));
            Assert.That(vm.PendingReview.UnresolvedCommentCount, Is.EqualTo(1));
            await _localComments.Received(1).DeleteAsync("gone", Arg.Any<CancellationToken>());
            await _localComments.DidNotReceive().DeleteAsync("keep", Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SyncReviewState_Clears_Ai_On_Commit_Even_When_Files_Remain()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt", "oid1")], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.AiRunState = AiRunState.Complete;
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("Re-run AI review"));

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: true);

            Assert.That(vm.PendingReview.AiRunState, Is.EqualTo(AiRunState.Idle));
            Assert.That(vm.PendingReview.HasAiRun, Is.False);
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("AI review"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SyncReviewState_Leaves_Ai_On_Refresh_When_Files_Remain()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt", "oid1")], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.AiRunState = AiRunState.Complete;
            vm.PendingReview.LocalComments.Add(Comment("gone", "gone.txt"));

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: false);

            Assert.That(vm.PendingReview.AiRunState, Is.EqualTo(AiRunState.Complete));
            Assert.That(vm.PendingReview.HasAiRun, Is.True);
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("Re-run AI review"));
            Assert.That(vm.PendingReview.LocalComments, Is.Empty);
            await _localComments.Received(1).DeleteAsync("gone", Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SyncReviewState_Clears_Ai_When_Working_Copy_Empty()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            vm.PendingReview.AiRunState = AiRunState.Complete;

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: false);

            Assert.That(vm.PendingReview.AiRunState, Is.EqualTo(AiRunState.Idle));
            Assert.That(vm.PendingReview.HasAiRun, Is.False);
            Assert.That(vm.PendingReview.AiButtonLabel, Is.EqualTo("AI review"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Status_Refresh_Preserves_AiChangeClassification_On_File_Rows()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(
                    Status([Unstaged("a.txt", "oid1")], epoch: 1),
                    Status([Unstaged("a.txt", "oid2")], epoch: 2));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var file = vm.UnstagedFiles.Single();
            file.AiChangeClassification = AiChangeClassification.NewFeature;
            vm.PendingReview.AiRunState = AiRunState.Complete;

            await vm.RefreshAsync();

            var refreshed = vm.UnstagedFiles.Single(f => f.Path.Value == "a.txt");
            Assert.That(refreshed.AiChangeClassification, Is.EqualTo(AiChangeClassification.NewFeature));
            Assert.That(refreshed.HasAiChangeClassification, Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task SyncReviewState_ClearAi_Clears_File_Classifications()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status([Unstaged("a.txt", "oid1")], epoch: 1));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var file = vm.UnstagedFiles.Single();
            file.AiChangeClassification = AiChangeClassification.BugFix;
            vm.PendingReview.AiRunState = AiRunState.Complete;

            await vm.PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReview: true);

            Assert.That(vm.PendingReview.AiRunState, Is.EqualTo(AiRunState.Idle));
            Assert.That(file.AiChangeClassification, Is.Null);
            Assert.That(file.HasAiChangeClassification, Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
