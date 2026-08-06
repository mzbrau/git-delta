using System.Collections.ObjectModel;
using System.Text;
using CodeReviewr.Core;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewr.App.ViewModels;

public partial class WorkingCopyViewModel
{
    public ObservableCollection<string> RecentCommitMessages { get; } = [];

    public ObservableCollection<MagicCommitResultEntry> MagicCommitResults { get; } = [];

    public bool AiCommitAssistVisible => _settings.Current.AiAssistanceEnabled;

    public bool CanGenerateCommitMessage =>
        AiCommitAssistVisible
        && !IsGeneratingCommitMessage
        && !IsMagicCommitRunning
        && HasStagedFiles
        && _repoPath is not null;

    public bool CanStartMagicCommit =>
        AiCommitAssistVisible
        && !IsGeneratingCommitMessage
        && !IsMagicCommitRunning
        && _repoPath is not null
        && (HasStagedFiles || UnstagedFiles.Count > 0 || WorkingCopyChangeCount > 0);

    public bool CanAddTicketFromBranch =>
        !string.IsNullOrWhiteSpace(CurrentBranch) && !IsMagicCommitRunning;

    public bool CanLoadRecentCommitMessages =>
        _repoPath is not null && !IsMagicCommitRunning;

    public bool ShowMagicCommitInstructions =>
        ShowMagicCommitDialog && MagicCommitDialogStep == MagicCommitDialogStepKind.Instructions;

    public bool ShowMagicCommitProgress =>
        ShowMagicCommitDialog && MagicCommitDialogStep == MagicCommitDialogStepKind.Progress;

    public bool ShowMagicCommitResults =>
        ShowMagicCommitDialog && MagicCommitDialogStep == MagicCommitDialogStepKind.Results;

    public bool MagicCommitAllFiles
    {
        get => !MagicCommitStagedOnly;
        set
        {
            if (value)
                MagicCommitStagedOnly = false;
        }
    }

    public bool HasMagicCommitResults => MagicCommitResults.Count > 0;

    public bool HasMagicCommitActivityLog => !string.IsNullOrWhiteSpace(MagicCommitActivityLog);

    /// <summary>Raised on the UI thread when <see cref="MagicCommitActivityLog"/> grows so the dialog can auto-scroll.</summary>
    public event Action? MagicCommitActivityLogUpdated;

