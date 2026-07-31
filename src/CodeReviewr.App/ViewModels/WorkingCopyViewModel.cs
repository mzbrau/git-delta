using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CodeReviewr.Git;

namespace CodeReviewr.App.ViewModels;

public partial class WorkingCopyViewModel : ObservableObject
{
    private readonly IGitStatusService _statusService;
    private readonly IGitDiffService _diffService;
    private readonly IGitStagingService _staging;
    private readonly IGitDiscardService _discard;
    private readonly IGitCommitService _commit;
    private readonly IGitBranchService _branches;
    private readonly IGitRemoteService _remotes;
    private readonly IGitConflictService _conflicts;
    private readonly IGitStashService _stash;
    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly IIntraLineDiffer _intraLine;
    private readonly IGitProcessRunner _runner;
    private readonly GitRepositoryWatcher _watcher;

    private CancellationTokenSource? _diffCts;
    private string? _repoPath;
    private FileDiff? _currentDiff;
    private readonly List<PendingMutation> _pending = [];
    private long _statusEpoch;

    public WorkingCopyViewModel(
        IGitStatusService statusService,
        IGitDiffService diffService,
        IGitStagingService staging,
        IGitDiscardService discard,
        IGitCommitService commit,
        IGitBranchService branches,
        IGitRemoteService remotes,
        IGitConflictService conflicts,
        IGitStashService stash,
        ISettingsStore settings,
        NotificationService notifications,
        IIntraLineDiffer intraLine,
        IGitProcessRunner runner,
        GitRepositoryWatcher watcher)
    {
        _statusService = statusService;
        _diffService = diffService;
        _staging = staging;
        _discard = discard;
        _commit = commit;
        _branches = branches;
        _remotes = remotes;
        _conflicts = conflicts;
        _stash = stash;
        _settings = settings;
        _notifications = notifications;
        _intraLine = intraLine;
        _runner = runner;
        _watcher = watcher;
        ViewMode = settings.Current.DefaultDiffMode;
        _watcher.RefreshRequested += () => _ = RefreshAsync();
        _watcher.OfferFsmonitor += () =>
            _notifications.Info("Status is slow. Enable Git fsmonitor for this repository?",
                () => _ = EnableFsmonitorAsync(), "Enable");
    }

    public ObservableCollection<FileItemViewModel> StagedFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> UnstagedFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> ConflictedFiles { get; } = [];
    public ObservableCollection<DiffRow> DiffRows { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];

    [ObservableProperty] private string? _repositoryPath;
    [ObservableProperty] private string? _currentBranch;
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private DiffViewMode _viewMode;
    [ObservableProperty] private bool _isLoadingDiff;
    [ObservableProperty] private bool _isCombinedReviewMode;
    [ObservableProperty] private string? _inProgressBanner;
    [ObservableProperty] private InProgressOperation _inProgress;
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private bool _amendCommit;
    [ObservableProperty] private bool _noVerify;
    [ObservableProperty] private bool _isCommitting;
    [ObservableProperty] private string _hookOutput = "";
    [ObservableProperty] private bool _canStageFromDiff;
    [ObservableProperty] private string? _stagingDisabledReason;
    [ObservableProperty] private int _selectedHunkIndex = -1;
    [ObservableProperty] private bool _statusUpdated;
    [ObservableProperty] private int _workingCopyChangeCount;
    [ObservableProperty] private int _selectedAddedLines;
    [ObservableProperty] private int _selectedRemovedLines;
    [ObservableProperty] private bool _stagedExpanded = true;
    [ObservableProperty] private bool _unstagedExpanded = true;
    [ObservableProperty] private bool _branchesExpanded = true;
    [ObservableProperty] private bool _explorerExpanded = true;

    public bool HasRepository => _repoPath is not null;

    public string CommitButtonLabel =>
        string.IsNullOrEmpty(CurrentBranch) ? "Commit" : $"Commit to {CurrentBranch}";

    public string DiffFooterText =>
        StagingDisabledReason
        ?? (SelectedFile is null ? "Select a file to view its diff"
            : IsLoadingDiff ? "Loading diff…"
            : SelectedAddedLines + SelectedRemovedLines == 0 ? "No line changes"
            : $"{SelectedAddedLines} additions, {SelectedRemovedLines} deletions");

