using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diff;
using GitDelta.Diff;
using NSubstitute;
using NUnit.Framework;

namespace GitDelta.App.Tests;

public sealed class RebaseWizardViewModelTests
{
    [Test]
    public void IsProtectedBranchName_Matches_Main_And_Master()
    {
        Assert.That(RebaseWizardViewModel.IsProtectedBranchName("main"), Is.True);
        Assert.That(RebaseWizardViewModel.IsProtectedBranchName("MASTER"), Is.True);
        Assert.That(RebaseWizardViewModel.IsProtectedBranchName("feature/x"), Is.False);
    }

    [Test]
    public void PickDefaultBase_Prefers_OriginHead_Then_Upstream_Then_Main()
    {
        var withOriginHead = new[]
        {
            new BranchInfo("origin/main", false, true, null, "a", DateTimeOffset.MinValue),
            new BranchInfo("origin/HEAD", false, true, null, "h", DateTimeOffset.MinValue),
            new BranchInfo("origin/feature", false, true, null, "c", DateTimeOffset.MinValue),
        };

        var pickedHead = RebaseWizardViewModel.PickDefaultBase(withOriginHead, "origin/feature");
        Assert.That(pickedHead!.Name, Is.EqualTo("origin/HEAD"));

        var withoutOriginHead = new[]
        {
            new BranchInfo("origin/main", false, true, null, "a", DateTimeOffset.MinValue),
            new BranchInfo("develop", false, false, null, "b", DateTimeOffset.MinValue),
            new BranchInfo("origin/feature", false, true, null, "c", DateTimeOffset.MinValue),
        };

        var pickedUpstream = RebaseWizardViewModel.PickDefaultBase(withoutOriginHead, "origin/feature");
        Assert.That(pickedUpstream!.Name, Is.EqualTo("origin/feature"));

        var withoutUpstream = RebaseWizardViewModel.PickDefaultBase(withoutOriginHead, null);
        Assert.That(withoutUpstream!.Name, Is.EqualTo("origin/main"));
    }

    [Test]
    public void BuildForcePushGuidance_Mentions_Fetch_On_Lease_Failure()
    {
        var ex = new GitException(
            "push failed",
            exitCode: 1,
            stderr: "error: failed to push some refs\nhint: Updates were rejected because the remote contains work that you do not have locally.");
        var guidance = RebaseWizardViewModel.BuildForcePushGuidance(ex);
        Assert.That(guidance, Does.Contain("Fetch").IgnoreCase);
        Assert.That(guidance, Does.Contain("force-with-lease").IgnoreCase);
    }

    [Test]
    public async Task WorkingCopy_CanRebase_False_On_Main()
    {
        var status = Substitute.For<IGitStatusService>();
        var branches = Substitute.For<IGitBranchService>();
        var stash = Substitute.For<IGitStashService>();
        var history = Substitute.For<IGitHistoryService>();
        var settings = Substitute.For<ISettingsStore>();
        var watcher = Substitute.For<IRepositoryWatcher>();
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        settings.Current.Returns(new AppSettings());
        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "main", 1));

            var vm = new WorkingCopyViewModel(
                status, Substitute.For<IGitDiffService>(), Substitute.For<IGitStagingService>(),
                Substitute.For<IGitDiscardService>(), Substitute.For<IGitObjectReader>(),
                Substitute.For<IGitCommitService>(), branches, Substitute.For<IGitRemoteService>(),
                Substitute.For<IGitConflictService>(), Substitute.For<IGitRebaseService>(),
                stash, history, settings, new NotificationService(), new AlwaysConfirmDialog(),
                new FakeStashDialog(null), new IntraLineDiffer(), Substitute.For<IFsmonitorService>(),
                watcher,
                new PendingChangesReviewViewModel(
                    NullAIReviewService.Instance, localComments, settings, new AlwaysConfirmDialog(),
                    new NotificationService(), Substitute.For<IGitHistoryService>()));

            await vm.OpenAsync(repo);
            Assert.That(vm.CanRebase, Is.False);
            Assert.That(vm.RebaseDisabledReason, Does.Contain("main"));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
            watcher.Dispose();
        }
    }

    [Test]
    public async Task WorkingCopy_CanRebase_True_On_Feature_Branch()
    {
        var status = Substitute.For<IGitStatusService>();
        var branches = Substitute.For<IGitBranchService>();
        var stash = Substitute.For<IGitStashService>();
        var history = Substitute.For<IGitHistoryService>();
        var settings = Substitute.For<ISettingsStore>();
        var watcher = Substitute.For<IRepositoryWatcher>();
        var localComments = Substitute.For<ILocalCommentStore>();
        localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        settings.Current.Returns(new AppSettings());
        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        history.ListCommitsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(new RepositoryStatus([], [], [], InProgressOperation.None, "feature/x", 1));

            var vm = new WorkingCopyViewModel(
                status, Substitute.For<IGitDiffService>(), Substitute.For<IGitStagingService>(),
                Substitute.For<IGitDiscardService>(), Substitute.For<IGitObjectReader>(),
                Substitute.For<IGitCommitService>(), branches, Substitute.For<IGitRemoteService>(),
                Substitute.For<IGitConflictService>(), Substitute.For<IGitRebaseService>(),
                stash, history, settings, new NotificationService(), new AlwaysConfirmDialog(),
                new FakeStashDialog(null), new IntraLineDiffer(), Substitute.For<IFsmonitorService>(),
                watcher,
                new PendingChangesReviewViewModel(
                    NullAIReviewService.Instance, localComments, settings, new AlwaysConfirmDialog(),
                    new NotificationService(), Substitute.For<IGitHistoryService>()));

            await vm.OpenAsync(repo);
            Assert.That(vm.CanRebase, Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
            watcher.Dispose();
        }
    }

    [Test]
    public async Task ForcePush_Disabled_Without_Upstream()
    {
        var branches = Substitute.For<IGitBranchService>();
        var history = Substitute.For<IGitHistoryService>();
        var rebase = Substitute.For<IGitRebaseService>();
        var stash = Substitute.For<IGitStashService>();
        var confirm = new AlwaysConfirmDialog();
        var notifications = new NotificationService();

        branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new BranchInfo("main", false, false, null, "aaa", DateTimeOffset.MinValue)]);
        history.ListCommitsRangeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var wizard = new RebaseWizardViewModel(
            branches, history, rebase, stash, confirm, notifications,
            () => 0, () => Task.CompletedTask);

        var repo = Path.Combine(Path.GetTempPath(), "gitdelta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            await wizard.OpenAsync(repo, "feature/x", upstream: null);
            Assert.That(wizard.HasUpstream, Is.False);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
