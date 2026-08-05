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

public sealed class WorkingCopyViewModelStashDialogTests
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
        _watcher = Substitute.For<IRepositoryWatcher>();
        _localComments = Substitute.For<ILocalCommentStore>();
        _localComments.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

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

    private WorkingCopyViewModel CreateVm(IStashDialog stashDialog) =>
        new(_status, _diff, _staging, _discard, Substitute.For<IGitObjectReader>(), _commit, _branches, _remotes,
            _conflicts, _stash, _history, _settings, _notifications, _confirm, stashDialog,
            new IntraLineDiffer(), _fsmonitor, _watcher,
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()));

    private static RepositoryStatus StatusWithChange() =>
        new(
            [],
            [new StatusEntry(FilePath.From("a.txt"), null, ChangeKind.Modified, false, true, false)],
            [],
            InProgressOperation.None,
            "main",
            1);

    [Test]
    public async Task StashAllChanges_Cancel_Does_Not_Call_Git()
    {
        var dialog = new FakeStashDialog(null);
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(StatusWithChange());
            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.StashAllChangesCommand.ExecuteAsync(null);

            Assert.That(dialog.CallCount, Is.EqualTo(1));
            await _stash.DidNotReceive()
                .StashPushAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            await _stash.DidNotReceive()
                .StashPopAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task StashAllChanges_Push_Passes_Message_And_Untracked_Flag()
    {
        var dialog = new FakeStashDialog(
            new StashDialogResult(StashDialogAction.Push, "my stash", IncludeUntracked: false));
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(StatusWithChange());
            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.StashAllChangesCommand.ExecuteAsync(null);

            await _stash.Received(1).StashPushAsync(repo, "my stash", false, Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task StashAllChanges_Pop_Calls_StashPop()
    {
        var dialog = new FakeStashDialog(
            new StashDialogResult(StashDialogAction.Pop, null, IncludeUntracked: true));
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(StatusWithChange());
            _stash.ListStashesAsync(repo, Arg.Any<CancellationToken>())
                .Returns([new StashInfo(0, "WIP on main: abc", "main")]);
            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);

            await vm.StashAllChangesCommand.ExecuteAsync(null);

            await _stash.Received(1).StashPopAsync(repo, Arg.Any<CancellationToken>());
            await _stash.DidNotReceive()
                .StashPushAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task DeleteStash_Confirmed_Drops_And_Leaves_Stash_Mode()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            var stashInfo = new StashInfo(0, "WIP on main: abc", "main");
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>()).Returns(StatusWithChange());
            _stash.ListStashesAsync(repo, Arg.Any<CancellationToken>()).Returns([stashInfo]);
            _stash.GetStashFilesAsync(repo, 0, Arg.Any<CancellationToken>()).Returns([]);

            var dialog = new FakeStashDialog(null);
            var vm = CreateVm(dialog);
            await vm.OpenAsync(repo);
            await vm.SelectStashCommand.ExecuteAsync(stashInfo);

            await vm.DeleteStashCommand.ExecuteAsync(stashInfo);

            Assert.That(_confirm.CallCount, Is.EqualTo(1));
            await _stash.Received(1).DropStashAsync(repo, 0, Arg.Any<CancellationToken>());
            Assert.That(vm.IsFileStatusMode, Is.True);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }
}