    public bool HasConflictedFiles => ConflictedFiles.Count > 0;

    partial void OnCurrentBranchChanged(string? value) => OnPropertyChanged(nameof(CommitButtonLabel));
    partial void OnStagingDisabledReasonChanged(string? value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnIsLoadingDiffChanged(bool value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnSelectedAddedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnSelectedRemovedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));

    public async Task OpenAsync(string path)
    {
        _repoPath = path;
        RepositoryPath = path;
        OnPropertyChanged(nameof(HasRepository));
        _watcher.WatchRepository(path);
        await RefreshAsync();
        await LoadBranchesAsync();
    }

    private async Task EnableFsmonitorAsync()
    {
        if (_repoPath is null) return;
        await new FsmonitorPrompt(_runner).EnableAsync(_repoPath);
        _notifications.Info("core.fsmonitor enabled for this repository.");
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_repoPath is null) return;
        var sw = Stopwatch.StartNew();
        var status = await _statusService.GetStatusAsync(_repoPath);
        if (status.Epoch < _statusEpoch) return;
        _statusEpoch = status.Epoch;

        CurrentBranch = status.CurrentBranch;
        InProgress = status.InProgress;
        InProgressBanner = status.InProgress switch
        {
            InProgressOperation.Merge => "Merge in progress. Abort is always available. Continue when the index is clean.",
            InProgressOperation.Rebase => "Rebase in progress. Abort is always available. Continue when the index is clean.",
            InProgressOperation.CherryPick => "Cherry-pick in progress. Abort is always available.",
            InProgressOperation.Revert => "Revert in progress. Abort is always available.",
            _ => null,
        };

