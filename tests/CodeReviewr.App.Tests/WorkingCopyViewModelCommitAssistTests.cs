using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using NSubstitute;
using NUnit.Framework;

namespace CodeReviewr.App.Tests;

public sealed class WorkingCopyViewModelCommitAssistTests
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
    private IAiCommitAssistService _commitAssist = null!;
    private AppSettings _appSettings = null!;

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
        _commitAssist = Substitute.For<IAiCommitAssistService>();

        _appSettings = new AppSettings();
        _settings.Current.Returns(_appSettings);
        _branches.ListBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _stash.ListStashesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
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
            new PendingChangesReviewViewModel(NullAIReviewService.Instance, _localComments, _settings, _confirm, _notifications, Substitute.For<IGitHistoryService>()),
            _commitAssist);

    private static StatusEntry Staged(string path) =>
        new(FilePath.From(path), null, ChangeKind.Modified, IsStaged: true, IsUnstaged: false, IsConflicted: false);

    private static RepositoryStatus Status(
        IReadOnlyList<StatusEntry>? staged = null,
        string branch = "bugfix/SMITH-123/3",
        long epoch = 1) =>
        new(staged ?? [], [], [], InProgressOperation.None, branch, epoch);

    private static string NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "codereviewr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        return repo;
    }

    [Test]
    public async Task StartMagicCommit_Defaults_To_All_Pending_Changes_Even_When_Staged()
    {
        _appSettings.AiAssistanceEnabled = true;
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            Assert.That(vm.HasStagedFiles, Is.True);

            vm.StartMagicCommitCommand.Execute(null);

            Assert.That(vm.ShowMagicCommitDialog, Is.True);
            Assert.That(vm.MagicCommitStagedOnly, Is.False);
            Assert.That(vm.MagicCommitAllFiles, Is.True);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task LoadRecentCommitMessages_Populates_And_Apply_Sets_Message()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")]));
            _history.ListCommitsAsync(repo, 0, 10, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(
                [
                    new CommitInfo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaa", "Subject one", "Body one",
                        "A", "a@b.c", DateTimeOffset.UtcNow, [], []),
                    new CommitInfo("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bbbbbbb", "Subject two", "",
                        "A", "a@b.c", DateTimeOffset.UtcNow, [], []),
                ]);

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            await vm.LoadRecentCommitMessagesCommand.ExecuteAsync(null);

            Assert.That(vm.RecentCommitMessages, Has.Count.EqualTo(2));
            Assert.That(vm.RecentCommitMessages[0], Does.Contain("Subject one").And.Contain("Body one"));
            Assert.That(vm.RecentCommitMessages[1], Is.EqualTo("Subject two"));

            vm.ApplyRecentCommitMessageCommand.Execute(vm.RecentCommitMessages[0]);
            Assert.That(vm.CommitMessage, Does.Contain("Subject one"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task AddTicketFromBranch_Prepends_Ticket()
    {
        var repo = NewRepo();
        try
        {
            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")]));

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.CommitMessage = "fix the bug";
            vm.AddTicketFromBranchCommand.Execute(null);

            Assert.That(vm.CommitMessage, Is.EqualTo("SMITH-123 fix the bug"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public void AiCommitAssistVisible_Follows_Settings()
    {
        var vm = CreateVm();
        Assert.That(vm.AiCommitAssistVisible, Is.False);

        _appSettings.AiAssistanceEnabled = true;
        vm.NotifyAiCommitAssistVisibilityChanged();
        Assert.That(vm.AiCommitAssistVisible, Is.True);
    }

    [Test]
    public async Task GenerateCommitMessage_Sets_Busy_Then_Message()
    {
        var repo = NewRepo();
        try
        {
            _appSettings.AiAssistanceEnabled = true;
            _appSettings.AiDisclosureAcknowledged = true;

            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")]));

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commitAssist.GenerateCommitMessageAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => tcs.Task);

            _diff.GetRawDiffAsync(repo, Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns([(FilePath.From("a.txt"), ContentId.FromSha("0".PadRight(40, '0')), ContentId.FromSha("1".PadRight(40, '0')), ChangeKind.Modified)]);
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(new FileDiff(
                    DiffTarget.HeadToIndex.AsWorkingCopy(),
                    FilePath.From("a.txt"),
                    FilePath.From("a.txt"),
                    ChangeKind.Modified,
                    ContentId.FromSha("0".PadRight(40, '0')),
                    ContentId.FromSha("1".PadRight(40, '0')),
                    false,
                    [new DiffHunk(1, 1, 1, 1, "@@ -1 +1 @@", [new DiffLine(DiffLineKind.Added, null, 1, "hello".AsMemory())])],
                    "diff --git a/a.txt b/a.txt\n+hello\n"));

            var vm = CreateVm();
            await vm.OpenAsync(repo);

            var generate = vm.GenerateCommitMessageCommand.ExecuteAsync(null);
            Assert.That(vm.IsGeneratingCommitMessage, Is.True);

            tcs.SetResult("feat: greet the world");
            await generate;

            Assert.That(vm.IsGeneratingCommitMessage, Is.False);
            Assert.That(vm.CommitMessage, Is.EqualTo("feat: greet the world"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public async Task MagicCommit_Accumulates_Activity_Log_From_Assist()
    {
        var repo = NewRepo();
        try
        {
            _appSettings.AiAssistanceEnabled = true;
            _appSettings.AiDisclosureAcknowledged = true;

            _status.GetStatusAsync(repo, Arg.Any<CancellationToken>())
                .Returns(Status(staged: [Staged("a.txt")]));

            var oldId = ContentId.FromSha("0".PadRight(40, '0'));
            var newId = ContentId.FromSha("1".PadRight(40, '0'));
            var fileDiff = new FileDiff(
                DiffTarget.HeadToIndex.AsWorkingCopy(),
                FilePath.From("a.txt"),
                FilePath.From("a.txt"),
                ChangeKind.Modified,
                oldId,
                newId,
                false,
                [new DiffHunk(1, 1, 1, 1, "@@ -1 +1 @@",
                    [new DiffLine(DiffLineKind.Added, null, 1, "hello".AsMemory())])],
                "diff --git a/a.txt b/a.txt\n+hello\n");

            _diff.GetRawDiffAsync(repo, Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns([(FilePath.From("a.txt"), oldId, newId, ChangeKind.Modified)]);
            _diff.GetDiffAsync(repo, Arg.Any<FilePath>(), Arg.Any<DiffScope>(), Arg.Any<DiffOptions>(), Arg.Any<CancellationToken>())
                .Returns(fileDiff);

            _commitAssist.ProposeMagicCommitPlanAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<IProgress<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var activity = ci.ArgAt<IProgress<string>?>(4);
                    activity?.Report("[12:00:00] >>> Prompt");
                    activity?.Report("[12:00:01] <<< End prompt");
                    activity?.Report("[12:00:02] >>> Tool start: submit_magic_commit_plan");
                    activity?.Report("""{"commits":[{"message":"x","hunkIds":["h1"]}]}""");
                    return Task.FromException<MagicCommitPlan>(
                        new InvalidOperationException("Copilot did not submit a Magic Commit plan."));
                });

            var vm = CreateVm();
            await vm.OpenAsync(repo);
            vm.StartMagicCommitCommand.Execute(null);
            await vm.ConfirmMagicCommitCommand.ExecuteAsync(null);

            Assert.That(vm.MagicCommitActivityLog, Does.Contain("Asking Copilot for a commit plan"));
            Assert.That(vm.MagicCommitActivityLog, Does.Contain(">>> Prompt"));
            Assert.That(vm.MagicCommitActivityLog, Does.Contain("submit_magic_commit_plan"));
            Assert.That(vm.HasMagicCommitActivityLog, Is.True);
            Assert.That(vm.MagicCommitError, Does.Contain("Copilot did not submit"));
            Assert.That(vm.ShowMagicCommitResults, Is.True);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
