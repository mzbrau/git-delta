using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Diff;
using GitDelta.TestSupport;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class WorkingCopyStatusEpochTests
{
    [Test]
    public async Task OpenAsync_NewRepository_AppliesStatusEvenWhenPreviousEpochWasHigher()
    {
        using var repoA = RepositoryBuilder.Create()
            .WithFile("a.txt", "a\n")
            .WithInitialCommit("init");
        using var repoB = RepositoryBuilder.Create()
            .WithFile("b.txt", "b\n")
            .WithInitialCommit("init");
        var pathA = repoA.Build();
        var pathB = repoB.Build();

        var entryB = new StatusEntry(
            FilePath.From("changed.txt"),
            null,
            ChangeKind.Modified,
            IsStaged: false,
            IsUnstaged: true,
            IsConflicted: false);

        var status = Substitute.For<IGitStatusService>();
        status.GetStatusAsync(pathA, Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus(
                Staged: [],
                Unstaged: [],
                Conflicted: [],
                InProgress: InProgressOperation.None,
                CurrentBranch: "branch-a",
                Epoch: 5));
        status.GetStatusAsync(pathB, Arg.Any<CancellationToken>())
            .Returns(new RepositoryStatus(
                Staged: [],
                Unstaged: [entryB],
                Conflicted: [],
                InProgress: InProgressOperation.None,
                CurrentBranch: "branch-b",
                Epoch: 0));

        var (vm, watcher) = CreateVm(status);
        try
        {
            await vm.OpenAsync(pathA);
            Assert.That(vm.CurrentBranch, Is.EqualTo("branch-a"));
            Assert.That(vm.UnstagedFiles, Is.Empty);

            await vm.OpenAsync(pathB);

            Assert.That(vm.CurrentBranch, Is.EqualTo("branch-b"));
            Assert.That(vm.UnstagedFiles, Has.Count.EqualTo(1));
            Assert.That(vm.UnstagedFiles[0].Path.Value, Is.EqualTo("changed.txt"));
            Assert.That(vm.WorkingCopyChangeCount, Is.EqualTo(1));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Test]
    public async Task RefreshAsync_SameRepository_DiscardsStaleEpoch()
    {
        using var repo = RepositoryBuilder.Create()
            .WithFile("a.txt", "a\n")
            .WithInitialCommit("init");
        var path = repo.Build();

        var freshEntry = new StatusEntry(
            FilePath.From("fresh.txt"),
            null,
            ChangeKind.Modified,
            IsStaged: false,
            IsUnstaged: true,
            IsConflicted: false);
        var staleEntry = new StatusEntry(
            FilePath.From("stale.txt"),
            null,
            ChangeKind.Modified,
            IsStaged: false,
            IsUnstaged: true,
            IsConflicted: false);

        var status = Substitute.For<IGitStatusService>();
        status.GetStatusAsync(path, Arg.Any<CancellationToken>())
            .Returns(
                new RepositoryStatus(
                    Staged: [],
                    Unstaged: [freshEntry],
                    Conflicted: [],
                    InProgress: InProgressOperation.None,
                    CurrentBranch: "main",
                    Epoch: 5),
                new RepositoryStatus(
                    Staged: [],
                    Unstaged: [staleEntry],
                    Conflicted: [],
                    InProgress: InProgressOperation.None,
                    CurrentBranch: "stale-branch",
                    Epoch: 4));

        var (vm, watcher) = CreateVm(status);
        try
        {
            await vm.OpenAsync(path);
            Assert.That(vm.UnstagedFiles, Has.Count.EqualTo(1));
            Assert.That(vm.UnstagedFiles[0].Path.Value, Is.EqualTo("fresh.txt"));
            Assert.That(vm.CurrentBranch, Is.EqualTo("main"));

            await vm.RefreshAsync();

            Assert.That(vm.UnstagedFiles, Has.Count.EqualTo(1));
            Assert.That(vm.UnstagedFiles[0].Path.Value, Is.EqualTo("fresh.txt"));
            Assert.That(vm.CurrentBranch, Is.EqualTo("main"));
        }
        finally
        {
            watcher.Dispose();
        }
    }

    private static (WorkingCopyViewModel Vm, IRepositoryWatcher Watcher) CreateVm(IGitStatusService status)
    {
        var settings = Substitute.For<ISettingsStore>();
        settings.Current.Returns(new AppSettings());
        var history = Substitute.For<IGitHistoryService>();
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var branches = Substitute.For<IGitBranchService>();
        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var stash = Substitute.For<IGitStashService>();
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var discard = Substitute.For<IGitDiscardService>();
        discard.RecentlyDiscarded.Returns(Array.Empty<DiscardedEntry>());
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        var watcher = Substitute.For<IRepositoryWatcher>();

        var vm = new WorkingCopyViewModel(
            status,
            Substitute.For<IGitDiffService>(),
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
            new NotificationService(),
            new AlwaysConfirmDialog(),
            new FakeStashDialog(null),
            new IntraLineDiffer(),
            Substitute.For<IFsmonitorService>(),
            watcher,
            new PendingChangesReviewViewModel(
                NullAIReviewService.Instance, localComments, settings, new AlwaysConfirmDialog(),
                new NotificationService(), history, Substitute.For<IGitDiffService>()));

        return (vm, watcher);
    }
}
