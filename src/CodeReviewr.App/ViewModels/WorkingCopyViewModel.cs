using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
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
    private readonly IConfirmDialog _confirm;
    private readonly IIntraLineDiffer _intraLine;
    private readonly IGitProcessRunner _runner;
    private readonly GitRepositoryWatcher _watcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _diffCts;
    private string? _repoPath;
    private FileDiff? _currentDiff;
    private readonly List<PendingMutation> _pending = [];
    private long _statusEpoch;
    private readonly List<FileItemViewModel> _allStaged = [];
    private readonly List<FileItemViewModel> _allUnstaged = [];
    private readonly List<FileItemViewModel> _allConflicted = [];
    private readonly List<FileItemViewModel> _selectedFiles = [];
    private bool _suppressSelectionSync;
    private bool _skipNextSelectedFileLoad;
    private readonly HashSet<(int HunkIndex, int LineIndexInHunk)> _expandedCollapses = [];
    private const int DefaultCollapseThreshold = 8;
    private const int FullFileContextLines = 100_000;

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
        IConfirmDialog confirm,
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
        _confirm = confirm;
        _intraLine = intraLine;
        _runner = runner;
        _watcher = watcher;
        ViewMode = settings.Current.DefaultDiffMode;
        _ignoreWhitespace = settings.Current.IgnoreWhitespace;
        _contextLines = settings.Current.ContextLines > 0 ? settings.Current.ContextLines : 3;
        // Watcher callbacks arrive on thread-pool / FileSystemWatcher threads.
        _watcher.RefreshRequested += () =>
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _watcher.OfferFsmonitor += () =>
            Dispatcher.UIThread.Post(() =>
                _notifications.Info("Status is slow. Enable Git fsmonitor for this repository?",
                    () => _ = EnableFsmonitorAsync(), "Enable"));
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
    [ObservableProperty] private bool _pushAfterCommit;
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
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private bool _isPushing;
    [ObservableProperty] private bool _isPulling;
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private int _selectedFileCount;
    [ObservableProperty] private bool _hasStagedSelection;
    [ObservableProperty] private bool _hasUnstagedSelection;
    [ObservableProperty] private string _diffEmptyMessage = "Select a file to view its diff";
    [ObservableProperty] private string? _diffOverlayMessage;
    [ObservableProperty] private bool _ignoreWhitespace;
    [ObservableProperty] private int _contextLines = 3;
    [ObservableProperty] private bool _showFullFile;

    /// <summary>Raised after the VM restores selection following a list rebuild so the view can rebind ListBoxes.</summary>
    public event Action? SelectionSyncRequested;

    public int[] ContextLineOptions { get; } = [1, 3, 5, 10, 25];

    /// <summary>ComboBox index for <see cref="ContextLineOptions"/> (avoids object↔int SelectedItem binding).</summary>
    public int ContextLinesIndex
    {
        get
        {
            var idx = Array.IndexOf(ContextLineOptions, ContextLines);
            return idx >= 0 ? idx : 1; // default to 3
        }
        set
        {
            if (value < 0 || value >= ContextLineOptions.Length) return;
            ContextLines = ContextLineOptions[value];
        }
    }

    public string FullFileToggleLabel => ShowFullFile ? "Diff only" : "Full file";

    public IReadOnlyList<FileItemViewModel> SelectedFilesSnapshot => _selectedFiles;

    public bool HasRepository => _repoPath is not null;

    public bool IsRemoteBusy => IsPushing || IsPulling || IsFetching;

    public string CommitButtonLabel =>
        string.IsNullOrEmpty(CurrentBranch) ? "Commit" : $"Commit to {CurrentBranch}";

    public string DiffFooterText =>
        StagingDisabledReason
        ?? (SelectedFileCount > 1 ? $"{SelectedFileCount} files selected"
            : SelectedFile is null ? "Select a file to view its diff"
            : IsLoadingDiff ? "Loading diff…"
            : SelectedAddedLines + SelectedRemovedLines == 0 ? "No line changes"
            : $"{SelectedAddedLines} additions, {SelectedRemovedLines} deletions");

    public bool HasConflictedFiles => ConflictedFiles.Count > 0;

    public bool HasStagedFiles => StagedFiles.Count > 0;

    partial void OnCurrentBranchChanged(string? value) => OnPropertyChanged(nameof(CommitButtonLabel));
    partial void OnStagingDisabledReasonChanged(string? value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnIsLoadingDiffChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffFooterText));
        UpdateDiffOverlay();
    }
    partial void OnSelectedAddedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnSelectedRemovedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnFileFilterChanged(string value) => ApplyFileFilter();
    partial void OnIsPushingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnIsPullingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnIsFetchingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnSelectedFileCountChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnHasUnstagedSelectionChanged(bool value) => OnPropertyChanged(nameof(CanDiscardSelection));

    partial void OnIgnoreWhitespaceChanged(bool value)
    {
        _settings.Update(s => s.IgnoreWhitespace = value);
        _ = _settings.SaveAsync();
        _ = LoadDiffForSelectionAsync(SelectedFile);
    }

    partial void OnContextLinesChanged(int value)
    {
        OnPropertyChanged(nameof(ContextLinesIndex));
        if (value <= 0) return;
        _settings.Update(s => s.ContextLines = value);
        _ = _settings.SaveAsync();
        if (!ShowFullFile)
            _ = LoadDiffForSelectionAsync(SelectedFile);
    }

    partial void OnShowFullFileChanged(bool value)
    {
        OnPropertyChanged(nameof(FullFileToggleLabel));
        _expandedCollapses.Clear();
        _ = LoadDiffForSelectionAsync(SelectedFile);
    }

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
        await _refreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_repoPath is null) return;
            var sw = Stopwatch.StartNew();
            var status = await _statusService.GetStatusAsync(_repoPath).ConfigureAwait(true);
            if (status.Epoch < _statusEpoch) return;
            _statusEpoch = status.Epoch;

            void ApplyStatus()
            {
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
            }

            await InvokeOnUiAsync(ApplyStatus);

            CodeReviewrMeters.StatusRefreshMs.Record(sw.Elapsed.TotalMilliseconds);
            CodeReviewrMeters.RepositoryOpenMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void RebuildFileLists(RepositoryStatus status)
    {
        _allStaged.Clear();
        _allUnstaged.Clear();
        _allConflicted.Clear();

        var pendingPaths = _pending.Select(p => p.Path.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var e in status.Staged)
        {
            if (pendingPaths.Contains(e.Path.Value) && _pending.Any(p => p.Path.Equals(e.Path) && p.WasUnstage))
                continue;
            _allStaged.Add(FileItemViewModel.From(e, isStagedList: true));
        }

        foreach (var e in status.Unstaged)
        {
            if (pendingPaths.Contains(e.Path.Value) && _pending.Any(p => p.Path.Equals(e.Path) && !p.WasUnstage))
                continue;
            _allUnstaged.Add(FileItemViewModel.From(e, isStagedList: false));
        }

        // Optimistic overlays: move predicted staged/unstaged
        foreach (var p in _pending.Where(p => !p.WasUnstage))
        {
            var path = p.Path.Value;
            if (_allUnstaged.All(f => f.Path.Value != path)
                && _allStaged.All(f => f.Path.Value != path))
                _allStaged.Add(new FileItemViewModel(p.Path, ChangeKind.Modified, isStagedList: true, isPartial: true, isOptimistic: true));
        }

        foreach (var e in status.Conflicted)
            _allConflicted.Add(FileItemViewModel.From(e, isStagedList: false));

        WorkingCopyChangeCount = _allStaged.Count + _allUnstaged.Count + _allConflicted.Count;
        ApplyFileFilter();
    }

    private void ApplyFileFilter()
    {
        var previousKeys = _selectedFiles
            .Select(f => (Path: f.Path.Value, f.IsStagedList))
            .ToList();

        _suppressSelectionSync = true;
        try
        {
            StagedFiles.Clear();
            UnstagedFiles.Clear();
            ConflictedFiles.Clear();

            foreach (var f in _allStaged.Where(MatchesFilter))
                StagedFiles.Add(f);
            foreach (var f in _allUnstaged.Where(MatchesFilter))
                UnstagedFiles.Add(f);
            foreach (var f in _allConflicted.Where(MatchesFilter))
                ConflictedFiles.Add(f);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        OnPropertyChanged(nameof(HasConflictedFiles));
        OnPropertyChanged(nameof(HasStagedFiles));

        if (previousKeys.Count == 0) return;

        var all = StagedFiles.Concat(UnstagedFiles).Concat(ConflictedFiles).ToList();
        var restored = new List<FileItemViewModel>();
        foreach (var key in previousKeys)
        {
            var match = all.FirstOrDefault(f =>
                string.Equals(f.Path.Value, key.Path, StringComparison.Ordinal)
                && f.IsStagedList == key.IsStagedList);
            if (match is null)
            {
                // Preferred list side disappeared (fully moved); fall back to same path other side.
                match = all.FirstOrDefault(f =>
                    string.Equals(f.Path.Value, key.Path, StringComparison.Ordinal));
            }

            if (match is not null && restored.All(r => !ReferenceEquals(r, match)))
                restored.Add(match);
        }

        ApplySelectionState(restored, requestViewSync: true);
    }

    private bool MatchesFilter(FileItemViewModel file)
    {
        if (string.IsNullOrWhiteSpace(FileFilter)) return true;
        var path = file.Path.Value ?? "";
        var name = file.Name ?? "";
        return path.Contains(FileFilter, StringComparison.OrdinalIgnoreCase)
               || name.Contains(FileFilter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Called from the view when ListBox multi-selection changes.</summary>
    public void SetFileSelection(IReadOnlyList<FileItemViewModel> selected)
    {
        if (_suppressSelectionSync) return;
        ApplySelectionState(selected, requestViewSync: false);
    }

    private void ApplySelectionState(IReadOnlyList<FileItemViewModel> selected, bool requestViewSync)
    {
        _selectedFiles.Clear();
        _selectedFiles.AddRange(selected);
        SelectedFileCount = _selectedFiles.Count;
        HasStagedSelection = _selectedFiles.Any(f => f.IsStagedList);
        HasUnstagedSelection = _selectedFiles.Any(f => !f.IsStagedList && !f.IsConflicted);

        if (_selectedFiles.Count == 1)
        {
            var file = _selectedFiles[0];
            DiffEmptyMessage = "Select a file to view its diff";
            DiffOverlayMessage = null;
            if (SameSelectionIdentity(SelectedFile, file))
            {
                if (!ReferenceEquals(SelectedFile, file))
                {
                    _skipNextSelectedFileLoad = true;
                    SelectedFile = file;
                }
            }
            else if (!ReferenceEquals(SelectedFile, file))
            {
                SelectedFile = file;
            }
            else
            {
                _ = LoadDiffForSelectionAsync(file);
            }
        }
        else if (_selectedFiles.Count > 1)
        {
            if (SelectedFile is not null)
                SelectedFile = null;
            DiffRows.Clear();
            _currentDiff = null;
            SelectedAddedLines = 0;
            SelectedRemovedLines = 0;
            DiffEmptyMessage = $"{_selectedFiles.Count} files selected";
            DiffOverlayMessage = $"{_selectedFiles.Count} files selected";
            OnPropertyChanged(nameof(DiffFooterText));
        }
        else
        {
            if (SelectedFile is not null)
                SelectedFile = null;
            DiffRows.Clear();
            _currentDiff = null;
            DiffEmptyMessage = "Select a file to view its diff";
            DiffOverlayMessage = null;
            OnPropertyChanged(nameof(DiffFooterText));
        }

        UpdateDiffOverlay();

        if (requestViewSync)
            SelectionSyncRequested?.Invoke();
    }

    private static bool SameSelectionIdentity(FileItemViewModel? a, FileItemViewModel? b) =>
        a is not null
        && b is not null
        && string.Equals(a.Path.Value, b.Path.Value, StringComparison.Ordinal)
        && a.IsStagedList == b.IsStagedList;

    private void UpdateDiffOverlay()
    {
        if (IsLoadingDiff)
        {
            DiffOverlayMessage = null;
            return;
        }

        if (SelectedFileCount > 1)
            DiffOverlayMessage = $"{SelectedFileCount} files selected";
        else if (SelectedFileCount == 0)
            DiffOverlayMessage = null;
    }

    partial void OnSelectedFileChanged(FileItemViewModel? value)
    {
        if (_skipNextSelectedFileLoad)
        {
            _skipNextSelectedFileLoad = false;
            return;
        }

        if (SelectedFileCount <= 1)
            _ = LoadDiffForSelectionAsync(value);
    }

    partial void OnViewModeChanged(DiffViewMode value)
    {
        if (_currentDiff is null) return;
        // Instant switch: recompute layout only — zero git, zero tokenize
        ProjectRows(_currentDiff);
    }

    partial void OnIsCombinedReviewModeChanged(bool value) => _ = LoadDiffForSelectionAsync(SelectedFile);

    [RelayCommand]
    private void ToggleCombinedReview() => IsCombinedReviewMode = !IsCombinedReviewMode;

    [RelayCommand]
    private void ToggleShowFullFile() => ShowFullFile = !ShowFullFile;

    public void ExpandCollapsedSection(int hunkIndex, int lineIndexInHunk)
    {
        if (!_expandedCollapses.Add((hunkIndex, lineIndexInHunk))) return;
        if (_currentDiff is not null)
            ProjectRows(_currentDiff);
    }

    private async Task LoadDiffForSelectionAsync(FileItemViewModel? file)
    {
        _diffCts?.Cancel();
        _diffCts = new CancellationTokenSource();
        var ct = _diffCts.Token;

        DiffRows.Clear();
        _currentDiff = null;
        _expandedCollapses.Clear();
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
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));

        IsLoadingDiff = true;
        try
        {
            var sw = Stopwatch.StartNew();
            FileDiff diff;
            if (file.Kind == ChangeKind.Untracked)
            {
                var fullPath = System.IO.Path.Combine(
                    _repoPath,
                    file.Path.Value.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.File.Exists(fullPath))
                {
                    diff = UntrackedFileDiff.Create(file.Path, string.Empty, target);
                }
                else
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(fullPath, ct);
                    ct.ThrowIfCancellationRequested();
                    diff = UntrackedFileDiff.Create(file.Path, bytes, target);
                }
            }
            else
            {
                var options = BuildDiffOptions();
                diff = await _diffService.GetDiffAsync(_repoPath, file.Path, target, options, ct);
                ct.ThrowIfCancellationRequested();
            }

            _currentDiff = ApplyIntraLine(diff);
            UpdateDiffStats(_currentDiff);
            ProjectRows(_currentDiff);
            OnPropertyChanged(nameof(CanStageLines));
            OnPropertyChanged(nameof(CanUnstageLines));
            OnPropertyChanged(nameof(CanDiscardLines));
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

    private DiffOptions BuildDiffOptions()
    {
        var baseOptions = _settings.Current.ToDiffOptions() with
        {
            IgnoreAllSpace = IgnoreWhitespace,
            ContextLines = ShowFullFile ? FullFileContextLines : Math.Max(1, ContextLines),
        };
        return baseOptions;
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
        var threshold = ShowFullFile ? 0 : DefaultCollapseThreshold;
        IReadOnlyList<DiffRow> rows = ViewMode == DiffViewMode.SideBySide
            ? SideBySideRowProjector.Project(diff, threshold, _intraLine, _expandedCollapses)
            : UnifiedRowProjector.Project(diff, threshold, _intraLine, _expandedCollapses);
        foreach (var r in rows)
            DiffRows.Add(r);
    }

    /// <summary>Refresh status, then reload the open diff only if a mutated path is the selection.</summary>
    private async Task RefreshAndMaybeReloadDiffAsync(IReadOnlyList<FilePath> mutatedPaths)
    {
        await RefreshAsync();
        if (SelectedFile is null || mutatedPaths.Count == 0) return;
        var selectedPath = SelectedFile.Path.Value;
        if (mutatedPaths.Any(p => string.Equals(p.Value, selectedPath, StringComparison.Ordinal)))
            await LoadDiffForSelectionAsync(SelectedFile);
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
            await RefreshAndMaybeReloadDiffAsync([file.Path]);
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
            await RefreshAndMaybeReloadDiffAsync([file.Path]);
        }
    }

    [RelayCommand]
    private Task StageAllAsync() =>
        StageManyAsync(UnstagedFiles.ToList());

    [RelayCommand]
    private Task UnstageAllAsync() =>
        UnstageManyAsync(StagedFiles.ToList());

    [RelayCommand]
    private Task StageSelectedAsync() =>
        StageManyAsync(_selectedFiles.Where(f => !f.IsStagedList && !f.IsConflicted).ToList());

    [RelayCommand]
    private Task UnstageSelectedAsync() =>
        UnstageManyAsync(_selectedFiles.Where(f => f.IsStagedList).ToList());

    private async Task StageManyAsync(IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null || files.Count == 0) return;
        var pendings = files.Select(f => new PendingMutation(f.Path, WasUnstage: false)).ToList();
        var paths = files.Select(f => f.Path).ToList();
        _pending.AddRange(pendings);
        await RefreshAsync();
        try
        {
            await _staging.StageFilesAsync(_repoPath, paths);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Stage failed: {ex.Message}", () => _ = StageManyAsync(files));
        }
        finally
        {
            foreach (var pending in pendings)
                _pending.Remove(pending);
            await RefreshAndMaybeReloadDiffAsync(paths);
        }
    }

    private async Task UnstageManyAsync(IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null || files.Count == 0) return;
        var pendings = files.Select(f => new PendingMutation(f.Path, WasUnstage: true)).ToList();
        var paths = files.Select(f => f.Path).ToList();
        _pending.AddRange(pendings);
        await RefreshAsync();
        try
        {
            await _staging.UnstageFilesAsync(_repoPath, paths);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Unstage failed: {ex.Message}", () => _ = UnstageManyAsync(files));
        }
        finally
        {
            foreach (var pending in pendings)
                _pending.Remove(pending);
            await RefreshAndMaybeReloadDiffAsync(paths);
        }
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
        if (_repoPath is null || IsRemoteBusy) return;
        IsFetching = true;
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
        finally
        {
            IsFetching = false;
        }
    }

    [RelayCommand]
    private async Task StageHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Target != DiffTarget.IndexToWorktree) return;
        await ApplyHunkPatchAsync(SelectedHunkIndex, stage: true);
    }

    [RelayCommand]
    private async Task UnstageHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Target != DiffTarget.HeadToIndex) return;
        await ApplyHunkPatchAsync(SelectedHunkIndex, stage: false);
    }

    public Task StageHunkAtAsync(int hunkIndex)
    {
        SelectedHunkIndex = hunkIndex;
        return StageHunkAsync();
    }

    public Task UnstageHunkAtAsync(int hunkIndex)
    {
        SelectedHunkIndex = hunkIndex;
        return UnstageHunkAsync();
    }

    private async Task ApplyHunkPatchAsync(int hunkIndex, bool stage)
    {
        if (_currentDiff is null || _repoPath is null || hunkIndex < 0) return;
        var patch = PatchSynthesizer.SynthesizeHunks(_currentDiff, [hunkIndex]);
        var pending = stage
            ? new PendingMutation(_currentDiff.NewPath, WasUnstage: false)
            : null;
        if (pending is not null)
        {
            _pending.Add(pending);
            ProjectRowsOptimisticRemoveHunk(hunkIndex);
        }

        try
        {
            if (stage)
                await _staging.StagePatchAsync(_repoPath, patch);
            else
                await _staging.UnstagePatchAsync(_repoPath, patch);
        }
        catch (Exception ex)
        {
            _notifications.Error($"{(stage ? "Stage" : "Unstage")} hunk failed: {ex.Message}");
        }
        finally
        {
            if (pending is not null)
                _pending.Remove(pending);
            var path = _currentDiff?.NewPath ?? SelectedFile?.Path;
            if (path is { } p)
                await RefreshAndMaybeReloadDiffAsync([p]);
            else
                await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task StageSelectedLinesAsync(IReadOnlyList<LineSelection>? lines)
    {
        if (lines is null || lines.Count == 0 || _currentDiff is null || _repoPath is null) return;
        if (_currentDiff.Target != DiffTarget.IndexToWorktree) return;
        var path = _currentDiff.NewPath;
        try
        {
            var patch = PatchSynthesizer.SynthesizeLines(_currentDiff, lines);
            var pending = new PendingMutation(path, WasUnstage: false);
            _pending.Add(pending);
            try
            {
                await _staging.StagePatchAsync(_repoPath, patch);
            }
            finally
            {
                _pending.Remove(pending);
            }
        }
        catch (Exception ex)
        {
            _notifications.Error($"Stage lines failed: {ex.Message}");
        }
        finally
        {
            await RefreshAndMaybeReloadDiffAsync([path]);
        }
    }

    [RelayCommand]
    private async Task UnstageSelectedLinesAsync(IReadOnlyList<LineSelection>? lines)
    {
        if (lines is null || lines.Count == 0 || _currentDiff is null || _repoPath is null) return;
        if (_currentDiff.Target != DiffTarget.HeadToIndex) return;
        var path = _currentDiff.NewPath;
        try
        {
            var patch = PatchSynthesizer.SynthesizeLines(_currentDiff, lines);
            await _staging.UnstagePatchAsync(_repoPath, patch);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Unstage lines failed: {ex.Message}");
        }
        finally
        {
            await RefreshAndMaybeReloadDiffAsync([path]);
        }
    }

    public bool CanStageLines =>
        _currentDiff?.Target == DiffTarget.IndexToWorktree;

    public bool CanUnstageLines =>
        _currentDiff?.Target == DiffTarget.HeadToIndex;

    /// <summary>True when the open diff is a worktree diff (stage and discard line/hunk ops apply).</summary>
    public bool CanDiscardLines => CanStageLines;

    /// <summary>True when at least one selected file can be discarded (unstaged / untracked).</summary>
    public bool CanDiscardSelection => HasUnstagedSelection;

    private void ProjectRowsOptimisticRemoveHunk(int hunkIndex)
    {
        if (_currentDiff is null) return;
        var remaining = _currentDiff.Hunks.Where((_, i) => i != hunkIndex).ToList();
        var optimistic = _currentDiff with { Hunks = remaining };
        ProjectRows(optimistic);
    }

    [RelayCommand]
    private async Task DiscardSelectedFilesAsync()
    {
        var files = _selectedFiles.Where(f => !f.IsStagedList && !f.IsConflicted).ToList();
        await DiscardFilesWithConfirmAsync(files);
    }

    [RelayCommand]
    private async Task DiscardFileAsync(FileItemViewModel? file)
    {
        if (file is null || file.IsStagedList || file.IsConflicted) return;
        await DiscardFilesWithConfirmAsync([file]);
    }

    private async Task DiscardFilesWithConfirmAsync(IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null || files.Count == 0) return;

        var message = files.Count == 1
            ? $"Discard all changes in {files[0].Path.Name}? This cannot be undone except via Undo."
            : $"Discard changes in {files.Count} files? This cannot be undone except via Undo.";
        if (!await _confirm.ConfirmAsync("Discard changes", message))
            return;

        var discarded = new List<DiscardedEntry>();
        try
        {
            foreach (var file in files)
            {
                await _discard.DiscardFileAsync(_repoPath, file.Path);
                var entry = _discard.RecentlyDiscarded.FirstOrDefault(e => e.Path.Equals(file.Path));
                if (entry is not null)
                    discarded.Add(entry);
            }

            var label = files.Count == 1
                ? $"Discarded {files[0].Path.Name}"
                : $"Discarded {files.Count} files";
            _notifications.Info(
                label,
                discarded.Count == 0 ? null : () => _ = RestoreDiscardedManyAsync(discarded),
                "Undo");

            var paths = files.Select(f => f.Path).ToList();
            await RefreshAndMaybeReloadDiffAsync(paths);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Discard failed: {ex.Message}");
            await RefreshAsync();
        }
    }

    public Task DiscardHunkAtAsync(int hunkIndex)
    {
        SelectedHunkIndex = hunkIndex;
        return DiscardHunkAsync();
    }

    [RelayCommand]
    private async Task DiscardHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Target != DiffTarget.IndexToWorktree) return;

        var hunkIndex = SelectedHunkIndex;
        var path = _currentDiff.NewPath;
        try
        {
            var patch = PatchSynthesizer.SynthesizeHunks(_currentDiff, [hunkIndex]);
            await _discard.DiscardPatchAsync(_repoPath, patch);
            var entry = _discard.RecentlyDiscarded.FirstOrDefault(e => e.Path.Equals(path));
            _notifications.Info(
                $"Discarded hunk in {path.Name}",
                entry is null ? null : () => _ = RestoreDiscardedAsync(entry),
                "Undo");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Discard hunk failed: {ex.Message}");
        }
        finally
        {
            await RefreshAndMaybeReloadDiffAsync([path]);
        }
    }

    [RelayCommand]
    private async Task DiscardSelectedLinesAsync(IReadOnlyList<LineSelection>? lines)
    {
        if (lines is null || lines.Count == 0 || _currentDiff is null || _repoPath is null) return;
        if (_currentDiff.Target != DiffTarget.IndexToWorktree) return;

        var path = _currentDiff.NewPath;
        try
        {
            var patch = PatchSynthesizer.SynthesizeLines(_currentDiff, lines);
            await _discard.DiscardPatchAsync(_repoPath, patch);
            var entry = _discard.RecentlyDiscarded.FirstOrDefault(e => e.Path.Equals(path));
            _notifications.Info(
                $"Discarded {lines.Count} line(s) in {path.Name}",
                entry is null ? null : () => _ = RestoreDiscardedAsync(entry),
                "Undo");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Discard lines failed: {ex.Message}");
        }
        finally
        {
            await RefreshAndMaybeReloadDiffAsync([path]);
        }
    }

    private async Task RestoreDiscardedAsync(DiscardedEntry entry)
    {
        if (_repoPath is null) return;
        await _discard.RestoreDiscardedAsync(_repoPath, entry);
        await RefreshAsync();
    }

    private async Task RestoreDiscardedManyAsync(IReadOnlyList<DiscardedEntry> entries)
    {
        if (_repoPath is null) return;
        foreach (var entry in entries.Reverse())
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
            if (PushAfterCommit)
                await ExecutePushAsync();
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
        if (_repoPath is null || IsRemoteBusy) return;
        await ExecutePushAsync();
    }

    private async Task ExecutePushAsync()
    {
        if (_repoPath is null) return;
        IsPushing = true;
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
        finally
        {
            IsPushing = false;
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        if (_repoPath is null || IsRemoteBusy) return;
        IsPulling = true;
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
        finally
        {
            IsPulling = false;
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

    private static async Task InvokeOnUiAsync(Action action)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            // Unit tests reference Avalonia but have no running lifetime — InvokeAsync would hang.
            if (Application.Current is null)
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
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