    public void NotifyAiCommitAssistVisibilityChanged()
    {
        OnPropertyChanged(nameof(AiCommitAssistVisible));
        OnPropertyChanged(nameof(CanGenerateCommitMessage));
        OnPropertyChanged(nameof(CanStartMagicCommit));
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        StartMagicCommitCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoadRecentCommitMessages))]
    private async Task LoadRecentCommitMessagesAsync()
    {
        if (_repoPath is null) return;

        try
        {
            var commits = await _history.ListCommitsAsync(_repoPath, skip: 0, take: 10).ConfigureAwait(true);
            RecentCommitMessages.Clear();
            foreach (var commit in commits)
            {
                var message = string.IsNullOrWhiteSpace(commit.Body)
                    ? commit.Subject
                    : $"{commit.Subject}\n\n{commit.Body.TrimEnd()}";
                RecentCommitMessages.Add(message);
            }
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load recent commit messages: {ex.Message}", null, ex);
        }
    }

    [RelayCommand]
    private void ApplyRecentCommitMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        CommitMessage = message;
    }

    [RelayCommand(CanExecute = nameof(CanAddTicketFromBranch))]
    private void AddTicketFromBranch()
    {
        if (!TicketFromBranch.TryExtract(CurrentBranch, _settings.Current.TicketFromBranchRegex, out var ticket, out var error))
        {
            HookOutput = error ?? "No ticket found in branch name.";
            return;
        }

        CommitMessage = TicketFromBranch.PrependTicket(CommitMessage, ticket);
        HookOutput = "";
    }

    [RelayCommand(CanExecute = nameof(CanGenerateCommitMessage))]
    private async Task GenerateCommitMessageAsync()
    {
        if (_repoPath is null || IsGeneratingCommitMessage) return;

        IsGeneratingCommitMessage = true;
        HookOutput = "";
        try
        {
            var summary = await BuildStagedDiffSummaryAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(summary))
            {
                HookOutput = "No staged changes to summarise.";
                return;
            }

            var message = await _commitAssist.GenerateCommitMessageAsync(
                    ((IPendingChangesReviewHost)this).RepositoryKey,
                    _repoPath,
                    summary)
                .ConfigureAwait(true);

            CommitMessage = message;
        }
        catch (Exception ex)
        {
            HookOutput = $"Generate commit message failed: {ex.Message}";
            _notifications.Error($"Generate commit message failed: {ex.Message}", null, ex);
        }
        finally
        {
            IsGeneratingCommitMessage = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartMagicCommit))]
    private void StartMagicCommit()
    {
        MagicCommitInstructions = "";
        MagicCommitStagedOnly = false;
        MagicCommitProgressText = "";
        MagicCommitActivityLog = "";
        MagicCommitError = "";
        MagicCommitResults.Clear();
        OnPropertyChanged(nameof(HasMagicCommitResults));
        MagicCommitDialogStep = MagicCommitDialogStepKind.Instructions;
        ShowMagicCommitDialog = true;
        _magicCommitCts?.Cancel();
        _magicCommitCts = null;
    }

    [RelayCommand]
    private void CancelMagicCommitDialog()
    {
        if (IsMagicCommitRunning)
        {
            _magicCommitCts?.Cancel();
            return;
        }

        ShowMagicCommitDialog = false;
        MagicCommitDialogStep = MagicCommitDialogStepKind.Instructions;
    }

    [RelayCommand]
    private void CloseMagicCommitDialog()
    {
        if (IsMagicCommitRunning)
            return;

        ShowMagicCommitDialog = false;
        MagicCommitDialogStep = MagicCommitDialogStepKind.Instructions;
    }

    [RelayCommand]
    private async Task ConfirmMagicCommitAsync()
    {
        if (_repoPath is null || IsMagicCommitRunning) return;

        _magicCommitCts?.Cancel();
        _magicCommitCts = new CancellationTokenSource();
        var ct = _magicCommitCts.Token;

        IsMagicCommitRunning = true;
        MagicCommitDialogStep = MagicCommitDialogStepKind.Progress;
        MagicCommitProgressText = "Preparing changes…";
        MagicCommitActivityLog = "";
        MagicCommitError = "";
        MagicCommitResults.Clear();
        OnPropertyChanged(nameof(HasMagicCommitResults));
        AppendMagicCommitActivity("Preparing changes…");

        try
        {
            var options = _settings.Current.ToDiffOptions();
            var inventoryDiffs = await CollectMagicCommitDiffsAsync(MagicCommitStagedOnly, options, ct)
                .ConfigureAwait(true);

            if (inventoryDiffs.Count == 0)
            {
                MagicCommitError = MagicCommitStagedOnly
                    ? "No staged changes to commit."
                    : "No pending changes to commit.";
                MagicCommitDialogStep = MagicCommitDialogStepKind.Results;
                return;
            }

            // Build inventory from the initial collect only. Do not rebuild from IndexToWorktree
            // after unstage — in staged-only mode that would pull in additional unstaged edits.
            var inventory = MagicCommitInventory.Build(inventoryDiffs);

            // Normalize index so the executor can rematch/stage against IndexToWorktree.
            var trackedPaths = inventoryDiffs
                .Where(d => d.Change != ChangeKind.Untracked)
                .Select(d => d.NewPath.Value.Length > 0 ? d.NewPath.Value : d.OldPath.Value)
                .Distinct(StringComparer.Ordinal)
                .Select(FilePath.From)
                .ToList();
            if (trackedPaths.Count > 0)
                await _staging.UnstageFilesAsync(_repoPath, trackedPaths, ct).ConfigureAwait(true);
            MagicCommitProgressText = "Asking Copilot for a commit plan…";
            AppendMagicCommitActivity("Asking Copilot for a commit plan…");

            var activity = new Progress<string>(AppendMagicCommitActivity);
            var plan = await _commitAssist.ProposeMagicCommitPlanAsync(
                    ((IPendingChangesReviewHost)this).RepositoryKey,
                    _repoPath,
                    MagicCommitInventory.FormatInventoryForPrompt(inventory),
                    MagicCommitInstructions,
                    activity,
                    ct)
                .ConfigureAwait(true);

            MagicCommitProgressText = "Executing commit plan…";
            AppendMagicCommitActivity("Executing commit plan…");

            var executor = new MagicCommitExecutor(
                _diffService, _staging, _commit, _history, NullLogger<MagicCommitExecutor>.Instance);
            var progress = new Progress<string>(line =>
            {
                MagicCommitProgressText = line;
                AppendMagicCommitActivity(line);
            });
            var result = await executor.ExecuteAsync(
                    _repoPath, inventory, plan, options, NoVerify, progress, ct)
                .ConfigureAwait(true);

            foreach (var created in result.Commits)
                MagicCommitResults.Add(created);

            OnPropertyChanged(nameof(HasMagicCommitResults));
            MagicCommitError = result.Error ?? "";
            MagicCommitDialogStep = MagicCommitDialogStepKind.Results;

            await RefreshAsync(clearAiReviewAfter: true).ConfigureAwait(true);
            if (_allHistoryCommits.Count > 0)
                _ = SoftRefreshHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            MagicCommitError = "Magic Commit was cancelled.";
            MagicCommitDialogStep = MagicCommitDialogStepKind.Results;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MagicCommitError = ex.Message;
            MagicCommitDialogStep = MagicCommitDialogStepKind.Results;
            _notifications.Error($"Magic Commit failed: {ex.Message}", null, ex);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsMagicCommitRunning = false;
            _magicCommitCts = null;
        }
    }

    private void AppendMagicCommitActivity(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        void Append()
        {
            MagicCommitActivityLog = string.IsNullOrEmpty(MagicCommitActivityLog)
                ? line
                : MagicCommitActivityLog + Environment.NewLine + line;
        }

        try
        {
            var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
            if (dispatcher.CheckAccess() || Avalonia.Application.Current is null)
            {
                Append();
                return;
            }

            dispatcher.Post(Append);
        }
        catch (InvalidOperationException)
        {
            Append();
        }
    }

    private async Task<string> BuildStagedDiffSummaryAsync()
    {
        if (_repoPath is null) return "";

        var options = _settings.Current.ToDiffOptions();
        var files = await _diffService.GetRawDiffAsync(_repoPath, DiffTarget.HeadToIndex, options)
            .ConfigureAwait(true);
        if (files.Count == 0) return "";

        var sb = new StringBuilder();
        const int maxChars = 48_000;
        foreach (var (path, _, _, kind) in files)
        {
            if (sb.Length >= maxChars) break;
            try
            {
                var diff = await _diffService.GetDiffAsync(_repoPath, path, DiffTarget.HeadToIndex, options)
                    .ConfigureAwait(true);
                sb.AppendLine($"FILE {path.Value} ({kind})");
                if (diff.IsBinary)
                {
                    sb.AppendLine("(binary)");
                }
                else
                {
                    var patch = diff.RawPatch;
                    if (patch.Length > 6_000)
                        patch = patch[..6_000] + "\n…(truncated)…\n";
                    sb.AppendLine(patch);
                }

                sb.AppendLine();
            }
            catch
            {
                sb.AppendLine($"FILE {path.Value} ({kind}) — diff unavailable");
            }
        }

        return sb.ToString();
    }

    private async Task<IReadOnlyList<FileDiff>> CollectMagicCommitDiffsAsync(
        bool stagedOnly,
        DiffOptions options,
        CancellationToken ct)
    {
        if (_repoPath is null) return [];

        var target = stagedOnly ? DiffTarget.HeadToIndex : DiffTarget.HeadToWorktree;
        var files = await _diffService.GetRawDiffAsync(_repoPath, target, options, ct).ConfigureAwait(true);
        var paths = files.Select(f => f.Path).ToList();
        var diffs = (await LoadDiffsForPathsAsync(paths, target, options, ct).ConfigureAwait(true)).ToList();

        if (stagedOnly)
            return diffs;

        // git diff HEAD never lists untracked paths — merge them from status.
        var status = await _statusService.GetStatusAsync(_repoPath, ct).ConfigureAwait(true);
        var known = new HashSet<string>(
            diffs.Select(d => d.NewPath.Value.Length > 0 ? d.NewPath.Value : d.OldPath.Value),
            StringComparer.Ordinal);

        foreach (var entry in status.Unstaged)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind != ChangeKind.Untracked)
                continue;
            if (!known.Add(entry.Path.Value))
                continue;

            try
            {
                var diff = await LoadUntrackedFileDiffAsync(_repoPath, entry.Path, DiffTarget.IndexToWorktree, ct)
                    .ConfigureAwait(true);
                diffs.Add(diff);
            }
            catch
            {
                // Skip paths that cannot be read.
            }
        }

        return diffs;
    }

    private async Task<IReadOnlyList<FileDiff>> LoadDiffsForPathsAsync(
        IReadOnlyList<FilePath> paths,
        DiffTarget target,
        DiffOptions options,
        CancellationToken ct)
    {
        if (_repoPath is null || paths.Count == 0) return [];

        var diffs = new List<FileDiff>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var diff = await _diffService.GetDiffAsync(_repoPath, path, target, options, ct)
                    .ConfigureAwait(true);
                if (diff.IsBinary || diff.Hunks.Count > 0 || diff.Change is ChangeKind.Added or ChangeKind.Deleted or ChangeKind.Untracked)
                    diffs.Add(diff);
            }
            catch
            {
                // Skip paths that cannot be diffed.
            }
        }

        return diffs;
    }

    partial void OnIsGeneratingCommitMessageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateCommitMessage));
        OnPropertyChanged(nameof(CanStartMagicCommit));
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        StartMagicCommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMagicCommitRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateCommitMessage));
        OnPropertyChanged(nameof(CanStartMagicCommit));
        OnPropertyChanged(nameof(CanAddTicketFromBranch));
        OnPropertyChanged(nameof(CanLoadRecentCommitMessages));
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        StartMagicCommitCommand.NotifyCanExecuteChanged();
        AddTicketFromBranchCommand.NotifyCanExecuteChanged();
        LoadRecentCommitMessagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnMagicCommitStagedOnlyChanged(bool value) =>
        OnPropertyChanged(nameof(MagicCommitAllFiles));

    partial void OnShowMagicCommitDialogChanged(bool value) => NotifyMagicCommitDialogStepChanged();

    partial void OnMagicCommitDialogStepChanged(MagicCommitDialogStepKind value) =>
        NotifyMagicCommitDialogStepChanged();

    partial void OnMagicCommitActivityLogChanged(string value)
    {
        OnPropertyChanged(nameof(HasMagicCommitActivityLog));
        MagicCommitActivityLogUpdated?.Invoke();
    }

    private void NotifyMagicCommitDialogStepChanged()
    {
        OnPropertyChanged(nameof(ShowMagicCommitInstructions));
        OnPropertyChanged(nameof(ShowMagicCommitProgress));
        OnPropertyChanged(nameof(ShowMagicCommitResults));
    }
}

public enum MagicCommitDialogStepKind
{
    Instructions,
    Progress,
    Results,
}