        RebuildFileLists(status);
        StatusUpdated = true;
        CodeReviewrMeters.StatusRefreshMs.Record(sw.Elapsed.TotalMilliseconds);
        CodeReviewrMeters.RepositoryOpenMs.Record(sw.Elapsed.TotalMilliseconds);
    }

    private void RebuildFileLists(RepositoryStatus status)
    {
        StagedFiles.Clear();
        UnstagedFiles.Clear();
        ConflictedFiles.Clear();

        var pendingPaths = _pending.Select(p => p.Path.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var e in status.Staged)
        {
            if (pendingPaths.Contains(e.Path.Value) && _pending.Any(p => p.Path.Equals(e.Path) && p.WasUnstage))
                continue;
            StagedFiles.Add(FileItemViewModel.From(e, isStagedList: true));
        }

        foreach (var e in status.Unstaged)
        {
            if (pendingPaths.Contains(e.Path.Value) && _pending.Any(p => p.Path.Equals(e.Path) && !p.WasUnstage))
                continue;
            UnstagedFiles.Add(FileItemViewModel.From(e, isStagedList: false));
        }

        // Optimistic overlays: move predicted staged/unstaged
        foreach (var p in _pending.Where(p => !p.WasUnstage))
        {
            if (UnstagedFiles.All(f => f.Path.Value != p.Path.Value))
                StagedFiles.Add(new FileItemViewModel(p.Path, ChangeKind.Modified, isStagedList: true, isPartial: true, isOptimistic: true));
        }

        foreach (var e in status.Conflicted)
            ConflictedFiles.Add(FileItemViewModel.From(e, isStagedList: false));

        WorkingCopyChangeCount = StagedFiles.Count + UnstagedFiles.Count + ConflictedFiles.Count;
        OnPropertyChanged(nameof(HasConflictedFiles));
    }

    partial void OnSelectedFileChanged(FileItemViewModel? value) => _ = LoadDiffForSelectionAsync(value);

    partial void OnViewModeChanged(DiffViewMode value)
    {
        if (_currentDiff is null) return;
        // Instant switch: recompute layout only — zero git, zero tokenize
        ProjectRows(_currentDiff);
    }

    partial void OnIsCombinedReviewModeChanged(bool value) => _ = LoadDiffForSelectionAsync(SelectedFile);

    [RelayCommand]
    private void ToggleCombinedReview() => IsCombinedReviewMode = !IsCombinedReviewMode;

    private async Task LoadDiffForSelectionAsync(FileItemViewModel? file)
    {
        _diffCts?.Cancel();
        _diffCts = new CancellationTokenSource();
        var ct = _diffCts.Token;

        DiffRows.Clear();
        _currentDiff = null;
        SelectedAddedLines = 0;
        SelectedRemovedLines = 0;
        if (file is null || _repoPath is null)
        {
            OnPropertyChanged(nameof(DiffFooterText));
            return;
        }

        var target = IsCombinedReviewMode
            ? DiffTarget.HeadToWorktree
            : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;

        CanStageFromDiff = target is DiffTarget.IndexToWorktree or DiffTarget.HeadToIndex;
        StagingDisabledReason = target == DiffTarget.HeadToWorktree
            ? "Combined review mode is read-only. Partial staging requires the staged/unstaged lists."
            : file.IsConflicted ? "Conflicted files cannot be staged here. Resolve externally or open mergetool."
            : null;

        IsLoadingDiff = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var options = _settings.Current.ToDiffOptions();
            var diff = await _diffService.GetDiffAsync(_repoPath, file.Path, target, options, ct);
            ct.ThrowIfCancellationRequested();
            _currentDiff = ApplyIntraLine(diff);
            UpdateDiffStats(_currentDiff);
            ProjectRows(_currentDiff);
            CodeReviewrMeters.DiffGenerationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SelectedAddedLines = 0;
            SelectedRemovedLines = 0;
            _notifications.Error($"Diff failed: {ex.Message}", () => _ = LoadDiffForSelectionAsync(file));
        }
        finally
        {
            IsLoadingDiff = false;
            OnPropertyChanged(nameof(DiffFooterText));
        }
    }

    private void UpdateDiffStats(FileDiff? diff)
    {
        var added = 0;
        var removed = 0;
        if (diff is not null)
        {
            foreach (var hunk in diff.Hunks)
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Added) added++;
                else if (line.Kind == DiffLineKind.Removed) removed++;
            }
        }
        SelectedAddedLines = added;
        SelectedRemovedLines = removed;
    }

    private FileDiff ApplyIntraLine(FileDiff diff)
    {
        var hunks = new List<DiffHunk>(diff.Hunks.Count);
        foreach (var h in diff.Hunks)
        {
            var lines = h.Lines.ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Kind != DiffLineKind.Removed) continue;
                // pair with following added
                var j = i + 1;
                while (j < lines.Count && lines[j].Kind == DiffLineKind.Removed) j++;
                if (j < lines.Count && lines[j].Kind == DiffLineKind.Added)
                {
                    var (oldSpans, newSpans) = _intraLine.Diff(lines[i].Text.Span, lines[j].Text.Span);
                    lines[i] = lines[i] with { IntraLine = oldSpans };
                    lines[j] = lines[j] with { IntraLine = newSpans };
                }
            }
            hunks.Add(h with { Lines = lines });
        }
        return diff with { Hunks = hunks };
    }

    private void ProjectRows(FileDiff diff)
    {
        DiffRows.Clear();
        IReadOnlyList<DiffRow> rows = ViewMode == DiffViewMode.SideBySide
            ? SideBySideRowProjector.Project(diff, collapseThreshold: 8, intraLineDiffer: _intraLine)
            : UnifiedRowProjector.Project(diff, collapseThreshold: 8, intraLineDiffer: _intraLine);
        foreach (var r in rows)
            DiffRows.Add(r);
    }

    [RelayCommand]
    private async Task StageFileAsync(FileItemViewModel? file)
    {
        if (file is null || _repoPath is null) return;
        var pending = new PendingMutation(file.Path, WasUnstage: false);
        _pending.Add(pending);
        await RefreshAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            await _staging.StageFileAsync(_repoPath, file.Path);
            CodeReviewrMeters.StageMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Stage failed: {ex.Message}", () => _ = StageFileAsync(file));
        }
        finally
        {
            _pending.Remove(pending);
            await RefreshAsync();
            await LoadDiffForSelectionAsync(SelectedFile);
        }
    }

    [RelayCommand]
    private async Task UnstageFileAsync(FileItemViewModel? file)
    {
        if (file is null || _repoPath is null) return;
        var pending = new PendingMutation(file.Path, WasUnstage: true);
        _pending.Add(pending);
        await RefreshAsync();
        try
        {
            await _staging.UnstageFileAsync(_repoPath, file.Path);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Unstage failed: {ex.Message}", () => _ = UnstageFileAsync(file));
        }
        finally
        {
            _pending.Remove(pending);
            await RefreshAsync();
            await LoadDiffForSelectionAsync(SelectedFile);
        }
    }

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (_repoPath is null) return;
        var files = UnstagedFiles.ToList();
        foreach (var file in files)
            await StageFileAsync(file);
    }

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        if (_repoPath is null) return;
        var files = StagedFiles.ToList();
        foreach (var file in files)
            await UnstageFileAsync(file);
    }

    [RelayCommand]
    private Task ToggleFileStagedAsync(FileItemViewModel? file)
    {
        if (file is null) return Task.CompletedTask;
        return file.IsStagedList ? UnstageFileAsync(file) : StageFileAsync(file);
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (_repoPath is null) return;
        try
        {
            await _branches.FetchAsync(_repoPath);
            await LoadBranchesAsync();
            _notifications.Info("Fetch completed");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Fetch failed: {ex.Message}", () => _ = FetchAsync());
        }
    }

    [RelayCommand]
    private async Task StageHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Target != DiffTarget.IndexToWorktree) return;
        var patch = PatchSynthesizer.SynthesizeHunks(_currentDiff, [SelectedHunkIndex]);
        var pending = new PendingMutation(_currentDiff.NewPath, WasUnstage: false);
        _pending.Add(pending);
        ProjectRowsOptimisticRemoveHunk(SelectedHunkIndex);
        try
        {
            var sw = Stopwatch.StartNew();
            await _staging.StagePatchAsync(_repoPath, patch);
            CodeReviewrMeters.StageMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Stage hunk failed: {ex.Message}");
        }
        finally
        {
            _pending.Remove(pending);
            await RefreshAsync();
            await LoadDiffForSelectionAsync(SelectedFile);
        }
    }

    [RelayCommand]
    private async Task UnstageHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Target != DiffTarget.HeadToIndex) return;
        var patch = PatchSynthesizer.SynthesizeHunks(_currentDiff, [SelectedHunkIndex]);
        try
        {
            await _staging.UnstagePatchAsync(_repoPath, patch);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Unstage hunk failed: {ex.Message}");
        }
        finally
        {
            await RefreshAsync();
            await LoadDiffForSelectionAsync(SelectedFile);
        }
    }

    private void ProjectRowsOptimisticRemoveHunk(int hunkIndex)
    {
        if (_currentDiff is null) return;
        var remaining = _currentDiff.Hunks.Where((_, i) => i != hunkIndex).ToList();
        var optimistic = _currentDiff with { Hunks = remaining };
        ProjectRows(optimistic);
    }

    [RelayCommand]
    private async Task DiscardFileAsync(FileItemViewModel? file)
    {
        if (file is null || _repoPath is null) return;
        try
        {
            await _discard.DiscardFileAsync(_repoPath, file.Path);
            var entry = _discard.RecentlyDiscarded.FirstOrDefault(e => e.Path.Equals(file.Path));
            _notifications.Info($"Discarded {file.Path.Name}",
                entry is null ? null : () => _ = RestoreDiscardedAsync(entry),
                "Undo");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _notifications.Error($"Discard failed: {ex.Message}");
        }
    }

    private async Task RestoreDiscardedAsync(DiscardedEntry entry)
    {
        if (_repoPath is null) return;
        await _discard.RestoreDiscardedAsync(_repoPath, entry);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (_repoPath is null || string.IsNullOrWhiteSpace(CommitMessage)) return;
        IsCommitting = true;
        HookOutput = "";
        try
        {
            var sw = Stopwatch.StartNew();
            var progress = new Progress<string>(line => HookOutput += line + Environment.NewLine);
            await _commit.CommitAsync(_repoPath, CommitMessage, AmendCommit, NoVerify, progress);
            CodeReviewrMeters.CommitMs.Record(sw.Elapsed.TotalMilliseconds);
            CommitMessage = "";
            AmendCommit = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _notifications.Error($"Commit failed: {ex.Message}", () => _ = CommitAsync());
        }
        finally
        {
            IsCommitting = false;
        }
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (_repoPath is null) return;
        try
        {
            var sw = Stopwatch.StartNew();
            await _remotes.PushAsync(_repoPath, null);
            CodeReviewrMeters.PushMs.Record(sw.Elapsed.TotalMilliseconds);
            _notifications.Info("Push completed");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Push failed: {ex.Message}", () => _ = PushAsync());
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        if (_repoPath is null) return;
        try
        {
            var sw = Stopwatch.StartNew();
            await _remotes.PullAsync(_repoPath, PullMode.FfOnly, null);
            CodeReviewrMeters.PullMs.Record(sw.Elapsed.TotalMilliseconds);
            await RefreshAsync();
            _notifications.Info("Pull completed");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Pull failed: {ex.Message}", () => _ = PullAsync());
        }
    }

    [RelayCommand]
    private async Task AbortInProgressAsync()
    {
        if (_repoPath is null) return;
        await _conflicts.AbortAsync(_repoPath);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ContinueInProgressAsync()
    {
        if (_repoPath is null) return;
        await _conflicts.ContinueAsync(_repoPath);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenMergetoolAsync(FileItemViewModel? file)
    {
        if (_repoPath is null) return;
        await _conflicts.OpenMergetoolAsync(_repoPath, file?.Path);
    }

    [RelayCommand]
    private async Task MarkResolvedAsync(FileItemViewModel? file)
    {
        if (_repoPath is null || file is null) return;
        await _conflicts.MarkResolvedAsync(_repoPath, file.Path);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CheckoutBranchAsync(BranchInfo? branch)
    {
        if (_repoPath is null || branch is null) return;
        try
        {
            await _branches.CheckoutAsync(_repoPath, branch.Name);
            await RefreshAsync();
            await LoadBranchesAsync();
        }
        catch (GitException ex) when (ex.Message.Contains("local changes", StringComparison.OrdinalIgnoreCase)
                                       || ex.StderrSummary?.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase) == true)
        {
            _notifications.Info("Checkout blocked by local changes. Stash and retry?",
                () => _ = StashThenCheckoutAsync(branch.Name), "Stash");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Checkout failed: {ex.Message}");
        }
    }

    private async Task StashThenCheckoutAsync(string branch)
    {
        if (_repoPath is null) return;
        await _stash.StashPushAsync(_repoPath, "CodeReviewr auto-stash");
        await _branches.CheckoutAsync(_repoPath, branch);
        _notifications.Info("Stashed and checked out. Pop stash?",
            () => _ = PopStashAsync(), "Pop stash");
        await RefreshAsync();
        await LoadBranchesAsync();
    }

    private async Task PopStashAsync()
    {
        if (_repoPath is null) return;
        await _stash.StashPopAsync(_repoPath);
        await RefreshAsync();
    }

    private async Task LoadBranchesAsync()
    {
        if (_repoPath is null) return;
        Branches.Clear();
        foreach (var b in await _branches.ListBranchesAsync(_repoPath))
            Branches.Add(b);
    }

    private sealed record PendingMutation(FilePath Path, bool WasUnstage);
}

public partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(FilePath path, ChangeKind kind, bool isStagedList, bool isPartial = false, bool isOptimistic = false, bool isConflicted = false)
    {
        Path = path;
        Kind = kind;
        IsStagedList = isStagedList;
        IsPartial = isPartial;
        IsOptimistic = isOptimistic;
        IsConflicted = isConflicted;
    }

    public FilePath Path { get; }
    public ChangeKind Kind { get; }
    public bool IsStagedList { get; }
    public bool IsPartial { get; }
    public bool IsOptimistic { get; }
    public bool IsConflicted { get; }
    public string Name => Path.Name;
    public string Directory => Path.Directory ?? "";
    public string KindLabel => Kind.ToString();
    public bool IsChecked => IsStagedList;

    public string StatusBadge => Kind switch
    {
        ChangeKind.Added or ChangeKind.Copied => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Modified or ChangeKind.TypeChanged => "M",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "U",
        ChangeKind.Conflicted => "C",
        ChangeKind.Ignored => "I",
        _ => "?",
    };

    public static FileItemViewModel From(StatusEntry e, bool isStagedList) =>
        new(e.Path, e.Kind, isStagedList,
            isPartial: e.IsStaged && e.IsUnstaged,
            isConflicted: e.IsConflicted);
}
