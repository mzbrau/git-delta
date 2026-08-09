using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.TestSupport;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class WorkingCopyRecentSelectionTests
{
    [Test]
    public async Task SelectRecentViewedFile_ClearsMultiSelect_AndShowsCleanRows()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("clean.txt", "hello\nworld\n")
            .WithInitialCommit("init")
            .WithFile("dirty.txt", "dirty\n")
            .WithCommit("add dirty");
        var path = repo.Build();
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "dirty\nchanged\n");

        var (vm, watcher, _) = CreateVm(path, staged: false, unstagedPath: "dirty.txt");
        try
        {
            await vm.OpenAsync(path);
            Assert.That(vm.UnstagedFiles, Has.Count.EqualTo(1));
            vm.SetFileSelection([vm.UnstagedFiles[0]]);
            Assert.That(vm.SelectedFileCount, Is.EqualTo(1));

            var recent = vm.RecentViewedFiles.Remember(
                FilePath.From("clean.txt"),
                new HashSet<string>(StringComparer.Ordinal) { "dirty.txt" });
            vm.SelectRecentViewedFile(recent);

            Assert.That(vm.SelectedFileCount, Is.EqualTo(1));
            Assert.That(vm.SelectedFile?.Path.Value, Is.EqualTo("clean.txt"));
            Assert.That(vm.HasStagedSelection, Is.False);
            Assert.That(vm.HasUnstagedSelection, Is.False);

            await WaitForDiffRowsAsync(vm);

            Assert.That(vm.IsLoadingDiff, Is.False);
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
            Assert.That(
                vm.DiffRows.Any(r => r.RightText.ToString().Contains("hello", StringComparison.Ordinal)),
                Is.True);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Test]
    public async Task SelectStagedAfterRecent_KeepsSelection_AndLoadsDiff()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("clean.txt", "hello\nworld\n")
            .WithInitialCommit("init")
            .WithFile("staged.txt", "staged\n")
            .WithCommit("add staged");
        var path = repo.Build();

        var (vm, watcher, diff) = CreateVm(path, staged: true, unstagedPath: "staged.txt");
        var sample = PatchParser.Parse(
            """
            diff --git a/staged.txt b/staged.txt
            --- a/staged.txt
            +++ b/staged.txt
            @@ -1 +1,2 @@
             staged
            +line
            """,
            DiffTarget.HeadToIndex);
        diff.GetDiffAsync(
                path,
                Arg.Any<FilePath>(),
                Arg.Any<DiffScope>(),
                Arg.Any<DiffOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(sample);

        try
        {
            await vm.OpenAsync(path);
            Assert.That(vm.StagedFiles, Has.Count.EqualTo(1));

            var recent = vm.RecentViewedFiles.Remember(
                FilePath.From("clean.txt"),
                new HashSet<string>(StringComparer.Ordinal) { "staged.txt" });
            vm.SelectRecentViewedFile(recent);
            await WaitForDiffRowsAsync(vm);

            vm.SetFileSelection([vm.StagedFiles[0]]);

            Assert.That(vm.SelectedFile?.Path.Value, Is.EqualTo("staged.txt"));
            Assert.That(vm.SelectedFile?.IsStagedList, Is.True);
            Assert.That(vm.HasStagedSelection, Is.True);

            await WaitForDiffRowsAsync(vm);

            Assert.That(vm.IsLoadingDiff, Is.False);
            Assert.That(vm.SelectedFile?.Path.Value, Is.EqualTo("staged.txt"));
            Assert.That(vm.DiffRows.Count, Is.GreaterThan(0));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Test]
    public async Task RefreshAsync_WhileBrowsingHistory_DoesNotExitBrowseMode()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("clean.txt", "hello\nworld\n")
            .WithInitialCommit("init")
            .WithFile("dirty.txt", "dirty\n")
            .WithCommit("add dirty");
        var path = repo.Build();
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "dirty\nchanged\n");

        var (vm, watcher, _) = CreateVm(path, staged: false, unstagedPath: "dirty.txt");
        try
        {
            await vm.OpenAsync(path);

            var recent = vm.RecentViewedFiles.Remember(
                FilePath.From("clean.txt"),
                new HashSet<string>(StringComparer.Ordinal) { "dirty.txt" });
            vm.SelectRecentViewedFile(recent);

            var item = new FileHistoryItemViewModel(new FileHistoryEntry(
                Oid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ShortOid: "aaaaaaa",
                Subject: "init",
                AuthorDate: DateTimeOffset.UtcNow.AddDays(-1),
                AuthorName: "A",
                IsCreated: true,
                IsCurrent: false));

            await vm.PendingReview.FileHistoryBrowse.SelectHistoryItemAsync(item, cache: null);
            Assert.That(vm.PendingReview.FileHistoryBrowse.IsFileHistoryBrowseMode, Is.True);

            await vm.RefreshAsync();

            Assert.That(vm.PendingReview.FileHistoryBrowse.IsFileHistoryBrowseMode, Is.True);
            Assert.That(vm.PendingReview.FileHistoryBrowse.SelectedOid, Is.EqualTo(item.Oid));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    private static async Task WaitForDiffRowsAsync(WorkingCopyViewModel vm)
    {
        for (var i = 0; i < 50 && (vm.IsLoadingDiff || vm.DiffRows.Count == 0); i++)
            await Task.Delay(20);
    }

    private static (WorkingCopyViewModel Vm, IRepositoryWatcher Watcher, IGitDiffService Diff) CreateVm(
        string path,
        bool staged,
        string unstagedPath)
    {
        var entry = new StatusEntry(
            FilePath.From(unstagedPath),
            null,
            ChangeKind.Modified,
            IsStaged: staged,
            IsUnstaged: !staged,
            IsConflicted: false);

        var status = Substitute.For<IGitStatusService>();
        status.GetStatusAsync(path, Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus(
                Staged: staged ? [entry] : [],
                Unstaged: staged ? [] : [entry],
                Conflicted: [],
                InProgress: InProgressOperation.None,
                CurrentBranch: "main",
                Epoch: 1));

        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var history = Substitute.For<IGitHistoryService>();
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        history.GetCommitPatchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<FilePath>(),
                Arg.Any<DiffOptions>(),
                Arg.Any<CancellationToken>())
            .Returns("");
        var branches = Substitute.For<IGitBranchService>();
        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var stash = Substitute.For<IGitStashService>();
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var discard = Substitute.For<IGitDiscardService>();
        discard.RecentlyDiscarded.Returns(Array.Empty<DiscardedEntry>());
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var confirm = new AlwaysConfirmDialog();
        var notifications = new NotificationService();
        var watcher = Substitute.For<IRepositoryWatcher>();
        var diff = Substitute.For<IGitDiffService>();

        var vm = new WorkingCopyViewModel(
            status,
            diff,
            Substitute.For<IGitStagingService>(),
            discard,
            Substitute.For<IGitObjectReader>(),
            Substitute.For<IGitCommitService>(),
            branches,
            Substitute.For<IGitRemoteService>(),
            Substitute.For<IGitConflictService>(),
            Substitute.For<IGitRebaseService>(),
            stash,
            history,
            settings,
            notifications,
            confirm,
            new FakeStashDialog(null),
            new IntraLineDiffer(),
            Substitute.For<IFsmonitorService>(),
            watcher,
            new PendingChangesReviewViewModel(
                NullAIReviewService.Instance, localComments, settings, confirm, notifications, history, diff));

        return (vm, watcher, diff);
    }
}
