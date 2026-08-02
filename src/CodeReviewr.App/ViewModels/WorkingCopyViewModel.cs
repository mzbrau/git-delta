using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;

namespace CodeReviewr.App.ViewModels;

public partial class WorkingCopyViewModel : ObservableObject
{
    private readonly IGitStatusService _statusService;
    private readonly IGitDiffService _diffService;
    private readonly IGitStagingService _staging;
    private readonly IGitDiscardService _discard;
    private readonly IGitObjectReader _objects;
    private readonly IGitCommitService _commit;
    private readonly IGitBranchService _branches;
    private readonly IGitRemoteService _remotes;
    private readonly IGitConflictService _conflicts;
    private readonly IGitStashService _stash;
    private readonly IGitHistoryService _history;
    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly IConfirmDialog _confirm;
    private readonly IStashDialog _stashDialog;
    private readonly IIntraLineDiffer _intraLine;
    private readonly ISyntaxTokenService? _syntaxTokens;
    private readonly IFsmonitorService _fsmonitor;
    private readonly IRepositoryWatcher _watcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _historyCts;
    private CancellationTokenSource? _commitFilesCts;
    private string? _cachedHistorySelectedPath;
    private string? _repoPath;
    private RepositoryStatus? _lastStatus;
    private readonly DiffWarmStore _warmStore;
    private readonly List<CommitInfo> _allHistoryCommits = [];
    private readonly List<FileItemViewModel> _allHistoryFiles = [];
    private const int HistoryPageSize = 300;
    private FileDiff? _currentDiff;
    private DateTimeOffset? _diffCacheCompletedAt;
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
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    public WorkingCopyViewModel(
        IGitStatusService statusService,
        IGitDiffService diffService,
        IGitStagingService staging,
        IGitDiscardService discard,
        IGitObjectReader objects,
        IGitCommitService commit,
        IGitBranchService branches,
        IGitRemoteService remotes,
        IGitConflictService conflicts,
        IGitStashService stash,
        IGitHistoryService history,
        ISettingsStore settings,
        NotificationService notifications,
        IConfirmDialog confirm,
        IStashDialog stashDialog,
        IIntraLineDiffer intraLine,
        IFsmonitorService fsmonitor,
        IRepositoryWatcher watcher,
        ISyntaxTokenService? syntaxTokens = null)
    {
        _statusService = statusService;
        _diffService = diffService;
        _staging = staging;
        _discard = discard;
        _objects = objects;
        _commit = commit;
        _branches = branches;
        _remotes = remotes;
        _conflicts = conflicts;
        _stash = stash;
        _history = history;
        _settings = settings;
        _notifications = notifications;
        _confirm = confirm;
        _stashDialog = stashDialog;
        _intraLine = intraLine;
        _syntaxTokens = syntaxTokens;
        _fsmonitor = fsmonitor;
        _watcher = watcher;
        _warmStore = new DiffWarmStore(DiffWarmStore.ClampConcurrency(settings.Current.DiffPrefetchConcurrency));
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

    /// <summary>Called when the main window is activated so the watcher can debounce a soft refresh.</summary>
    public void NotifyWindowActivated()
    {
        _watcher.NotifyWindowActivated();
        if (IsHistoryMode && _allHistoryCommits.Count > 0)
            _ = SoftRefreshHistoryAsync();
    }

    /// <summary>Updates the warm-store prefetch concurrency cap (clamped 1–8).</summary>
    public void SetDiffPrefetchConcurrency(int value) =>
        _warmStore.SetMaxConcurrency(value);

    public ObservableCollection<FileItemViewModel> StagedFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> UnstagedFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> ConflictedFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> StashFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> HistoryFiles { get; } = [];
    public ObservableCollection<CommitInfo> HistoryCommits { get; } = [];
    public ObservableCollection<DiffRow> DiffRows { get; } = [];
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    public ObservableCollection<StashInfo> Stashes { get; } = [];

    [ObservableProperty] private string? _repositoryPath;
    [ObservableProperty] private string? _currentBranch;
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private DiffViewMode _viewMode;
    [ObservableProperty] private bool _isLoadingDiff;
    [ObservableProperty] private bool _isDiffRefreshing;
    [ObservableProperty] private bool _hasDiffCache;
    [ObservableProperty] private string? _diffCacheAgeText;
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
    [ObservableProperty] private bool _workspaceExpanded = true;
    [ObservableProperty] private bool _branchesExpanded;
    [ObservableProperty] private bool _stashesExpanded = true;
    [ObservableProperty] private bool _pullRequestsExpanded = true;
    [ObservableProperty] private bool _prRequestedExpanded = true;
    [ObservableProperty] private bool _prRaisedExpanded = true;
    [ObservableProperty] private WorkspaceMode _workspaceMode = WorkspaceMode.FileStatus;
    [ObservableProperty] private StashInfo? _selectedStash;
    [ObservableProperty] private CommitInfo? _selectedCommit;
    [ObservableProperty] private bool _isStashing;
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private bool _hasFileFilter;
    [ObservableProperty] private string _historyFileFilter = "";
    [ObservableProperty] private bool _hasHistoryFileFilter;
    [ObservableProperty] private string _historySearchText = "";
    [ObservableProperty] private bool _hasHistorySearch;
    [ObservableProperty] private bool _isHistoryLoading;
    [ObservableProperty] private bool _isHistoryRefreshing;
    [ObservableProperty] private bool _hasMoreHistory;
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
    [ObservableProperty] private bool _isImagePreview;
    [ObservableProperty] private bool _hasImageBefore;
    [ObservableProperty] private bool _isSingleImagePreview;
    [ObservableProperty] private Bitmap? _imageBefore;
    [ObservableProperty] private Bitmap? _imageAfter;
    [ObservableProperty] private FileSyntaxTokens? _leftSyntaxTokens;
    [ObservableProperty] private FileSyntaxTokens? _rightSyntaxTokens;

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

    public string RevealInFileManagerLabel => FileManagerReveal.Label;

    public string? SelectedFileAbsolutePath =>
        _repoPath is null || SelectedFile is null
            ? null
            : AbsolutePathFor(SelectedFile);

    public IReadOnlyList<FileItemViewModel> SelectedFilesSnapshot => _selectedFiles;

    public bool HasRepository => _repoPath is not null;

    public bool IsRemoteBusy => IsPushing || IsPulling || IsFetching || IsStashing;

    public bool IsFileStatusMode => WorkspaceMode == WorkspaceMode.FileStatus;
    public bool IsHistoryMode => WorkspaceMode == WorkspaceMode.History;
    public bool IsStashMode => WorkspaceMode == WorkspaceMode.Stash;

    public bool CanLoadMoreHistory =>
        HasMoreHistory && !IsHistoryLoading && !IsHistoryRefreshing;

    public string FileListHeader => WorkspaceMode switch
    {
        WorkspaceMode.History => "History",
        WorkspaceMode.Stash when SelectedStash is { } s => s.DisplayTitle,
        WorkspaceMode.Stash => "Stash",
        _ => "File Status",
    };

    public bool IsFileStatusNavSelected => WorkspaceMode == WorkspaceMode.FileStatus;
    public bool IsHistoryNavSelected => WorkspaceMode == WorkspaceMode.History;

    public string CommitButtonLabel =>
        string.IsNullOrEmpty(CurrentBranch) ? "Commit" : $"Commit to {CurrentBranch}";

    public string DiffFooterText =>
        StagingDisabledReason
        ?? (SelectedFileCount > 1 ? $"{SelectedFileCount} files selected"
            : SelectedFile is null ? "Select a file to view its diff"
            : IsLoadingDiff ? "Loading diff…"
            : IsDiffRefreshing ? "Refreshing diff…"
            : SelectedAddedLines + SelectedRemovedLines == 0 ? "No line changes"
            : $"{SelectedAddedLines} additions, {SelectedRemovedLines} deletions");

    public string? DiffFreshnessText =>
        IsDiffRefreshing
            ? (DiffCacheAgeText is { } age ? $"Refreshing… · {age}" : "Refreshing…")
            : DiffCacheAgeText;

    public bool HasConflictedFiles => ConflictedFiles.Count > 0;

    public bool HasStagedFiles => StagedFiles.Count > 0;
    public bool ShowCommitDock => IsFileStatusMode && HasStagedFiles;
    public bool ShowCommitDetailsDock => IsHistoryMode && SelectedCommit is not null;
    public bool ShowStashDetailsDock => IsStashMode && SelectedStash is not null;

    public string? SelectedStashMessage => SelectedStash?.DisplayTitle;
    public string? SelectedStashRef => SelectedStash?.Ref;
    public string? SelectedStashBranch => SelectedStash?.BranchHint;
    public bool HasSelectedStashBranch => !string.IsNullOrWhiteSpace(SelectedStashBranch);

    public string? SelectedCommitSubject => SelectedCommit?.Subject;
    public string? SelectedCommitBody =>
        SelectedCommit is { Body.Length: > 0 } c ? c.Body : null;
    public bool HasSelectedCommitBody => !string.IsNullOrWhiteSpace(SelectedCommitBody);
    public string? SelectedCommitOid => SelectedCommit?.Oid;
    public string? SelectedCommitAuthor => SelectedCommit?.AuthorDisplay;
    public string? SelectedCommitDate =>
        SelectedCommit is null ? null : FormatCommitDate(SelectedCommit.AuthorDate);
    public string? SelectedCommitDecorations =>
        SelectedCommit is { Decorations.Count: > 0 } c ? c.DecorationsDisplay : null;

    partial void OnCurrentBranchChanged(string? value) => OnPropertyChanged(nameof(CommitButtonLabel));
    partial void OnStagingDisabledReasonChanged(string? value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnIsLoadingDiffChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffFooterText));
        OnPropertyChanged(nameof(DiffFreshnessText));
        UpdateDiffOverlay();
    }

    partial void OnIsDiffRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffFooterText));
        OnPropertyChanged(nameof(DiffFreshnessText));
    }

    partial void OnDiffCacheAgeTextChanged(string? value) => OnPropertyChanged(nameof(DiffFreshnessText));
    partial void OnSelectedAddedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnSelectedRemovedLinesChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnFileFilterChanged(string value)
    {
        HasFileFilter = !string.IsNullOrWhiteSpace(value);
        if (IsStashMode && SelectedStash is not null)
            _ = SelectStashAsync(SelectedStash);
        else
            ApplyFileFilter();
    }

    partial void OnHistoryFileFilterChanged(string value)
    {
        HasHistoryFileFilter = !string.IsNullOrWhiteSpace(value);
        ApplyHistoryFileFilter();
    }

    partial void OnHistorySearchTextChanged(string value)
    {
        HasHistorySearch = !string.IsNullOrWhiteSpace(value);
        ApplyHistoryFilter();
    }

    partial void OnIsHistoryLoadingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreHistory));
    partial void OnIsHistoryRefreshingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreHistory));
    partial void OnHasMoreHistoryChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreHistory));

    [RelayCommand]
    private void ClearFileFilter() => FileFilter = "";

    [RelayCommand]
    private void ClearHistoryFileFilter() => HistoryFileFilter = "";

    [RelayCommand]
    private void ClearHistorySearch() => HistorySearchText = "";
    partial void OnIsPushingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnIsPullingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnIsFetchingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnIsStashingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteBusy));
    partial void OnSelectedFileCountChanged(int value) => OnPropertyChanged(nameof(DiffFooterText));
    partial void OnHasUnstagedSelectionChanged(bool value) => OnPropertyChanged(nameof(CanDiscardSelection));
    partial void OnHasStagedSelectionChanged(bool value) => OnPropertyChanged(nameof(CanDiscardSelection));

    partial void OnIgnoreWhitespaceChanged(bool value)
    {
        _settings.Update(s => s.IgnoreWhitespace = value);
        _ = _settings.SaveAsync();
        _warmStore.InvalidateAll();
        _ = LoadDiffForSelectionAsync(SelectedFile);
        ScheduleFileStatusPrefetch();
    }

    partial void OnContextLinesChanged(int value)
    {
        OnPropertyChanged(nameof(ContextLinesIndex));
        if (value <= 0) return;
        _settings.Update(s => s.ContextLines = value);
        _ = _settings.SaveAsync();
        if (!ShowFullFile)
        {
            _warmStore.InvalidateAll();
            _ = LoadDiffForSelectionAsync(SelectedFile);
            ScheduleFileStatusPrefetch();
        }
    }

    partial void OnShowFullFileChanged(bool value)
    {
        OnPropertyChanged(nameof(FullFileToggleLabel));
        _expandedCollapses.Clear();
        _warmStore.InvalidateAll();
        _ = LoadDiffForSelectionAsync(SelectedFile);
        ScheduleFileStatusPrefetch();
    }

    public async Task OpenAsync(string path)
    {
        _repoPath = path;
        RepositoryPath = path;
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(SelectedFileAbsolutePath));
        WorkspaceMode = WorkspaceMode.FileStatus;
        SelectedStash = null;
        SelectedCommit = null;
        ClearHistoryState();
        _watcher.WatchRepository(path);
        await RefreshAsync();
        await LoadBranchesAsync();
        await LoadStashesAsync();
    }

    partial void OnWorkspaceModeChanged(WorkspaceMode value)
    {
        OnPropertyChanged(nameof(IsFileStatusMode));
        OnPropertyChanged(nameof(IsHistoryMode));
        OnPropertyChanged(nameof(IsStashMode));
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(IsFileStatusNavSelected));
        OnPropertyChanged(nameof(IsHistoryNavSelected));
        OnPropertyChanged(nameof(ShowCommitDock));
        OnPropertyChanged(nameof(ShowCommitDetailsDock));
        OnPropertyChanged(nameof(ShowStashDetailsDock));
    }

    partial void OnSelectedStashChanged(StashInfo? value)
    {
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(ShowStashDetailsDock));
        OnPropertyChanged(nameof(SelectedStashMessage));
        OnPropertyChanged(nameof(SelectedStashRef));
        OnPropertyChanged(nameof(SelectedStashBranch));
        OnPropertyChanged(nameof(HasSelectedStashBranch));
    }

    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        OnPropertyChanged(nameof(ShowCommitDetailsDock));
        OnPropertyChanged(nameof(SelectedCommitSubject));
        OnPropertyChanged(nameof(SelectedCommitBody));
        OnPropertyChanged(nameof(HasSelectedCommitBody));
        OnPropertyChanged(nameof(SelectedCommitOid));
        OnPropertyChanged(nameof(SelectedCommitAuthor));
        OnPropertyChanged(nameof(SelectedCommitDate));
        OnPropertyChanged(nameof(SelectedCommitDecorations));
    }
    private async Task EnableFsmonitorAsync()
    {
        if (_repoPath is null) return;
        await _fsmonitor.EnableAsync(_repoPath);
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
            _lastStatus = status;

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
            _warmStore.SoftInvalidateScope("fs");
            UpdateFileCacheIndicators();
            await RevalidateSelectedDiffAfterStatusAsync();
            ScheduleFileStatusPrefetch();

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
        OnPropertyChanged(nameof(ShowCommitDock));

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

    private bool MatchesFilter(FileItemViewModel file) => MatchesPathFilter(file, FileFilter);

    private bool MatchesHistoryFileFilter(FileItemViewModel file) =>
        MatchesPathFilter(file, HistoryFileFilter);

    private static bool MatchesPathFilter(FileItemViewModel file, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var path = file.Path.Value ?? "";
        var name = file.Name ?? "";
        return path.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
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
        OnPropertyChanged(nameof(SelectedFileAbsolutePath));

        if (_skipNextSelectedFileLoad)
        {
            _skipNextSelectedFileLoad = false;
            return;
        }

        if (SelectedFileCount <= 1)
            _ = LoadDiffForSelectionAsync(value);
    }

    [RelayCommand]
    private void RevealSelectedInFileManager() => RevealInFileManager(SelectedFile);

    [RelayCommand]
    private void RevealInFileManager(FileItemViewModel? file)
    {
        file ??= SelectedFile;
        if (file is null || _repoPath is null) return;
        FileManagerReveal.Reveal(AbsolutePathFor(file));
    }

    [RelayCommand]
    private void RevealRepositoryInFileManager()
    {
        if (_repoPath is null) return;
        FileManagerReveal.Reveal(_repoPath);
    }

    [RelayCommand]
    private async Task ViewRemoteAsync()
    {
        if (_repoPath is null) return;
        try
        {
            var remoteUrl = await _remotes.GetRemoteUrlAsync(_repoPath);
            var browse = RemoteWebUrl.ToBrowseUrl(remoteUrl);
            if (browse is null)
            {
                _notifications.Error(remoteUrl is null
                    ? "No remote named 'origin' is configured."
                    : $"Could not open remote URL in a browser: {remoteUrl}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = browse,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _notifications.Error($"View Remote failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SelectFileStatus()
    {
        if (IsHistoryMode)
        {
            _cachedHistorySelectedPath = SelectedFile?.Path.Value
                                         ?? _selectedFiles.FirstOrDefault()?.Path.Value;
            CancelCommitFilesLoad();
        }

        WorkspaceMode = WorkspaceMode.FileStatus;
        SelectedStash = null;
        // Keep history commits / SelectedCommit / HistoryFiles cached for instant revisit.
        _selectedFiles.Clear();
        SelectedFileCount = 0;
        SelectedFile = null;
        DiffRows.Clear();
        _currentDiff = null;
        ClearImagePreview();
        DiffEmptyMessage = "Select a file to view its diff";
        DiffOverlayMessage = null;
        ScheduleFileStatusPrefetch();
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(ShowCommitDetailsDock));
        OnPropertyChanged(nameof(DiffFooterText));
        SelectionSyncRequested?.Invoke();
    }

    [RelayCommand]
    private void SelectHistory()
    {
        WorkspaceMode = WorkspaceMode.History;
        SelectedStash = null;
        StashFiles.Clear();
        CanStageFromDiff = false;
        StagingDisabledReason = "History diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(DiffFooterText));
        OnPropertyChanged(nameof(ShowCommitDetailsDock));

        if (_allHistoryCommits.Count > 0)
        {
            ApplyHistoryFilter(preserveSelection: true);
            RestoreHistoryPresentationFromCache();
            _ = SoftRefreshHistoryAsync();
            return;
        }

        SelectedFile = null;
        _selectedFiles.Clear();
        SelectedFileCount = 0;
        DiffRows.Clear();
        _currentDiff = null;
        ClearImagePreview();
        SelectedCommit = null;
        _allHistoryFiles.Clear();
        HistoryFiles.Clear();
        DiffEmptyMessage = "Select a commit";
        DiffOverlayMessage = null;
        _ = LoadHistoryAsync(reset: true);
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync()
    {
        if (!HasMoreHistory || IsHistoryLoading || IsHistoryRefreshing) return;
        await LoadHistoryAsync(reset: false);
    }

    /// <summary>
    /// Re-shows the cached commit list / last selection without clearing, then loads the
    /// selected history diff from the warm store when possible.
    /// </summary>
    private void RestoreHistoryPresentationFromCache()
    {
        if (SelectedCommit is null)
        {
            SelectedFile = null;
            _selectedFiles.Clear();
            SelectedFileCount = 0;
            DiffRows.Clear();
            _currentDiff = null;
            ClearImagePreview();
            DiffEmptyMessage = "Select a commit";
            DiffOverlayMessage = null;
            OnPropertyChanged(nameof(DiffFooterText));
            SelectionSyncRequested?.Invoke();
            return;
        }

        DiffEmptyMessage = _allHistoryFiles.Count == 0
            ? "Select a file to view its diff"
            : DiffEmptyMessage;
        DiffOverlayMessage = null;

        if (_allHistoryFiles.Count == 0)
        {
            SelectedFile = null;
            _selectedFiles.Clear();
            SelectedFileCount = 0;
            DiffRows.Clear();
            _currentDiff = null;
            ClearImagePreview();
            OnPropertyChanged(nameof(DiffFooterText));
            SelectionSyncRequested?.Invoke();
            // Files were not cached (e.g. only commits loaded); fetch for the selected commit.
            _ = SelectCommitAsync(SelectedCommit);
            return;
        }

        var previousPath = _cachedHistorySelectedPath;
        _cachedHistorySelectedPath = null;

        _suppressSelectionSync = true;
        try
        {
            HistoryFiles.Clear();
            foreach (var f in _allHistoryFiles.Where(MatchesHistoryFileFilter))
                HistoryFiles.Add(f);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        if (HistoryFiles.Count == 0)
        {
            SelectedFile = null;
            _selectedFiles.Clear();
            SelectedFileCount = 0;
            DiffRows.Clear();
            _currentDiff = null;
            ClearImagePreview();
            DiffEmptyMessage = "Select a file to view its diff";
            OnPropertyChanged(nameof(DiffFooterText));
            SelectionSyncRequested?.Invoke();
            return;
        }

        var match = previousPath is not null
            ? HistoryFiles.FirstOrDefault(f =>
                string.Equals(f.Path.Value, previousPath, StringComparison.Ordinal))
            : null;
        match ??= HistoryFiles[0];
        SetFileSelection([match]);
        SelectionSyncRequested?.Invoke();
    }

    public void SelectCommit(CommitInfo? commit) => _ = SelectCommitAsync(commit);

    private async Task SelectCommitAsync(CommitInfo? commit)
    {
        if (!IsHistoryMode) return;

        _commitFilesCts?.Cancel();
        _commitFilesCts = new CancellationTokenSource();
        var ct = _commitFilesCts.Token;

        SelectedCommit = commit;
        SelectedFile = null;
        _selectedFiles.Clear();
        SelectedFileCount = 0;
        _allHistoryFiles.Clear();
        HistoryFiles.Clear();
        DiffRows.Clear();
        _currentDiff = null;
        ClearImagePreview();
        CanStageFromDiff = false;
        StagingDisabledReason = "History diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));

        if (commit is null || _repoPath is null)
        {
            DiffEmptyMessage = "Select a commit";
            DiffOverlayMessage = null;
            OnPropertyChanged(nameof(DiffFooterText));
            return;
        }

        DiffEmptyMessage = "Select a file to view its diff";
        DiffOverlayMessage = null;
        OnPropertyChanged(nameof(DiffFooterText));

        try
        {
            var files = await _history.GetCommitFilesAsync(_repoPath, commit.Oid, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsHistoryMode
                || SelectedCommit is null
                || !string.Equals(SelectedCommit.Oid, commit.Oid, StringComparison.Ordinal))
            {
                return;
            }

            _allHistoryFiles.Clear();
            foreach (var (path, kind) in files)
                _allHistoryFiles.Add(new FileItemViewModel(path, kind, isStagedList: false));

            ApplyHistoryFileFilter(autoSelectFirst: true);
            if (_allHistoryFiles.Count > 0)
                ScheduleHistoryPrefetch(commit.Oid, _allHistoryFiles.ToList());
        }
        catch (OperationCanceledException)
        {
            // Leaving History or selecting another commit cancelled this load.
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load commit files: {ex.Message}");
        }
    }

    private void ApplyHistoryFileFilter(bool autoSelectFirst = false)
    {
        if (!IsHistoryMode) return;

        var previousPath = _selectedFiles.FirstOrDefault()?.Path.Value
                           ?? SelectedFile?.Path.Value;

        _suppressSelectionSync = true;
        try
        {
            HistoryFiles.Clear();
            foreach (var f in _allHistoryFiles.Where(MatchesHistoryFileFilter))
                HistoryFiles.Add(f);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        if (HistoryFiles.Count == 0)
        {
            if (_selectedFiles.Count > 0 || SelectedFile is not null)
            {
                _selectedFiles.Clear();
                SelectedFileCount = 0;
                SelectedFile = null;
                DiffRows.Clear();
                _currentDiff = null;
                ClearImagePreview();
                DiffEmptyMessage = SelectedCommit is null
                    ? "Select a commit"
                    : "Select a file to view its diff";
                DiffOverlayMessage = null;
                OnPropertyChanged(nameof(DiffFooterText));
                SelectionSyncRequested?.Invoke();
            }
            return;
        }

        FileItemViewModel? match = null;
        if (!autoSelectFirst && previousPath is not null)
        {
            match = HistoryFiles.FirstOrDefault(f =>
                string.Equals(f.Path.Value, previousPath, StringComparison.Ordinal));
        }

        match ??= HistoryFiles[0];

        if (_selectedFiles.Count == 1
            && ReferenceEquals(_selectedFiles[0], match)
            && ReferenceEquals(SelectedFile, match))
        {
            SelectionSyncRequested?.Invoke();
            return;
        }

        SetFileSelection([match]);
        SelectionSyncRequested?.Invoke();
    }

    private async Task LoadHistoryAsync(bool reset)
    {
        if (_repoPath is null) return;

        _historyCts?.Cancel();
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;

        IsHistoryLoading = true;
        IsHistoryRefreshing = false;
        try
        {
            if (reset)
            {
                _allHistoryCommits.Clear();
                HistoryCommits.Clear();
                HasMoreHistory = false;
            }

            var skip = _allHistoryCommits.Count;
            var page = await _history.ListCommitsAsync(_repoPath, skip, HistoryPageSize, ct);
            ct.ThrowIfCancellationRequested();

            foreach (var c in page)
                _allHistoryCommits.Add(c);

            HasMoreHistory = page.Count >= HistoryPageSize;
            ApplyHistoryFilter(preserveSelection: !reset);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load history: {ex.Message}");
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    /// <summary>
    /// Background refresh of the first history page without clearing the painted list.
    /// Extra "Load more" pages are dropped when the new first page arrives.
    /// </summary>
    private async Task SoftRefreshHistoryAsync()
    {
        if (_repoPath is null || _allHistoryCommits.Count == 0) return;

        _historyCts?.Cancel();
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;

        IsHistoryRefreshing = true;
        try
        {
            var page = await _history.ListCommitsAsync(_repoPath, skip: 0, HistoryPageSize, ct);
            ct.ThrowIfCancellationRequested();

            _allHistoryCommits.Clear();
            foreach (var c in page)
                _allHistoryCommits.Add(c);

            HasMoreHistory = page.Count >= HistoryPageSize;
            ApplyHistoryFilter(preserveSelection: true);

            if (SelectedCommit is null && IsHistoryMode)
            {
                _allHistoryFiles.Clear();
                HistoryFiles.Clear();
                SelectedFile = null;
                _selectedFiles.Clear();
                SelectedFileCount = 0;
                DiffRows.Clear();
                _currentDiff = null;
                ClearImagePreview();
                DiffEmptyMessage = "Select a commit";
                DiffOverlayMessage = null;
                OnPropertyChanged(nameof(DiffFooterText));
                SelectionSyncRequested?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // ignored — keep painted cache
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to refresh history: {ex.Message}");
        }
        finally
        {
            IsHistoryRefreshing = false;
        }
    }

    private void ApplyHistoryFilter(bool preserveSelection = true)
    {
        var selectedOid = SelectedCommit?.Oid;
        var query = HistorySearchText.Trim();

        HistoryCommits.Clear();
        foreach (var commit in _allHistoryCommits)
        {
            if (MatchesHistorySearch(commit, query))
                HistoryCommits.Add(commit);
        }

        if (!preserveSelection || selectedOid is null)
            return;

        var stillVisible = HistoryCommits.FirstOrDefault(c =>
            string.Equals(c.Oid, selectedOid, StringComparison.Ordinal));
        if (stillVisible is null && SelectedCommit is not null)
        {
            // Keep details if filtered out? Plan: keep SelectedCommit if still visible.
            // If not visible, clear selection.
            SelectedCommit = null;
            _allHistoryFiles.Clear();
            HistoryFiles.Clear();
            DiffRows.Clear();
            DiffEmptyMessage = "Select a commit";
        }
        else if (stillVisible is not null && !ReferenceEquals(SelectedCommit, stillVisible))
        {
            SelectedCommit = stillVisible;
        }
    }

    private static bool MatchesHistorySearch(CommitInfo commit, string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;

        return commit.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
               || commit.AuthorName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || commit.AuthorEmail.Contains(query, StringComparison.OrdinalIgnoreCase)
               || commit.ShortOid.Contains(query, StringComparison.OrdinalIgnoreCase)
               || commit.Oid.Contains(query, StringComparison.OrdinalIgnoreCase)
               || commit.Body.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearHistoryState()
    {
        _historyCts?.Cancel();
        CancelCommitFilesLoad();
        _allHistoryCommits.Clear();
        HistoryCommits.Clear();
        _allHistoryFiles.Clear();
        HistoryFiles.Clear();
        SelectedCommit = null;
        _cachedHistorySelectedPath = null;
        HistorySearchText = "";
        HistoryFileFilter = "";
        HasMoreHistory = false;
        IsHistoryLoading = false;
        IsHistoryRefreshing = false;
    }

    private void CancelCommitFilesLoad()
    {
        _commitFilesCts?.Cancel();
        _commitFilesCts = null;
    }

    public static string FormatCommitDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
            return $"Today at {local:HH:mm}";
        if (local.Date == now.Date.AddDays(-1))
            return $"Yesterday at {local:HH:mm}";
        if (local.Year == now.Year)
            return local.ToString("d MMM yyyy");
        return local.ToString("d MMM yyyy");
    }

    [RelayCommand]
    private async Task SelectStashAsync(StashInfo? stash)
    {
        if (stash is null || _repoPath is null) return;
        if (IsHistoryMode)
        {
            _cachedHistorySelectedPath = SelectedFile?.Path.Value
                                         ?? _selectedFiles.FirstOrDefault()?.Path.Value;
            CancelCommitFilesLoad();
        }

        WorkspaceMode = WorkspaceMode.Stash;
        SelectedStash = stash;
        // Keep history commits / SelectedCommit / HistoryFiles cached for instant revisit.
        SelectedFile = null;
        _selectedFiles.Clear();
        SelectedFileCount = 0;
        DiffRows.Clear();
        _currentDiff = null;
        ClearImagePreview();
        DiffEmptyMessage = "Select a file to view its diff";
        DiffOverlayMessage = null;
        CanStageFromDiff = false;
        StagingDisabledReason = "Stash diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(ShowCommitDetailsDock));

        try
        {
            var files = await _stash.GetStashFilesAsync(_repoPath, stash.Index);
            StashFiles.Clear();
            foreach (var (path, kind) in files.Where(f =>
                         string.IsNullOrWhiteSpace(FileFilter)
                         || f.Path.Value.Contains(FileFilter, StringComparison.OrdinalIgnoreCase)
                         || f.Path.Name.Contains(FileFilter, StringComparison.OrdinalIgnoreCase)))
            {
                StashFiles.Add(new FileItemViewModel(path, kind, isStagedList: false));
            }

            if (StashFiles.Count > 0)
                ScheduleStashPrefetch(stash.Index, StashFiles.ToList());
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load stash: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ApplyStashAsync(StashInfo? stash)
    {
        stash ??= SelectedStash;
        if (_repoPath is null || stash is null) return;
        try
        {
            await _stash.ApplyStashAsync(_repoPath, stash.Index);
            _notifications.Info($"Applied {stash.Ref}");
            SelectFileStatus();
            await RefreshAsync();
            await LoadStashesAsync();
        }
        catch (Exception ex)
        {
            _notifications.Error($"Apply stash failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteStashAsync(StashInfo? stash)
    {
        stash ??= SelectedStash;
        if (_repoPath is null || stash is null) return;

        var confirmed = await _confirm.ConfirmAsync(
            "Delete stash",
            $"Permanently delete {stash.Ref}?\n\n{stash.DisplayTitle}",
            "Delete");
        if (!confirmed) return;

        try
        {
            var deletingSelected = SelectedStash?.Index == stash.Index;
            await _stash.DropStashAsync(_repoPath, stash.Index);
            _notifications.Info($"Deleted {stash.Ref}");
            if (deletingSelected)
            {
                SelectedStash = null;
                StashFiles.Clear();
                DiffRows.Clear();
                _currentDiff = null;
                ClearImagePreview();
                SelectFileStatus();
            }

            await LoadStashesAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _notifications.Error($"Delete stash failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StashAllChangesAsync()
    {
        if (_repoPath is null) return;

        var choice = await _stashDialog.ShowAsync();
        if (choice is null) return;

        if (choice.Action == StashDialogAction.Pop)
        {
            if (Stashes.Count == 0)
            {
                _notifications.Info("No stashes to pop.");
                return;
            }

            IsStashing = true;
            try
            {
                await _stash.StashPopAsync(_repoPath);
                SelectFileStatus();
                await RefreshAsync();
                await LoadStashesAsync();
                _notifications.Info("Stash popped.");
            }
            catch (Exception ex)
            {
                _notifications.Error($"Stash pop failed: {ex.Message}");
            }
            finally
            {
                IsStashing = false;
            }

            return;
        }

        if (WorkingCopyChangeCount == 0)
        {
            _notifications.Info("No local changes to stash.");
            return;
        }

        IsStashing = true;
        try
        {
            await _stash.StashPushAsync(_repoPath, choice.Message, choice.IncludeUntracked);
            SelectFileStatus();
            await RefreshAsync();
            await LoadStashesAsync();
            _notifications.Info("Changes stashed.");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Stash failed: {ex.Message}");
        }
        finally
        {
            IsStashing = false;
        }
    }

    private string AbsolutePathFor(FileItemViewModel file) =>
        RepositoryPathResolver.ResolveUnderRoot(_repoPath!, file.Path);

    partial void OnViewModeChanged(DiffViewMode value)
    {
        if (_currentDiff is null) return;
        // Instant switch: recompute layout only — zero git, zero tokenize
        ProjectRows(_currentDiff);
    }

    partial void OnIsCombinedReviewModeChanged(bool value)
    {
        _warmStore.InvalidateAll();
        _ = LoadDiffForSelectionAsync(SelectedFile);
        ScheduleFileStatusPrefetch();
    }

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

        _expandedCollapses.Clear();
        SelectedAddedLines = 0;
        SelectedRemovedLines = 0;
        ClearImagePreview();
        if (file is null || _repoPath is null)
        {
            DiffRows.Clear();
            _currentDiff = null;
            ClearDiffCacheState();
            DiffEmptyMessage = IsHistoryMode
                ? SelectedCommit is null ? "Select a commit" : "Select a file to view its diff"
                : "Select a file to view its diff";
            OnPropertyChanged(nameof(DiffFooterText));
            return;
        }

        if (IsStashMode)
        {
            await LoadStashDiffAsync(file, ct);
            return;
        }

        if (IsHistoryMode)
        {
            await LoadCommitDiffAsync(file, ct);
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

        var options = BuildDiffOptions();
        var key = FileStatusWarmKey(file.Path, target, options);

        try
        {
            var sw = Stopwatch.StartNew();
            FileDiff diff;
            if (file.Kind == ChangeKind.Untracked)
            {
                DiffRows.Clear();
                _currentDiff = null;
                ClearDiffCacheState();
                IsLoadingDiff = true;
                IsDiffRefreshing = false;
                var fullPath = RepositoryPathResolver.ResolveUnderRoot(_repoPath, file.Path);
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

                ct.ThrowIfCancellationRequested();
                await PresentDiffAsync(file, diff, target, ct);
            }
            else
            {
                await LoadTrackedDiffWithSwrAsync(
                    file,
                    key,
                    target,
                    force: false,
                    factory: token => _diffService.GetDiffAsync(_repoPath, file.Path, target.AsWorkingCopy(), options, token),
                    ct);
            }

            CodeReviewrMeters.DiffGenerationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (DiffTooLargeException ex)
        {
            SelectedAddedLines = 0;
            SelectedRemovedLines = 0;
            ClearImagePreview();
            DiffRows.Clear();
            _currentDiff = null;
            ClearDiffCacheState();
            DiffEmptyMessage = ex.Message;
            _notifications.Error(ex.Message);
        }
        catch (Exception ex)
        {
            SelectedAddedLines = 0;
            SelectedRemovedLines = 0;
            ClearImagePreview();
            DiffRows.Clear();
            _currentDiff = null;
            ClearDiffCacheState();
            _notifications.Error($"Diff failed: {ex.Message}", () => _ = LoadDiffForSelectionAsync(file));
        }
        finally
        {
            IsLoadingDiff = false;
            IsDiffRefreshing = false;
            UpdateDiffCacheState(key);
            UpdateFileCacheIndicators();
            OnPropertyChanged(nameof(DiffFooterText));
        }
    }

    /// <summary>
    /// Stale-while-revalidate load: paint a warm (possibly stale) hit immediately, then refresh in
    /// the background when needed. Only clears the viewer when there is no usable cache — including
    /// keeping a same-path painted (or alternate-target warm) diff across stage/unstage target flips.
    /// </summary>
    private async Task LoadTrackedDiffWithSwrAsync(
        FileItemViewModel file,
        DiffWarmKey key,
        DiffTarget target,
        bool force,
        Func<CancellationToken, Task<FileDiff>> factory,
        CancellationToken ct)
    {
        DiffWarmEntry? entry = null;
        var hasWarmHit = _warmStore.TryGetCompleted(key, out entry) && entry is not null;
        var needsRefresh = force || !hasWarmHit || entry!.IsStale;

        if (hasWarmHit)
        {
            IsLoadingDiff = false;
            ApplyDiffCacheState(entry!);
            await PresentDiffAsync(file, entry!.Diff, target, ct);
            if (!needsRefresh)
                return;

            IsDiffRefreshing = true;
        }
        else if (TryGetAlternateTargetWarmEntry(key, out var altEntry) && altEntry is not null)
        {
            // Stage/unstage flips DiffTarget; reuse the previous target's cached diff as a stand-in.
            IsLoadingDiff = false;
            IsDiffRefreshing = true;
            ApplyDiffCacheState(altEntry with { IsStale = true });
            await PresentDiffAsync(file, altEntry.Diff with { Scope = target.AsWorkingCopy() }, target, ct);
        }
        else if (HasPaintedDiffForPath(file.Path.Value))
        {
            // Keep whatever is already on screen for this path until the new target arrives.
            IsLoadingDiff = false;
            IsDiffRefreshing = true;
            if (_currentDiff is not null && _currentDiff.Scope.WorkingCopyTargetOrNull() != target)
            {
                _currentDiff = _currentDiff with { Scope = target.AsWorkingCopy() };
                OnPropertyChanged(nameof(CanStageLines));
                OnPropertyChanged(nameof(CanUnstageLines));
                OnPropertyChanged(nameof(CanDiscardLines));
            }

            if (_diffCacheCompletedAt is { } at)
                DiffCacheAgeText = FormatCacheAge(at);
            HasDiffCache = DiffRows.Count > 0;
        }
        else
        {
            DiffRows.Clear();
            _currentDiff = null;
            ClearDiffCacheState();
            IsLoadingDiff = true;
            IsDiffRefreshing = false;
        }

        var loadTask = _warmStore.GetOrStart(key, factory, force);
        var diff = await loadTask.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        await PresentDiffAsync(file, diff, target, ct);
        UpdateDiffCacheState(key);
    }

    private bool HasPaintedDiffForPath(string path)
    {
        if (DiffRows.Count == 0 || _currentDiff is null)
            return false;

        return string.Equals(_currentDiff.NewPath.Value, path, StringComparison.Ordinal)
               || string.Equals(_currentDiff.OldPath.Value, path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Looks up a completed warm entry for the same path/scope/options under an alternate
    /// <see cref="DiffTarget"/> (used when stage/unstage flips IndexToWorktree ↔ HeadToIndex).
    /// </summary>
    private bool TryGetAlternateTargetWarmEntry(DiffWarmKey key, out DiffWarmEntry? entry)
    {
        foreach (var alt in AlternateDiffScopes(key.DiffScope))
        {
            var altKey = new DiffWarmKey(key.Scope, key.Path, alt, key.Options);
            if (_warmStore.TryGetCompleted(altKey, out entry) && entry is not null)
                return true;
        }

        entry = null;
        return false;
    }

    private static IEnumerable<DiffScope> AlternateDiffScopes(DiffScope scope)
    {
        if (scope is not DiffScope.WorkingCopy wc)
            yield break;

        foreach (var alt in AlternateDiffTargets(wc.Target))
            yield return alt.AsWorkingCopy();
    }

    private static IEnumerable<DiffTarget> AlternateDiffTargets(DiffTarget target) =>
        target switch
        {
            DiffTarget.IndexToWorktree => [DiffTarget.HeadToIndex, DiffTarget.HeadToWorktree],
            DiffTarget.HeadToIndex => [DiffTarget.IndexToWorktree, DiffTarget.HeadToWorktree],
            DiffTarget.HeadToWorktree => [DiffTarget.IndexToWorktree, DiffTarget.HeadToIndex],
            _ => [],
        };

    [RelayCommand]
    private async Task ForceRefreshDiffAsync()
    {
        if (SelectedFile is null || _repoPath is null) return;
        if (SelectedFile.Kind == ChangeKind.Untracked)
        {
            await LoadDiffForSelectionAsync(SelectedFile);
            return;
        }

        _diffCts?.Cancel();
        _diffCts = new CancellationTokenSource();
        var ct = _diffCts.Token;
        var file = SelectedFile;

        try
        {
            if (IsStashMode && SelectedStash is { } stash)
            {
                var options = BuildDiffOptions();
                var key = StashWarmKey(stash.Index, file.Path, options);
                await LoadTrackedDiffWithSwrAsync(
                    file,
                    key,
                    DiffTarget.HeadToWorktree,
                    force: true,
                    factory: token => LoadStashFileDiffAsync(
                        _repoPath, stash.Index, file.Path, file.Kind, options, token),
                    ct);
                return;
            }

            if (IsHistoryMode && SelectedCommit is { } commit)
            {
                var options = BuildDiffOptions();
                var key = HistoryWarmKey(commit.Oid, file.Path, options);
                await LoadTrackedDiffWithSwrAsync(
                    file,
                    key,
                    DiffTarget.HeadToWorktree,
                    force: true,
                    factory: token => LoadHistoryFileDiffAsync(
                        _repoPath, commit.Oid, file.Path, file.Kind, options, token),
                    ct);
                return;
            }

            var target = IsCombinedReviewMode
                ? DiffTarget.HeadToWorktree
                : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
            var fsOptions = BuildDiffOptions();
            var fsKey = FileStatusWarmKey(file.Path, target, fsOptions);
            await LoadTrackedDiffWithSwrAsync(
                file,
                fsKey,
                target,
                force: true,
                factory: token => _diffService.GetDiffAsync(_repoPath, file.Path, target.AsWorkingCopy(), fsOptions, token),
                ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Error($"Diff refresh failed: {ex.Message}", () => _ = ForceRefreshDiffAsync());
        }
        finally
        {
            IsLoadingDiff = false;
            IsDiffRefreshing = false;
            UpdateFileCacheIndicators();
            OnPropertyChanged(nameof(DiffFooterText));
        }
    }

    private async Task RevalidateSelectedDiffAfterStatusAsync()
    {
        if (!IsFileStatusMode || SelectedFile is null)
            return;

        if (!IsPathInWorkingLists(SelectedFile.Path.Value, SelectedFile.IsStagedList))
        {
            _warmStore.InvalidatePath(SelectedFile.Path.Value);
            DiffRows.Clear();
            _currentDiff = null;
            ClearDiffCacheState();
            ClearImagePreview();
            DiffEmptyMessage = "Select a file to view its diff";
            SelectedAddedLines = 0;
            SelectedRemovedLines = 0;
            IsLoadingDiff = false;
            IsDiffRefreshing = false;
            OnPropertyChanged(nameof(DiffFooterText));
            return;
        }

        await LoadDiffForSelectionAsync(SelectedFile);
    }

    private bool IsPathInWorkingLists(string path, bool preferStaged)
    {
        bool Match(FileItemViewModel f) =>
            string.Equals(f.Path.Value, path, StringComparison.Ordinal);

        if (preferStaged && _allStaged.Any(Match)) return true;
        if (!preferStaged && _allUnstaged.Any(Match)) return true;
        if (_allStaged.Any(Match) || _allUnstaged.Any(Match) || _allConflicted.Any(Match))
            return true;
        return false;
    }

    private void ClearDiffCacheState()
    {
        _diffCacheCompletedAt = null;
        HasDiffCache = false;
        DiffCacheAgeText = null;
    }

    private void ApplyDiffCacheState(DiffWarmEntry entry)
    {
        _diffCacheCompletedAt = entry.CompletedAt;
        HasDiffCache = true;
        DiffCacheAgeText = FormatCacheAge(entry.CompletedAt);
    }

    private void UpdateDiffCacheState(DiffWarmKey key)
    {
        if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
            ApplyDiffCacheState(entry);
        else
            ClearDiffCacheState();
    }

    private static string FormatCacheAge(DateTimeOffset completedAt)
    {
        var ago = DateTimeOffset.UtcNow - completedAt;
        if (ago.TotalSeconds < 5) return "Cached just now";
        if (ago.TotalMinutes < 1) return $"Cached {(int)ago.TotalSeconds}s ago";
        if (ago.TotalHours < 1) return $"Cached {(int)ago.TotalMinutes}m ago";
        if (ago.TotalDays < 1) return $"Cached {(int)ago.TotalHours}h ago";
        return $"Cached {(int)ago.TotalDays}d ago";
    }

    private void UpdateFileCacheIndicators()
    {
        var options = BuildDiffOptions();

        void UpdateFs(FileItemViewModel file)
        {
            if (file.Kind == ChangeKind.Untracked)
            {
                file.HasCachedDiff = false;
                file.IsDiffStale = false;
                return;
            }

            var target = IsCombinedReviewMode
                ? DiffTarget.HeadToWorktree
                : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
            var key = FileStatusWarmKey(file.Path, target, options);
            if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
            {
                file.HasCachedDiff = true;
                file.IsDiffStale = entry.IsStale;
            }
            else
            {
                file.HasCachedDiff = false;
                file.IsDiffStale = false;
            }
        }

        foreach (var f in _allStaged) UpdateFs(f);
        foreach (var f in _allUnstaged) UpdateFs(f);
        foreach (var f in _allConflicted) UpdateFs(f);

        if (SelectedStash is { } stash)
        {
            foreach (var file in StashFiles)
            {
                var key = StashWarmKey(stash.Index, file.Path, options);
                if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
                {
                    file.HasCachedDiff = true;
                    file.IsDiffStale = entry.IsStale;
                }
                else
                {
                    file.HasCachedDiff = false;
                    file.IsDiffStale = false;
                }
            }
        }

        if (SelectedCommit is { } commit)
        {
            foreach (var file in _allHistoryFiles)
            {
                var key = HistoryWarmKey(commit.Oid, file.Path, options);
                if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry) && entry is not null)
                {
                    file.HasCachedDiff = true;
                    file.IsDiffStale = entry.IsStale;
                }
                else
                {
                    file.HasCachedDiff = false;
                    file.IsDiffStale = false;
                }
            }
        }
    }

    private async Task PresentDiffAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct)
    {
        _currentDiff = ApplyIntraLine(diff);
        UpdateDiffStats(_currentDiff);

        if (IsImagePath(file.Path.Value))
        {
            ClearSyntaxTokens();
            await LoadImagePreviewAsync(file, _currentDiff, target, ct);
            DiffRows.Clear();
            DiffEmptyMessage = "";
        }
        else if (_currentDiff.IsBinary)
        {
            ClearSyntaxTokens();
            DiffRows.Clear();
            DiffEmptyMessage = "Binary file";
            IsImagePreview = false;
        }
        else
        {
            DiffEmptyMessage = "Select a file to view its diff";
            ProjectRows(_currentDiff);
            // Tokenise once per selected FileDiff; view-mode switches reuse these tokens.
            await LoadSyntaxTokensAsync(file, _currentDiff, target, ct);
        }

        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));
    }

    private void ClearSyntaxTokens()
    {
        LeftSyntaxTokens = null;
        RightSyntaxTokens = null;
    }

    private async Task LoadSyntaxTokensAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct)
    {
        if (_syntaxTokens is null || _repoPath is null)
        {
            ClearSyntaxTokens();
            return;
        }

        try
        {
            var leftText = await ReadSideTextAsync(diff.OldContent, file, target, sideIsNew: false, ct)
                .ConfigureAwait(false);
            var rightText = await ReadSideTextAsync(diff.NewContent, file, target, sideIsNew: true, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            FileSyntaxTokens? left = null;
            FileSyntaxTokens? right = null;
            if (leftText is not null)
            {
                left = await _syntaxTokens.TokeniseAsync(diff.OldContent, file.Path, leftText, ct)
                    .ConfigureAwait(false);
            }

            if (rightText is not null)
            {
                right = await _syntaxTokens.TokeniseAsync(diff.NewContent, file.Path, rightText, ct)
                    .ConfigureAwait(false);
            }

            await InvokeOnUiAsync(() =>
            {
                LeftSyntaxTokens = left;
                RightSyntaxTokens = right;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await InvokeOnUiAsync(ClearSyntaxTokens);
        }
    }

    private async Task<string?> ReadSideTextAsync(
        ContentId content,
        FileItemViewModel file,
        DiffTarget target,
        bool sideIsNew,
        CancellationToken ct)
    {
        if (_repoPath is null) return null;

        if (sideIsNew
            && (target is DiffTarget.IndexToWorktree or DiffTarget.HeadToWorktree
                || file.Kind == ChangeKind.Untracked))
        {
            var worktreePath = RepositoryPathResolver.ResolveUnderRoot(_repoPath, file.Path);
            if (System.IO.File.Exists(worktreePath))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(worktreePath, ct).ConfigureAwait(false);
                return DecodeUtf8(bytes);
            }
        }

        if (content.IsEmpty) return null;
        var blob = await _objects.ReadBlobAsync(_repoPath, content, ct).ConfigureAwait(false);
        return DecodeUtf8(blob);
    }

    private static string? DecodeUtf8(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        // Skip UTF-8 BOM if present.
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static DiffWarmKey FileStatusWarmKey(FilePath path, DiffTarget target, DiffOptions options) =>
        new("fs", path.Value, target.AsWorkingCopy(), options);

    private static DiffWarmKey HistoryWarmKey(string oid, FilePath path, DiffOptions options) =>
        new($"hist:{oid}", path.Value, DiffTarget.HeadToWorktree.AsWorkingCopy(), options);

    private static DiffWarmKey StashWarmKey(int index, FilePath path, DiffOptions options) =>
        new($"stash:{index}", path.Value, DiffTarget.HeadToWorktree.AsWorkingCopy(), options);

    private void ScheduleFileStatusPrefetch()
    {
        if (_repoPath is null || !IsFileStatusMode) return;
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;
        _ = PrefetchFileStatusDiffsAsync(ct);
    }

    private async Task PrefetchFileStatusDiffsAsync(CancellationToken ct)
    {
        if (_repoPath is null) return;

        try
        {
            var options = BuildDiffOptions();
            var work = BuildFileStatusPrefetchOrder();
            var pending = new List<Task>();
            foreach (var (path, target, kind) in work)
            {
                if (ct.IsCancellationRequested) break;
                if (kind == ChangeKind.Untracked) continue;

                var key = FileStatusWarmKey(path, target, options);
                if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry)
                    && entry is { IsStale: false })
                    continue;

                var repoPath = _repoPath;
                var filePath = path;
                var diffTarget = target;
                pending.Add(_warmStore.GetOrStart(
                    key,
                    token => _diffService.GetDiffAsync(repoPath, filePath, diffTarget.AsWorkingCopy(), options, token)));
            }

            if (pending.Count > 0)
            {
                try { await Task.WhenAll(pending).WaitAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* individual failures are fine */ }
            }

            await InvokeOnUiAsync(UpdateFileCacheIndicators);
        }
        catch (OperationCanceledException)
        {
            // Prefetch superseded.
        }
    }

    private List<(FilePath Path, DiffTarget Target, ChangeKind Kind)> BuildFileStatusPrefetchOrder()
    {
        var result = new List<(FilePath, DiffTarget, ChangeKind)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(FileItemViewModel file)
        {
            var target = IsCombinedReviewMode
                ? DiffTarget.HeadToWorktree
                : file.IsStagedList ? DiffTarget.HeadToIndex : DiffTarget.IndexToWorktree;
            var key = $"{file.Path.Value}|{(int)target}|{file.IsStagedList}";
            if (!seen.Add(key)) return;
            result.Add((file.Path, target, file.Kind));
        }

        // Selected first, then ±1 neighbors in the visible list, then the rest.
        var visible = IsCombinedReviewMode
            ? UnstagedFiles.Concat(StagedFiles).Concat(ConflictedFiles).ToList()
            : UnstagedFiles.Concat(StagedFiles).Concat(ConflictedFiles).ToList();

        if (SelectedFile is { } selected)
        {
            Add(selected);
            var idx = visible.FindIndex(f =>
                string.Equals(f.Path.Value, selected.Path.Value, StringComparison.Ordinal)
                && f.IsStagedList == selected.IsStagedList);
            if (idx >= 0)
            {
                if (idx > 0) Add(visible[idx - 1]);
                if (idx + 1 < visible.Count) Add(visible[idx + 1]);
            }
        }

        foreach (var f in visible)
            Add(f);

        return result;
    }

    private void ApplyOptimisticFileLists()
    {
        if (_lastStatus is null) return;
        RebuildFileLists(_lastStatus);
    }

    private void ScheduleHistoryPrefetch(string oid, IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null) return;
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;
        _ = PrefetchHistoryDiffsAsync(oid, files, ct);
    }

    private async Task PrefetchHistoryDiffsAsync(
        string oid,
        IReadOnlyList<FileItemViewModel> files,
        CancellationToken ct)
    {
        if (_repoPath is null) return;
        try
        {
            var options = BuildDiffOptions();
            var ordered = OrderPrefetchAroundSelection(files, HistoryFiles.ToList());
            foreach (var file in ordered)
            {
                if (ct.IsCancellationRequested) break;
                var key = HistoryWarmKey(oid, file.Path, options);
                if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry)
                    && entry is { IsStale: false })
                    continue;
                var repoPath = _repoPath;
                var path = file.Path;
                var kind = file.Kind;
                _ = _warmStore.GetOrStart(key, token => LoadHistoryFileDiffAsync(repoPath, oid, path, kind, options, token));
            }

            await Task.Yield();
            await InvokeOnUiAsync(UpdateFileCacheIndicators);
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleStashPrefetch(int stashIndex, IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null) return;
        _prefetchCts?.Cancel();
        _prefetchCts = new CancellationTokenSource();
        var ct = _prefetchCts.Token;
        _ = PrefetchStashDiffsAsync(stashIndex, files, ct);
    }

    private async Task PrefetchStashDiffsAsync(
        int stashIndex,
        IReadOnlyList<FileItemViewModel> files,
        CancellationToken ct)
    {
        if (_repoPath is null) return;
        try
        {
            var options = BuildDiffOptions();
            var ordered = OrderPrefetchAroundSelection(files, StashFiles.ToList());
            foreach (var file in ordered)
            {
                if (ct.IsCancellationRequested) break;
                var key = StashWarmKey(stashIndex, file.Path, options);
                if (_warmStore.TryGetCompleted(key, out DiffWarmEntry? entry)
                    && entry is { IsStale: false })
                    continue;
                var repoPath = _repoPath;
                var path = file.Path;
                var kind = file.Kind;
                _ = _warmStore.GetOrStart(
                    key,
                    token => LoadStashFileDiffAsync(repoPath, stashIndex, path, kind, options, token));
            }

            await Task.Yield();
            await InvokeOnUiAsync(UpdateFileCacheIndicators);
        }
        catch (OperationCanceledException) { }
    }

    private List<FileItemViewModel> OrderPrefetchAroundSelection(
        IReadOnlyList<FileItemViewModel> all,
        IReadOnlyList<FileItemViewModel> visible)
    {
        var result = new List<FileItemViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(FileItemViewModel file)
        {
            if (!seen.Add(file.Path.Value)) return;
            result.Add(file);
        }

        if (SelectedFile is { } selected)
        {
            Add(selected);
            var idx = -1;
            for (var i = 0; i < visible.Count; i++)
            {
                if (string.Equals(visible[i].Path.Value, selected.Path.Value, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                if (idx > 0) Add(visible[idx - 1]);
                if (idx + 1 < visible.Count) Add(visible[idx + 1]);
            }
        }

        foreach (var f in visible)
            Add(f);
        foreach (var f in all)
            Add(f);

        return result;
    }

    private async Task<FileDiff> LoadHistoryFileDiffAsync(
        string repoPath,
        string oid,
        FilePath path,
        ChangeKind kind,
        DiffOptions options,
        CancellationToken ct)
    {
        var rawPatch = await _history.GetCommitPatchAsync(repoPath, oid, path, options, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawPatch) && kind is ChangeKind.Added)
            return UntrackedFileDiff.Create(path, string.Empty, DiffTarget.HeadToWorktree);
        return PatchParser.Parse(rawPatch, DiffTarget.HeadToWorktree);
    }

    private async Task<FileDiff> LoadStashFileDiffAsync(
        string repoPath,
        int stashIndex,
        FilePath path,
        ChangeKind kind,
        DiffOptions options,
        CancellationToken ct)
    {
        var rawPatch = await _stash.GetStashPatchAsync(repoPath, stashIndex, path, options, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawPatch) && kind is ChangeKind.Added or ChangeKind.Untracked)
            return UntrackedFileDiff.Create(path, string.Empty, DiffTarget.HeadToWorktree);
        return PatchParser.Parse(rawPatch, DiffTarget.HeadToWorktree);
    }

    private static bool IsImagePath(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ImageExtensions.Contains(ext);
    }

    private async Task LoadImagePreviewAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct)
    {
        byte[]? afterBytes = null;
        byte[]? beforeBytes = null;

        var worktreePath = RepositoryPathResolver.ResolveUnderRoot(_repoPath!, file.Path);

        // After image: prefer worktree for worktree-facing targets; else NewContent blob.
        if (target is DiffTarget.IndexToWorktree or DiffTarget.HeadToWorktree
            || file.Kind == ChangeKind.Untracked)
        {
            if (System.IO.File.Exists(worktreePath))
                afterBytes = await System.IO.File.ReadAllBytesAsync(worktreePath, ct);
        }
        else if (!diff.NewContent.IsEmpty)
        {
            afterBytes = await _objects.ReadBlobAsync(_repoPath!, diff.NewContent, ct);
        }

        if (afterBytes is null && !diff.NewContent.IsEmpty)
            afterBytes = await _objects.ReadBlobAsync(_repoPath!, diff.NewContent, ct);

        // Before image: OldContent when present and not a pure add/untracked.
        if (file.Kind is not ChangeKind.Untracked and not ChangeKind.Added
            && !diff.OldContent.IsEmpty)
        {
            beforeBytes = await _objects.ReadBlobAsync(_repoPath!, diff.OldContent, ct);
        }

        ct.ThrowIfCancellationRequested();

        var after = DecodeBitmap(afterBytes);
        var before = DecodeBitmap(beforeBytes);
        ClearImagePreviewBitmaps();
        ImageAfter = after;
        ImageBefore = before;
        HasImageBefore = before is not null;
        IsImagePreview = after is not null || before is not null;
        IsSingleImagePreview = IsImagePreview && !HasImageBefore;
        if (!IsImagePreview)
            DiffEmptyMessage = "Unable to preview image";
    }

    private static Bitmap? DecodeBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private void ClearImagePreview()
    {
        ClearImagePreviewBitmaps();
        IsImagePreview = false;
        HasImageBefore = false;
        IsSingleImagePreview = false;
    }

    private void ClearImagePreviewBitmaps()
    {
        ImageBefore?.Dispose();
        ImageAfter?.Dispose();
        ImageBefore = null;
        ImageAfter = null;
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
        _warmStore.SoftInvalidatePath(file.Path.Value);
        ApplyOptimisticFileLists();
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
        _warmStore.SoftInvalidatePath(file.Path.Value);
        ApplyOptimisticFileLists();
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
        foreach (var path in paths)
            _warmStore.SoftInvalidatePath(path.Value);
        ApplyOptimisticFileLists();
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
        foreach (var path in paths)
            _warmStore.SoftInvalidatePath(path.Value);
        ApplyOptimisticFileLists();
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
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.IndexToWorktree }) return;
        await ApplyHunkPatchAsync(SelectedHunkIndex, stage: true);
    }

    [RelayCommand]
    private async Task UnstageHunkAsync()
    {
        if (_currentDiff is null || _repoPath is null || SelectedHunkIndex < 0) return;
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.HeadToIndex }) return;
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
            _warmStore.SoftInvalidatePath(_currentDiff.NewPath.Value);
            ProjectRowsOptimisticRemoveHunk(hunkIndex);
        }

        try
        {
            if (stage)
                await _staging.StagePatchAsync(_repoPath, patch);
            else
            {
                _warmStore.SoftInvalidatePath(_currentDiff.NewPath.Value);
                await _staging.UnstagePatchAsync(_repoPath, patch);
            }
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
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.IndexToWorktree }) return;
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
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.HeadToIndex }) return;
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
        _currentDiff?.Scope is DiffScope.WorkingCopy { Target: DiffTarget.IndexToWorktree };

    public bool CanUnstageLines =>
        _currentDiff?.Scope is DiffScope.WorkingCopy { Target: DiffTarget.HeadToIndex };

    /// <summary>True when the open diff is a worktree diff (stage and discard line/hunk ops apply).</summary>
    public bool CanDiscardLines => CanStageLines;

    /// <summary>True when at least one selected non-conflicted file can be discarded.</summary>
    public bool CanDiscardSelection => HasUnstagedSelection || HasStagedSelection;

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
        var files = _selectedFiles.Where(f => !f.IsConflicted).ToList();
        await DiscardFilesWithConfirmAsync(files);
    }

    [RelayCommand]
    private async Task DiscardFileAsync(FileItemViewModel? file)
    {
        if (file is null || file.IsConflicted) return;
        await DiscardFilesWithConfirmAsync([file]);
    }

    private async Task DiscardFilesWithConfirmAsync(IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null || files.Count == 0) return;

        // Deduplicate by path: prefer staged discard when the same path appears in both lists.
        var byPath = files
            .GroupBy(f => f.Path.Value, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(f => f.IsStagedList) ?? g.First())
            .ToList();

        var message = byPath.Count == 1
            ? $"Discard all changes in {byPath[0].Path.Name}? This cannot be undone except via Undo."
            : $"Discard changes in {byPath.Count} files? This cannot be undone except via Undo.";
        if (!await _confirm.ConfirmAsync("Discard changes", message))
            return;

        var discarded = new List<DiscardedEntry>();
        try
        {
            foreach (var file in byPath)
            {
                if (file.IsStagedList)
                    await _discard.DiscardStagedFileAsync(_repoPath, file.Path);
                else
                    await _discard.DiscardFileAsync(_repoPath, file.Path);
                var entry = _discard.RecentlyDiscarded.FirstOrDefault(e => e.Path.Equals(file.Path));
                if (entry is not null)
                    discarded.Add(entry);
            }

            var label = byPath.Count == 1
                ? $"Discarded {byPath[0].Path.Name}"
                : $"Discarded {byPath.Count} files";
            _notifications.Info(
                label,
                discarded.Count == 0 ? null : () => _ = RestoreDiscardedManyAsync(discarded),
                "Undo");

            var paths = byPath.Select(f => f.Path).ToList();
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
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.IndexToWorktree }) return;

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
        if (_currentDiff.Scope is not DiffScope.WorkingCopy { Target: DiffTarget.IndexToWorktree }) return;

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
            if (_allHistoryCommits.Count > 0)
                _ = SoftRefreshHistoryAsync();
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
        var listed = await _branches.ListBranchesAsync(_repoPath);
        await InvokeOnUiAsync(() =>
        {
            Branches.Clear();
            foreach (var b in listed.Where(b => !b.IsRemote))
                Branches.Add(b);
        });
    }

    private async Task LoadStashesAsync()
    {
        if (_repoPath is null) return;
        try
        {
            var listed = await _stash.ListStashesAsync(_repoPath);
            await InvokeOnUiAsync(() =>
            {
                var selectedIndex = SelectedStash?.Index;
                Stashes.Clear();
                foreach (var s in listed)
                    Stashes.Add(s);
                if (selectedIndex is int idx)
                    SelectedStash = Stashes.FirstOrDefault(s => s.Index == idx);
            });
        }
        catch
        {
            // Stash list failure should not block status refresh.
        }
    }

    private async Task LoadStashDiffAsync(FileItemViewModel file, CancellationToken ct)
    {
        if (_repoPath is null || SelectedStash is null) return;

        CanStageFromDiff = false;
        StagingDisabledReason = "Stash diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));

        var options = BuildDiffOptions();
        var key = StashWarmKey(SelectedStash.Index, file.Path, options);
        try
        {
            await LoadTrackedDiffWithSwrAsync(
                file,
                key,
                DiffTarget.HeadToWorktree,
                force: false,
                factory: token => LoadStashFileDiffAsync(
                    _repoPath, SelectedStash.Index, file.Path, file.Kind, options, token),
                ct);

            if (IsImagePath(file.Path.Value))
            {
                ClearImagePreview();
                IsImagePreview = false;
                DiffEmptyMessage = "Image preview is not available for stash entries yet";
            }
            else if (_currentDiff?.IsBinary == true)
            {
                DiffEmptyMessage = "Binary file";
            }
            else if (DiffRows.Count == 0)
            {
                DiffEmptyMessage = "No differences";
            }

            OnPropertyChanged(nameof(DiffFooterText));
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            DiffEmptyMessage = $"Failed to load stash diff: {ex.Message}";
            OnPropertyChanged(nameof(DiffFooterText));
        }
        finally
        {
            IsLoadingDiff = false;
            IsDiffRefreshing = false;
            UpdateDiffCacheState(key);
            UpdateFileCacheIndicators();
            UpdateDiffOverlay();
        }
    }

    private async Task LoadCommitDiffAsync(FileItemViewModel file, CancellationToken ct)
    {
        if (_repoPath is null || SelectedCommit is null) return;

        CanStageFromDiff = false;
        StagingDisabledReason = "History diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));

        var options = BuildDiffOptions();
        var key = HistoryWarmKey(SelectedCommit.Oid, file.Path, options);
        try
        {
            await LoadTrackedDiffWithSwrAsync(
                file,
                key,
                DiffTarget.HeadToWorktree,
                force: false,
                factory: token => LoadHistoryFileDiffAsync(
                    _repoPath, SelectedCommit.Oid, file.Path, file.Kind, options, token),
                ct);

            if (IsImagePath(file.Path.Value))
            {
                ClearImagePreview();
                IsImagePreview = false;
                DiffEmptyMessage = "Image preview is not available for history entries yet";
            }
            else if (_currentDiff?.IsBinary == true)
            {
                DiffEmptyMessage = "Binary file";
            }
            else if (DiffRows.Count == 0)
            {
                DiffEmptyMessage = "No differences";
            }

            OnPropertyChanged(nameof(DiffFooterText));
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            DiffEmptyMessage = $"Failed to load commit diff: {ex.Message}";
            OnPropertyChanged(nameof(DiffFooterText));
        }
        finally
        {
            IsLoadingDiff = false;
            IsDiffRefreshing = false;
            UpdateDiffCacheState(key);
            UpdateFileCacheIndicators();
            UpdateDiffOverlay();
        }
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

    [ObservableProperty] private bool _hasCachedDiff;
    [ObservableProperty] private bool _isDiffStale;
    [ObservableProperty] private int _unresolvedThreadCount;
    [ObservableProperty] private bool _isViewed;
    [ObservableProperty] private bool _isViewedPending;
    [ObservableProperty] private bool _hasCommentThreads;
    [ObservableProperty] private bool _hasStaleThreads;

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
