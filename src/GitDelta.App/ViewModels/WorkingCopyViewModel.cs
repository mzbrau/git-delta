using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.App.Collections;
using GitDelta.App.Controls;
using GitDelta.App.Services;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.AI;
using GitDelta.Core.Diagnostics;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.ViewModels;

public partial class WorkingCopyViewModel : ObservableObject, IPendingChangesReviewHost
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
    private readonly IAiCommitAssistService _commitAssist;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly WorkingCopyDiffPresenter _diff;
    private readonly WorkingCopyStatusController _status;

    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _historyCts;
    private CancellationTokenSource? _commitFilesCts;
    private CancellationTokenSource? _markdownCts;
    private string? _cachedHistorySelectedPath;
    private bool _suppressHistoryBranchReload;
    private string? _repoPath;
    private RepositoryStatus? _lastStatus;
    private readonly DiffWarmStore _warmStore;
    private readonly List<CommitInfo> _allHistoryCommits = [];
    private readonly List<FileItemViewModel> _allHistoryFiles = [];
    private const int HistoryPageSize = 300;
    private const int DiffEmptyDetailMaxNames = 20;
    private const double SlowUiInvokeMs = 50;
    private const double FileListLayoutActivityMs = 16;
    private const int PrefetchDripDelayMsMin = 0;
    private const int PrefetchDripDelayMsMax = 5000;
    private const int PrefetchIndicatorThrottleMsMin = 50;
    private const int PrefetchIndicatorThrottleMsMax = 5000;
    private const int PrefetchPriorityPathsMin = 1;
    private const int PrefetchPriorityPathsMax = 500;
    private const int PrefetchNeighborRadiusMin = 0;
    private const int PrefetchNeighborRadiusMax = 64;
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
    private readonly Dictionary<string, bool> _fileStatusExpandState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _historyExpandState = new(StringComparer.Ordinal);
    private readonly HashSet<(int HunkIndex, int LineIndexInHunk)> _expandedCollapses = [];
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
        IGitRebaseService rebase,
        IGitStashService stash,
        IGitHistoryService history,
        ISettingsStore settings,
        NotificationService notifications,
        IConfirmDialog confirm,
        IStashDialog stashDialog,
        IIntraLineDiffer intraLine,
        IFsmonitorService fsmonitor,
        IRepositoryWatcher watcher,
        PendingChangesReviewViewModel pendingReview,
        IAiCommitAssistService? commitAssist = null,
        ISyntaxTokenService? syntaxTokens = null)
    {
        _diff = new WorkingCopyDiffPresenter(this);
        _status = new WorkingCopyStatusController(this);
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
        _commitAssist = commitAssist ?? NullAiCommitAssistService.Instance;
        _fsmonitor = fsmonitor;
        _watcher = watcher;
        PendingReview = pendingReview;
        RebaseWizard = new RebaseWizardViewModel(
            branches,
            history,
            rebase,
            stash,
            confirm,
            notifications,
            () => WorkingCopyChangeCount,
            () => RefreshAsync());
        _warmStore = new DiffWarmStore(DiffWarmStore.ClampConcurrency(settings.Current.DiffPrefetchConcurrency));
        ViewMode = settings.Current.DefaultDiffMode;
        _ignoreWhitespace = settings.Current.IgnoreWhitespace;
        _contextLines = settings.Current.ContextLines > 0 ? settings.Current.ContextLines : 3;
        _fileStatusListLayout = NormalizeFileListLayout(settings.Current.FileStatusListLayout);
        _historyFileListLayout = NormalizeFileListLayout(settings.Current.HistoryFileListLayout);
        // Watcher callbacks arrive on thread-pool / FileSystemWatcher threads.
        _watcher.RefreshRequested += () =>
            Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _watcher.OfferFsmonitor += () =>
            Dispatcher.UIThread.Post(() =>
                _notifications.Info("Status is slow. Enable Git fsmonitor for this repository?",
                    () => _ = EnableFsmonitorAsync(), "Enable"));
        PendingReview.AttachHost(this);
        RebaseWizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RebaseWizardViewModel.OwnsInProgressRebase))
                OnPropertyChanged(nameof(CanUseInProgressBanner));
        };
    }

    /// <summary>PR-parity AI review + local-only comments surface for File Status (pending changes).</summary>
    public PendingChangesReviewViewModel PendingReview { get; }

    /// <summary>Interactive rebase wizard (shown as an in-app overlay).</summary>
    public RebaseWizardViewModel RebaseWizard { get; }

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

    public ResettableObservableCollection<FileItemViewModel> StagedFiles { get; } = new();
    public ResettableObservableCollection<FileItemViewModel> UnstagedFiles { get; } = new();
    public ResettableObservableCollection<FileItemViewModel> ConflictedFiles { get; } = new();
    public ObservableCollection<FileItemViewModel> StashFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> HistoryFiles { get; } = [];
    public ResettableObservableCollection<FileListEntry> StagedFileEntries { get; } = new();
    public ResettableObservableCollection<FileListEntry> UnstagedFileEntries { get; } = new();
    public ResettableObservableCollection<FileListEntry> ConflictedFileEntries { get; } = new();
    public ResettableObservableCollection<FileListEntry> StashFileEntries { get; } = new();
    public ResettableObservableCollection<FileListEntry> HistoryFileEntries { get; } = new();
    public ObservableCollection<CommitInfo> HistoryCommits { get; } = [];
    public ResettableObservableCollection<DiffRow> DiffRows { get; } = new();
    public ObservableCollection<BranchInfo> Branches { get; } = [];
    /// <summary>Local and remote branches available for the history branch filter.</summary>
    public ObservableCollection<BranchInfo> HistoryBranches { get; } = [];
    /// <summary>HistoryBranches filtered by <see cref="HistoryBranchFilter"/>.</summary>
    public ObservableCollection<BranchInfo> FilteredHistoryBranches { get; } = [];
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
    [ObservableProperty] private bool _isGeneratingCommitMessage;
    [ObservableProperty] private bool _showMagicCommitDialog;
    [ObservableProperty] private MagicCommitDialogStepKind _magicCommitDialogStep;
    [ObservableProperty] private string _magicCommitInstructions = "";
    [ObservableProperty] private bool _magicCommitStagedOnly = true;
    [ObservableProperty] private string _magicCommitProgressText = "";
    [ObservableProperty] private string _magicCommitActivityLog = "";
    [ObservableProperty] private string _magicCommitError = "";
    [ObservableProperty] private bool _isMagicCommitRunning;
    private CancellationTokenSource? _magicCommitCts;
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
    [ObservableProperty] private BranchInfo? _selectedHistoryBranch;
    [ObservableProperty] private string _historyBranchFilter = "";
    [ObservableProperty] private bool _isStashing;
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private bool _hasFileFilter;
    [ObservableProperty] private string _historyFileFilter = "";
    [ObservableProperty] private bool _hasHistoryFileFilter;
    [ObservableProperty] private FileListLayoutMode _fileStatusListLayout;
    [ObservableProperty] private FileListLayoutMode _historyFileListLayout;
    [ObservableProperty] private string _historySearchText = "";
    [ObservableProperty] private bool _hasHistorySearch;
    [ObservableProperty] private bool _isHistoryLoading;
    [ObservableProperty] private bool _isHistoryRefreshing;
    [ObservableProperty] private bool _hasMoreHistory;
    [ObservableProperty] private bool _isPushing;
    [ObservableProperty] private bool _isPulling;
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private bool _showRebaseWizard;
    [ObservableProperty] private int _selectedFileCount;
    [ObservableProperty] private bool _hasStagedSelection;
    [ObservableProperty] private bool _hasUnstagedSelection;
    [ObservableProperty] private string _diffEmptyMessage = "Select a file to view its diff";
    [ObservableProperty] private string? _diffEmptyDetail;
    [ObservableProperty] private string? _diffOverlayMessage;
    [ObservableProperty] private bool _ignoreWhitespace;
    [ObservableProperty] private int _contextLines = 3;
    [ObservableProperty] private bool _showFullFile;
    [ObservableProperty] private bool _showMarkdownPreview;
    [ObservableProperty] private string? _markdownPreviewText;
    [ObservableProperty] private bool _isImagePreview;
    [ObservableProperty] private bool _hasImageBefore;
    [ObservableProperty] private bool _isSingleImagePreview;
    [ObservableProperty] private Bitmap? _imageBefore;
    [ObservableProperty] private Bitmap? _imageAfter;
    [ObservableProperty] private FileSyntaxTokens? _leftSyntaxTokens;
    [ObservableProperty] private FileSyntaxTokens? _rightSyntaxTokens;
    private DiffTarget _currentDiffTarget = DiffTarget.IndexToWorktree;

    /// <summary>Raised before File Status entry collections are cleared so the view can drop ListBox selection first.</summary>
    public event Action? SelectionClearRequested;

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
    public string FullFileToggleTooltip => ShowFullFile ? "Diff only" : "Full file";
    public bool IsMarkdownFile => SelectedFile is not null && MarkdownPath.IsMarkdownPath(SelectedFile.Path.Value);
    public bool CanShowMarkdownPreview => IsMarkdownFile;
    public bool ShowMarkdownPreviewPane => ShowMarkdownPreview && IsMarkdownFile;
    public bool ShowDiffViewer => !IsImagePreview && !ShowMarkdownPreviewPane;
    public bool ShowDiffBrandWatermark => SelectedFile is null;
    public string MarkdownPreviewEmptyMessage =>
        SelectedFile is null ? "Select a file to view its diff"
        : MarkdownPreviewText is null ? "No new version"
        : "No markdown content";
    public bool IsUnifiedView => ViewMode == DiffViewMode.Unified;
    public bool IsSideBySideView => ViewMode == DiffViewMode.SideBySide;
    public bool IsContextLines1 => ContextLines == 1;
    public bool IsContextLines3 => ContextLines == 3;
    public bool IsContextLines5 => ContextLines == 5;
    public bool IsContextLines10 => ContextLines == 10;
    public bool IsContextLines25 => ContextLines == 25;

    public string RevealInFileManagerLabel => FileManagerReveal.Label;

    public string? SelectedFileAbsolutePath =>
        _repoPath is null || SelectedFile is null
            ? null
            : AbsolutePathFor(SelectedFile);

    public IReadOnlyList<FileItemViewModel> SelectedFilesSnapshot => _selectedFiles;

    public bool HasRepository => _repoPath is not null;

    // ---------------------------------------------------------------------
    // IPendingChangesReviewHost — lets PendingReview drive AI review + local comments
    // without WorkingCopyViewModel-specific coupling in the pending-review view model.
    // ---------------------------------------------------------------------

    FileDiff? IPendingChangesReviewHost.CurrentDiff => _currentDiff;

    string IPendingChangesReviewHost.RepositoryKey =>
        _repoPath is null ? "(no repository)" : NormalizeRepositoryKey(_repoPath);

    int IPendingChangesReviewHost.StagedCount => _allStaged.Count;

    int IPendingChangesReviewHost.UnstagedCount => _allUnstaged.Count;

    IReadOnlyList<FileItemViewModel> IPendingChangesReviewHost.PendingFiles => BuildAllPendingFiles();

    private static string NormalizeRepositoryKey(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    private List<FileItemViewModel> BuildAllPendingFiles()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FileItemViewModel>();
        foreach (var file in _allStaged)
            if (seen.Add(file.Path.Value)) result.Add(file);
        foreach (var file in _allUnstaged)
            if (seen.Add(file.Path.Value)) result.Add(file);
        foreach (var file in _allConflicted)
            if (seen.Add(file.Path.Value)) result.Add(file);
        return result;
    }

    IReadOnlyList<AiChangedFileFact> IPendingChangesReviewHost.BuildChangedFileFacts(AiReviewScope scope)
    {
        if (_lastStatus is null)
            return [];

        var entries = scope == AiReviewScope.WorkingCopyStaged
            ? _lastStatus.Staged
            : MergeStagedAndUnstagedEntries(_lastStatus);

        var statsByPath = new Dictionary<string, FileItemViewModel>(StringComparer.Ordinal);
        foreach (var file in StagedFiles.Concat(UnstagedFiles).Concat(ConflictedFiles))
            statsByPath.TryAdd(file.Path.Value, file);

        return entries
            .Select(e =>
            {
                statsByPath.TryGetValue(e.Path.Value, out var file);
                return new AiChangedFileFact(
                    e.Path.Value,
                    e.Kind.ToString(),
                    BeforeBlobOid: e.HeadOid?.Value,
                    AfterBlobOid: (scope == AiReviewScope.WorkingCopyStaged ? e.IndexOid : e.WorktreeOid ?? e.IndexOid)?.Value,
                    LinesAdded: file?.LinesAdded,
                    LinesRemoved: file?.LinesRemoved,
                    ChangePercent: file?.ChangePercent);
            })
            .ToList();
    }

    private static IEnumerable<StatusEntry> MergeStagedAndUnstagedEntries(RepositoryStatus status)
    {
        var byPath = new Dictionary<string, StatusEntry>(StringComparer.Ordinal);
        foreach (var e in status.Staged)
            byPath[e.Path.Value] = e;
        foreach (var e in status.Unstaged)
        {
            byPath[e.Path.Value] = byPath.TryGetValue(e.Path.Value, out var existing)
                ? existing with { WorktreeOid = e.WorktreeOid ?? existing.WorktreeOid, IsUnstaged = true }
                : e;
        }

        return byPath.Values;
    }

    async Task<string?> IPendingChangesReviewHost.TryGetHeadCommitShaAsync(CancellationToken ct)
    {
        if (_repoPath is null)
            return null;

        try
        {
            var commits = await _history.ListCommitsAsync(_repoPath, 0, 1, ct: ct).ConfigureAwait(false);
            return commits.Count > 0 ? commits[0].Oid : null;
        }
        catch
        {
            return null;
        }
    }

    async Task IPendingChangesReviewHost.SelectFileAsync(FilePath path)
    {
        var file = StagedFiles.Concat(UnstagedFiles).Concat(ConflictedFiles)
            .FirstOrDefault(f => string.Equals(f.Path.Value, path.Value, StringComparison.Ordinal));
        if (file is null)
        {
            // Fall back to unfiltered lists (filter may hide the path).
            file = _allStaged.Concat(_allUnstaged).Concat(_allConflicted)
                .FirstOrDefault(f => string.Equals(f.Path.Value, path.Value, StringComparison.Ordinal));
        }

        if (file is null)
            return;

        WorkspaceMode = WorkspaceMode.FileStatus;
        _skipNextSelectedFileLoad = true;
        ApplySelectionState([file], requestViewSync: true);
        await LoadDiffForSelectionAsync(file).ConfigureAwait(true);
    }

    void IPendingChangesReviewHost.ClearFileSelection()
    {
        // Only skip the next load when SelectedFile will actually change (nulling it).
        // If already null, OnSelectedFileChanged never runs and a stuck skip flag would
        // swallow the next real file selection (first click after AI briefing).
        if (SelectedFile is not null)
            _skipNextSelectedFileLoad = true;
        ApplySelectionState([], requestViewSync: true);
        DiffRows.Clear();
        _currentDiff = null;
        DiffEmptyMessage = "Pending changes context";
        DiffEmptyDetail = null;
        OnPropertyChanged(nameof(DiffFooterText));
        PendingReview.OnFileSelectionChanged(null, null);
    }

    public bool IsRemoteBusy => IsPushing || IsPulling || IsFetching || IsStashing;

    /// <summary>True when the current branch is not main/master and a repository is open.</summary>
    public bool CanRebase =>
        _repoPath is not null
        && !string.IsNullOrWhiteSpace(CurrentBranch)
        && !RebaseWizardViewModel.IsProtectedBranchName(CurrentBranch);

    public string? RebaseDisabledReason =>
        _repoPath is null ? "Open a repository to rebase."
        : RebaseWizardViewModel.IsProtectedBranchName(CurrentBranch)
            ? "Interactive rebase is not available on main or master."
            : null;

    /// <summary>
    /// When the rebase wizard owns an in-progress rebase, the main banner Abort/Continue buttons
    /// are disabled so the wizard remains the sole controller.
    /// </summary>
    public bool CanUseInProgressBanner =>
        !(ShowRebaseWizard && RebaseWizard.OwnsInProgressRebase);

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
    public int StagedFileCount => StagedFiles.Count;
    public int UnstagedFileCount => UnstagedFiles.Count;
    public bool CanCommit =>
        !IsCommitting && HasStagedFiles && !string.IsNullOrWhiteSpace(CommitMessage);
    public bool ShowCommitDock => IsFileStatusMode && HasStagedFiles;
    public bool ShowCommitDetailsDock => IsHistoryMode && SelectedCommit is not null;

    /// <summary>
    /// Cherry-pick is only useful when browsing a branch other than the checked-out one.
    /// </summary>
    public bool CanCherryPickHistoryCommit =>
        SelectedHistoryBranch is { Name: { Length: > 0 } selected }
        && (CurrentBranch is null
            || !string.Equals(selected, CurrentBranch, StringComparison.Ordinal));

    public bool ShowHistoryBranchFilterEmpty =>
        !string.IsNullOrWhiteSpace(HistoryBranchFilter) && FilteredHistoryBranches.Count == 0;

    public bool ShowStashDetailsDock => IsStashMode && SelectedStash is not null;

    public bool IsFileStatusFlatLayout => FileStatusListLayout == FileListLayoutMode.Flat;
    public bool IsFileStatusTreeLayout => FileStatusListLayout == FileListLayoutMode.Tree;
    public bool IsHistoryFlatLayout => HistoryFileListLayout == FileListLayoutMode.Flat;
    public bool IsHistoryTreeLayout => HistoryFileListLayout == FileListLayoutMode.Tree;
    public Material.Icons.MaterialIconKind FileStatusLayoutIcon =>
        FileStatusListLayout == FileListLayoutMode.Tree
            ? Material.Icons.MaterialIconKind.FileTree
            : Material.Icons.MaterialIconKind.FormatListBulleted;
    public Material.Icons.MaterialIconKind HistoryLayoutIcon =>
        HistoryFileListLayout == FileListLayoutMode.Tree
            ? Material.Icons.MaterialIconKind.FileTree
            : Material.Icons.MaterialIconKind.FormatListBulleted;

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

    partial void OnCurrentBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(CommitButtonLabel));
        OnPropertyChanged(nameof(CanRebase));
        OnPropertyChanged(nameof(RebaseDisabledReason));
        OnPropertyChanged(nameof(CanCherryPickHistoryCommit));
        OnPropertyChanged(nameof(CanAddTicketFromBranch));
        OpenRebaseWizardCommand.NotifyCanExecuteChanged();
        CherryPickCommitCommand.NotifyCanExecuteChanged();
        AddTicketFromBranchCommand.NotifyCanExecuteChanged();
        SyncSelectedHistoryBranchToCurrent();
    }

    partial void OnShowRebaseWizardChanged(bool value) =>
        OnPropertyChanged(nameof(CanUseInProgressBanner));

    partial void OnIsCommittingChanged(bool value) => NotifyCanCommitChanged();
    partial void OnCommitMessageChanged(string value) => NotifyCanCommitChanged();

    private void NotifyCanCommitChanged()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanGenerateCommitMessage));
        OnPropertyChanged(nameof(CanStartMagicCommit));
        CommitCommand.NotifyCanExecuteChanged();
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        StartMagicCommitCommand.NotifyCanExecuteChanged();
    }
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
        OnPropertyChanged(nameof(IsFileStatusContentSearchActive));
        if (IsStashMode && SelectedStash is not null)
            _ = SelectStashAsync(SelectedStash);
        else
            ApplyFileFilter();
    }

    partial void OnHistoryFileFilterChanged(string value)
    {
        HasHistoryFileFilter = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(IsHistoryContentSearchActive));
        ApplyHistoryFileFilter();
    }

    partial void OnHistorySearchTextChanged(string value)
    {
        HasHistorySearch = !string.IsNullOrWhiteSpace(value);
        ApplyHistoryFilter();
    }

    partial void OnHistoryBranchFilterChanged(string value)
    {
        RebuildFilteredHistoryBranches();
        OnPropertyChanged(nameof(ShowHistoryBranchFilterEmpty));
    }

    partial void OnSelectedHistoryBranchChanged(BranchInfo? value)
    {
        OnPropertyChanged(nameof(CanCherryPickHistoryCommit));
        CherryPickCommitCommand.NotifyCanExecuteChanged();
        if (_suppressHistoryBranchReload || !IsHistoryMode)
            return;

        _ = ReloadHistoryForSelectedBranchAsync();
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

    [RelayCommand]
    private void SelectHistoryBranch(BranchInfo? branch)
    {
        if (branch is null)
            return;
        SelectedHistoryBranch = branch;
    }

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
        NotifyContextLineSelectionChanged();
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
        OnPropertyChanged(nameof(FullFileToggleTooltip));
        _expandedCollapses.Clear();
        // Presentation-only: reproject in memory — do not wipe warm cache or re-run git.
        if (_currentDiff is not null)
            ProjectRows(_currentDiff);
    }

    public async Task OpenAsync(string path)
    {
        using var activity = GitDeltaActivity.Source.StartActivity("repository.open");
        activity?.SetTag("repository.path", path);
        var openSw = Stopwatch.StartNew();
        try
        {
            var isNewRepository = !string.Equals(_repoPath, path, StringComparison.Ordinal);
            activity?.SetTag("open.is_new_repository", isNewRepository);
            _repoPath = path;
            RepositoryPath = path;
            OnPropertyChanged(nameof(HasRepository));
            OnPropertyChanged(nameof(SelectedFileAbsolutePath));
            WorkspaceMode = WorkspaceMode.FileStatus;
            SelectedStash = null;
            SelectedCommit = null;
            ClearHistoryState();
            _watcher.WatchRepository(path);
            if (isNewRepository)
            {
                PendingReview.ResetState();
            }
            await RefreshAsync();
            activity?.SetTag("wc.total_count", _allStaged.Count + _allUnstaged.Count + _allConflicted.Count);
            await LoadBranchesAsync();
            await LoadStashesAsync();
            _ = PendingReview.RefreshLocalCommentsAsync();
            _ = PendingReview.LoadCachedAiRunAsync();
        }
        finally
        {
            GitDeltaMeters.RepositoryOpenMs.Record(openSw.Elapsed.TotalMilliseconds);
        }
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

    private int _refreshGeneration;

    [RelayCommand]
    /// <param name="clearAiReviewAfter">
    /// When true (in-app commit), clear AI triage/summary after sync so the button resets even if
    /// other files remain pending. Ordinary refreshes only clear AI when the working copy is empty.
    /// </param>
    public async Task RefreshAsync(bool clearAiReviewAfter = false)
    {
        if (_repoPath is null) return;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        await _refreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            // A newer refresh was scheduled while we waited — let that waiter do the work.
            if (generation != Volatile.Read(ref _refreshGeneration))
                return;

            if (_repoPath is null) return;
            using var activity = GitDeltaActivity.Source.StartActivity("wc.refresh");
            var sw = Stopwatch.StartNew();
            try
            {
                var previousStatus = _lastStatus;
                var status = await _statusService.GetStatusAsync(_repoPath).ConfigureAwait(true);
                if (generation != Volatile.Read(ref _refreshGeneration))
                    return;
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

                    RebuildFileListsTimed(status, "refresh");
                    StatusUpdated = true;
                }

                await InvokeOnUiAsync(ApplyStatus, "apply_status");
                SoftInvalidateChangedPaths(previousStatus, status);
                UpdateFileCacheIndicators();
                await RevalidateSelectedDiffAfterStatusAsync(previousStatus, status);
                await PendingReview.SyncReviewStateWithPendingFilesAsync(clearAiReviewAfter);
                PendingReview.UpdateFileUnresolvedCommentCounts();
                ScheduleFileStatusPrefetch();

                activity?.SetTag("wc.staged_count", _allStaged.Count);
                activity?.SetTag("wc.unstaged_count", _allUnstaged.Count);
                activity?.SetTag("wc.conflicted_count", _allConflicted.Count);
                activity?.SetTag("wc.total_count", _allStaged.Count + _allUnstaged.Count + _allConflicted.Count);
            }
            finally
            {
                GitDeltaMeters.WcRefreshMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void RebuildFileListsTimed(RepositoryStatus status, string reason) => _status.RebuildFileListsTimed(status, reason);


    private void RebuildFileLists(RepositoryStatus status) => _status.RebuildFileLists(status);


    /// <summary>
    /// Measures time from file-list rebuild until the next UI layout/render pass
    /// (Avalonia realize + measure of file-list rows).
    /// </summary>
    private void ScheduleFileListLayoutTiming() => _status.ScheduleFileListLayoutTiming();


    private Dictionary<string, AiChangeClassification> CaptureAiClassifications() => _status.CaptureAiClassifications();


    private void ApplyAiClassifications(Dictionary<string, AiChangeClassification> classifications) => _status.ApplyAiClassifications(classifications);


    private void ApplyFileFilter()
    {
        using var activity = GitDeltaActivity.Source.StartActivity("wc.filelists.filter");
        var sw = Stopwatch.StartNew();
        try
        {
            if (IsFileStatusContentSearchActive)
            {
                ScheduleFileStatusContentSearch();
                return;
            }

            _fileStatusSearchCts?.Cancel();
            _stagedSearchResults = [];
            _unstagedSearchResults = [];
            _conflictedSearchResults = [];

            var previousKeys = _selectedFiles
                .Select(f => (Path: f.Path.Value, f.IsStagedList))
                .ToList();

            _suppressSelectionSync = true;
            try
            {
                var staged = _allStaged.Where(MatchesFilter).ToList();
                var unstaged = _allUnstaged.Where(MatchesFilter).ToList();
                var conflicted = _allConflicted.Where(MatchesFilter).ToList();
                StagedFiles.Reset(staged);
                UnstagedFiles.Reset(unstaged);
                ConflictedFiles.Reset(conflicted);
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            RebuildFileStatusEntries();

            activity?.SetTag("filter.staged", StagedFiles.Count);
            activity?.SetTag("filter.unstaged", UnstagedFiles.Count);
            activity?.SetTag("filter.conflicted", ConflictedFiles.Count);

            OnPropertyChanged(nameof(HasConflictedFiles));
            OnPropertyChanged(nameof(HasStagedFiles));
            OnPropertyChanged(nameof(StagedFileCount));
            OnPropertyChanged(nameof(UnstagedFileCount));
            OnPropertyChanged(nameof(ShowCommitDock));
            NotifyCanCommitChanged();

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
        finally
        {
            GitDeltaMeters.WcFileListsFilterMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private bool MatchesFilter(FileItemViewModel file) =>
        IsFileStatusSearchMode || MatchesPathFilter(file, FileFilter);

    private bool MatchesHistoryFileFilter(FileItemViewModel file) =>
        IsHistoryFileSearchMode || MatchesPathFilter(file, HistoryFileFilter);

    private void RebuildFileStatusEntries()
    {
        if (IsFileStatusContentSearchActive)
        {
            RebuildFileStatusSearchEntries();
            return;
        }

        // Clear ListBox selection before mutating ItemsSource-bound collections to avoid
        // Avalonia InternalSelectionModel.CopyTo racing with Clear (Array.Copy ArgumentException).
        SelectionClearRequested?.Invoke();
        FileListLayoutHelper.Rebuild(
            StagedFileEntries, StagedFiles, FileStatusListLayout, flatUsesFullPath: false, _fileStatusExpandState);
        FileListLayoutHelper.Rebuild(
            UnstagedFileEntries, UnstagedFiles, FileStatusListLayout, flatUsesFullPath: false, _fileStatusExpandState);
        FileListLayoutHelper.Rebuild(
            ConflictedFileEntries, ConflictedFiles, FileStatusListLayout, flatUsesFullPath: false, _fileStatusExpandState);
        FileListLayoutHelper.Rebuild(
            StashFileEntries, StashFiles, FileStatusListLayout, flatUsesFullPath: false, _fileStatusExpandState);
    }

    private void RebuildHistoryFileEntries()
    {
        if (IsHistoryContentSearchActive)
        {
            RebuildHistorySearchEntries();
            return;
        }

        FileListLayoutHelper.Rebuild(
            HistoryFileEntries, HistoryFiles, HistoryFileListLayout, flatUsesFullPath: false, _historyExpandState);
    }

    private static FileListLayoutMode NormalizeFileListLayout(FileListLayoutMode mode) =>
        // Legacy persisted "AiSuggested" (numeric 2) collapses to Flat after triage removal.
        mode == FileListLayoutMode.Tree ? FileListLayoutMode.Tree : FileListLayoutMode.Flat;

    partial void OnFileStatusListLayoutChanged(FileListLayoutMode value)
    {
        _settings.Update(s => s.FileStatusListLayout = value);
        _ = _settings.SaveAsync();
        RebuildFileStatusEntries();
        OnPropertyChanged(nameof(IsFileStatusFlatLayout));
        OnPropertyChanged(nameof(IsFileStatusTreeLayout));
        OnPropertyChanged(nameof(FileStatusLayoutIcon));
        SelectionSyncRequested?.Invoke();
    }

    partial void OnHistoryFileListLayoutChanged(FileListLayoutMode value)
    {
        _settings.Update(s => s.HistoryFileListLayout = value);
        _ = _settings.SaveAsync();
        RebuildHistoryFileEntries();
        OnPropertyChanged(nameof(IsHistoryFlatLayout));
        OnPropertyChanged(nameof(IsHistoryTreeLayout));
        OnPropertyChanged(nameof(HistoryLayoutIcon));
        SelectionSyncRequested?.Invoke();
    }

    [RelayCommand]
    private void SetFileStatusListLayout(FileListLayoutMode mode) =>
        FileStatusListLayout = NormalizeFileListLayout(mode);

    [RelayCommand]
    private void SetHistoryFileListLayout(FileListLayoutMode mode) => HistoryFileListLayout = mode;

    [RelayCommand]
    private void ToggleFileStatusFolder(string? folderKey)
    {
        if (string.IsNullOrEmpty(folderKey)) return;
        if (TryToggleFileStatusSearchGroup(folderKey))
            return;
        var expanded = FileListLayoutHelper.IsExpanded(_fileStatusExpandState, folderKey);
        _fileStatusExpandState[folderKey] = !expanded;
        RebuildFileStatusEntries();
        SelectionSyncRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleHistoryFolder(string? folderKey)
    {
        if (string.IsNullOrEmpty(folderKey)) return;
        if (TryToggleHistorySearchGroup(folderKey))
            return;
        var expanded = FileListLayoutHelper.IsExpanded(_historyExpandState, folderKey);
        _historyExpandState[folderKey] = !expanded;
        RebuildHistoryFileEntries();
        SelectionSyncRequested?.Invoke();
    }

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
            DiffEmptyDetail = null;
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
            DiffEmptyDetail = FormatSelectedFileDetail(_selectedFiles);
            DiffOverlayMessage = null;
            OnPropertyChanged(nameof(DiffFooterText));
        }
        else
        {
            if (SelectedFile is not null)
                SelectedFile = null;
            DiffRows.Clear();
            _currentDiff = null;
            DiffEmptyMessage = "Select a file to view its diff";
            DiffEmptyDetail = null;
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

    private static string? FormatSelectedFileDetail(IReadOnlyList<FileItemViewModel> files)
    {
        if (files.Count <= 1) return null;

        var shown = Math.Min(files.Count, DiffEmptyDetailMaxNames);
        var names = new string[shown];
        for (var i = 0; i < shown; i++)
            names[i] = files[i].Name;

        var text = string.Join('\n', names);
        if (files.Count > DiffEmptyDetailMaxNames)
            text += $"\n…and {files.Count - DiffEmptyDetailMaxNames} more";
        return text;
    }

    private void UpdateDiffOverlay()
    {
        if (IsLoadingDiff)
        {
            DiffOverlayMessage = null;
            return;
        }

        // Multi-select uses the DiffViewer brand caption + EmptyDetail; keep the
        // centered overlay clear to avoid duplicated "N files selected" text.
        if (SelectedFileCount != 1)
            DiffOverlayMessage = null;
    }

    partial void OnSelectedFileChanged(FileItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedFileAbsolutePath));
        OnPropertyChanged(nameof(ShowDiffBrandWatermark));
        NotifyMarkdownPreviewStateChanged();

        // Hide change briefing as soon as a file is chosen (don't wait for diff present).
        if (value is not null && PendingReview.IsChangeBriefingSelected)
            PendingReview.IsChangeBriefingSelected = false;

        if (_skipNextSelectedFileLoad)
        {
            _skipNextSelectedFileLoad = false;
            return;
        }

        if (SelectedFileCount <= 1)
            _ = LoadDiffForSelectionAsync(value);
    }

    partial void OnShowMarkdownPreviewChanged(bool value)
    {
        NotifyMarkdownPreviewStateChanged();
        _markdownCts?.Cancel();
        _markdownCts = null;
        if (value && SelectedFile is not null && _currentDiff is not null)
        {
            _markdownCts = new CancellationTokenSource();
            _ = LoadMarkdownPreviewTextAsync(SelectedFile, _currentDiff, _currentDiffTarget, _markdownCts.Token);
        }
        else if (!value)
            MarkdownPreviewText = null;
    }

    partial void OnIsImagePreviewChanged(bool value) => NotifyMarkdownPreviewStateChanged();

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
            _notifications.Error($"View Remote failed: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRebase))]
    private async Task OpenRebaseWizardAsync()
    {
        if (_repoPath is null || !CanRebase) return;

        // Refresh remotes so base branches / origin/HEAD reflect the latest remote tips.
        if (!IsRemoteBusy)
        {
            IsFetching = true;
            try
            {
                await _branches.FetchAsync(_repoPath);
                await LoadBranchesAsync();
            }
            catch (Exception ex)
            {
                _notifications.Error($"Fetch failed: {ex.Message}", null, ex);
            }
            finally
            {
                IsFetching = false;
            }
        }

        string? upstream = null;
        try
        {
            var listed = await _branches.ListBranchesAsync(_repoPath);
            upstream = listed.FirstOrDefault(b => b.IsCurrent)?.Upstream
                ?? listed.FirstOrDefault(b =>
                    string.Equals(b.Name, CurrentBranch, StringComparison.Ordinal))?.Upstream;
        }
        catch
        {
            // Wizard can still open; force-push will be disabled without upstream.
        }

        await RebaseWizard.OpenAsync(_repoPath, CurrentBranch, upstream);
        ShowRebaseWizard = true;
        OnPropertyChanged(nameof(CanUseInProgressBanner));
    }

    [RelayCommand]
    private async Task CloseRebaseWizardAsync()
    {
        if (!ShowRebaseWizard) return;
        if (!await RebaseWizard.RequestCloseAsync())
            return;

        ShowRebaseWizard = false;
        OnPropertyChanged(nameof(CanUseInProgressBanner));
        await RefreshAsync();
    }

    /// <summary>
    /// Closes the rebase review overlay, then force-pushes with lease using the toolbar Push spinner.
    /// </summary>
    [RelayCommand]
    private async Task ForcePushAfterRebaseAsync()
    {
        if (_repoPath is null || !RebaseWizard.HasUpstream || IsRemoteBusy) return;

        ShowRebaseWizard = false;
        OnPropertyChanged(nameof(CanUseInProgressBanner));
        RebaseWizard.Reset();
        // Preserve eligibility so a failure toast can retry after the overlay is gone.
        RebaseWizard.HasUpstream = true;

        IsPushing = true;
        try
        {
            await _remotes.ForcePushWithLeaseAsync(_repoPath, null);
            _notifications.Info("Force-with-lease push completed");
            await RefreshAsync();
        }
        catch (GitException ex)
        {
            var guidance = RebaseWizardViewModel.BuildForcePushGuidance(ex);
            _notifications.Error(guidance, () => _ = ForcePushAfterRebaseAsync(), ex);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Force push failed: {ex.Message}", () => _ = ForcePushAfterRebaseAsync(), ex);
        }
        finally
        {
            IsPushing = false;
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
        DiffEmptyDetail = null;
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
        StashFileEntries.Clear();
        CanStageFromDiff = false;
        StagingDisabledReason = "History diffs are read-only.";
        OnPropertyChanged(nameof(CanStageLines));
        OnPropertyChanged(nameof(CanUnstageLines));
        OnPropertyChanged(nameof(CanDiscardLines));
        OnPropertyChanged(nameof(FileListHeader));
        OnPropertyChanged(nameof(DiffFooterText));
        OnPropertyChanged(nameof(ShowCommitDetailsDock));

        _ = EnterHistoryAsync();
    }

    private async Task EnterHistoryAsync()
    {
        await LoadHistoryBranchesAsync(preferCurrentBranch: true).ConfigureAwait(true);

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
        HistoryFileEntries.Clear();
        DiffEmptyMessage = "Select a commit";
        DiffEmptyDetail = null;
        DiffOverlayMessage = null;
        _ = LoadHistoryAsync(reset: true);
    }

    private async Task ReloadHistoryForSelectedBranchAsync()
    {
        SelectedFile = null;
        _selectedFiles.Clear();
        SelectedFileCount = 0;
        DiffRows.Clear();
        _currentDiff = null;
        ClearImagePreview();
        SelectedCommit = null;
        _allHistoryFiles.Clear();
        HistoryFiles.Clear();
        HistoryFileEntries.Clear();
        DiffEmptyMessage = "Select a commit";
        DiffOverlayMessage = null;
        OnPropertyChanged(nameof(ShowCommitDetailsDock));
        OnPropertyChanged(nameof(DiffFooterText));
        SelectionSyncRequested?.Invoke();
        await LoadHistoryAsync(reset: true);
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

        RebuildHistoryFileEntries();

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
        HistoryFileEntries.Clear();
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
            _notifications.Error($"Failed to load commit files: {ex.Message}", exception: ex);
        }
    }

    private void ApplyHistoryFileFilter(bool autoSelectFirst = false)
    {
        if (!IsHistoryMode) return;

        if (IsHistoryContentSearchActive)
        {
            ScheduleHistoryContentSearch();
            return;
        }

        _historySearchCts?.Cancel();
        _historySearchResults = [];

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

        RebuildHistoryFileEntries();

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
            var page = await _history.ListCommitsAsync(_repoPath, skip, HistoryPageSize, HistoryRevision, ct);
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
            _notifications.Error($"Failed to load history: {ex.Message}", exception: ex);
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
            var page = await _history.ListCommitsAsync(_repoPath, skip: 0, HistoryPageSize, HistoryRevision, ct);
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
                HistoryFileEntries.Clear();
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
            _notifications.Error($"Failed to refresh history: {ex.Message}", exception: ex);
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
            HistoryFileEntries.Clear();
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
        HistoryFileEntries.Clear();
        SelectedCommit = null;
        _cachedHistorySelectedPath = null;
        HistorySearchText = "";
        HistoryFileFilter = "";
        HistoryBranchFilter = "";
        HasMoreHistory = false;
        IsHistoryLoading = false;
        IsHistoryRefreshing = false;
        _suppressHistoryBranchReload = true;
        try
        {
            SelectedHistoryBranch = null;
            HistoryBranches.Clear();
            FilteredHistoryBranches.Clear();
        }
        finally
        {
            _suppressHistoryBranchReload = false;
        }

        OnPropertyChanged(nameof(ShowHistoryBranchFilterEmpty));
    }

    private void CancelCommitFilesLoad()
    {
        _commitFilesCts?.Cancel();
        _commitFilesCts = null;
    }

    private string HistoryRevision =>
        string.IsNullOrWhiteSpace(SelectedHistoryBranch?.Name) ? "HEAD" : SelectedHistoryBranch.Name;

    private static bool IsHistoryBranchCandidate(BranchInfo branch) =>
        !branch.Name.EndsWith("/HEAD", StringComparison.Ordinal);

    private BranchInfo? FindHistoryBranch(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return HistoryBranches.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.Ordinal));
    }

    private void SyncSelectedHistoryBranchToCurrent()
    {
        if (HistoryBranches.Count == 0)
            return;

        var match = FindHistoryBranch(CurrentBranch);
        if (match is null)
            return;

        if (SelectedHistoryBranch is not null
            && string.Equals(SelectedHistoryBranch.Name, match.Name, StringComparison.Ordinal))
        {
            if (!ReferenceEquals(SelectedHistoryBranch, match))
            {
                _suppressHistoryBranchReload = true;
                try { SelectedHistoryBranch = match; }
                finally { _suppressHistoryBranchReload = false; }
            }

            return;
        }

        SelectedHistoryBranch = match;
    }

    private async Task LoadHistoryBranchesAsync(bool preferCurrentBranch)
    {
        if (_repoPath is null) return;
        var listed = await _branches.ListBranchesAsync(_repoPath);
        await InvokeOnUiAsync(() => ApplyHistoryBranches(listed, preferCurrentBranch));
    }

    private void ApplyHistoryBranches(IReadOnlyList<BranchInfo> listed, bool preferCurrentBranch)
    {
        var previousName = SelectedHistoryBranch?.Name;
        _suppressHistoryBranchReload = true;
        try
        {
            HistoryBranches.Clear();
            foreach (var b in listed.Where(IsHistoryBranchCandidate))
                HistoryBranches.Add(b);

            var targetName = preferCurrentBranch
                ? CurrentBranch ?? previousName
                : previousName ?? CurrentBranch;
            SelectedHistoryBranch = FindHistoryBranch(targetName)
                ?? HistoryBranches.FirstOrDefault(b => b.IsCurrent)
                ?? HistoryBranches.FirstOrDefault();
        }
        finally
        {
            _suppressHistoryBranchReload = false;
        }

        RebuildFilteredHistoryBranches();
        OnPropertyChanged(nameof(CanCherryPickHistoryCommit));
        CherryPickCommitCommand.NotifyCanExecuteChanged();
    }

    private void RebuildFilteredHistoryBranches()
    {
        FilteredHistoryBranches.Clear();
        foreach (var branch in HistoryBranches.Where(MatchesHistoryBranchFilter))
            FilteredHistoryBranches.Add(branch);

        OnPropertyChanged(nameof(ShowHistoryBranchFilterEmpty));
    }

    private bool MatchesHistoryBranchFilter(BranchInfo branch)
    {
        if (string.IsNullOrWhiteSpace(HistoryBranchFilter))
            return true;

        return branch.Name.Contains(HistoryBranchFilter, StringComparison.OrdinalIgnoreCase);
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
        DiffEmptyDetail = null;
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

            RebuildFileStatusEntries();

            if (StashFiles.Count > 0)
                ScheduleStashPrefetch(stash.Index, StashFiles.ToList());
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load stash: {ex.Message}", exception: ex);
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
            _notifications.Error($"Apply stash failed: {ex.Message}", exception: ex);
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
                StashFileEntries.Clear();
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
            _notifications.Error($"Delete stash failed: {ex.Message}", exception: ex);
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
                _notifications.Error($"Stash pop failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Stash failed: {ex.Message}", exception: ex);
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
        OnPropertyChanged(nameof(IsUnifiedView));
        OnPropertyChanged(nameof(IsSideBySideView));
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

    [RelayCommand]
    private void ToggleShowMarkdownPreview()
    {
        if (!CanShowMarkdownPreview)
            return;
        ShowMarkdownPreview = !ShowMarkdownPreview;
    }

    [RelayCommand]
    private void ToggleIgnoreWhitespace() => IgnoreWhitespace = !IgnoreWhitespace;

    [RelayCommand]
    private void SetViewMode(DiffViewMode mode) => ViewMode = mode;

    [RelayCommand]
    private void SetContextLines(object? lines)
    {
        var value = lines switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => -1,
        };
        if (value > 0)
            ContextLines = value;
    }

    private void NotifyContextLineSelectionChanged()
    {
        OnPropertyChanged(nameof(IsContextLines1));
        OnPropertyChanged(nameof(IsContextLines3));
        OnPropertyChanged(nameof(IsContextLines5));
        OnPropertyChanged(nameof(IsContextLines10));
        OnPropertyChanged(nameof(IsContextLines25));
    }

    public void ExpandCollapsedSection(int hunkIndex, int lineIndexInHunk) => _diff.ExpandCollapsedSection(hunkIndex, lineIndexInHunk);


    private Task LoadDiffForSelectionAsync(FileItemViewModel? file) => _diff.LoadDiffForSelectionAsync(file);


    /// <summary>
    /// Stale-while-revalidate load: paint a warm (possibly stale) hit immediately, then refresh in
    /// the background when needed. Only clears the viewer when there is no usable cache — including
    /// keeping a same-path painted (or alternate-target warm) diff across stage/unstage target flips.
    /// </summary>
    private Task LoadTrackedDiffWithSwrAsync(
        FileItemViewModel file,
        DiffWarmKey key,
        DiffTarget target,
        bool force,
        Func<CancellationToken, Task<FileDiff>> factory,
        CancellationTokenSource cts,
        CancellationToken ct) => _diff.LoadTrackedDiffWithSwrAsync(file, key, target, force, factory, cts, ct);


    private bool HasPaintedDiffForPath(string path) => _diff.HasPaintedDiffForPath(path);


    /// <summary>
    /// Looks up a completed warm entry for the same path/scope/options under an alternate
    /// <see cref="DiffTarget"/> (used when stage/unstage flips IndexToWorktree ↔ HeadToIndex).
    /// </summary>
    private bool TryGetAlternateTargetWarmEntry(DiffWarmKey key, out DiffWarmEntry? entry) => _diff.TryGetAlternateTargetWarmEntry(key, out entry);


    private static IEnumerable<DiffScope> AlternateDiffScopes(DiffScope scope) => WorkingCopyDiffPresenter.AlternateDiffScopes(scope);


    private static IEnumerable<DiffTarget> AlternateDiffTargets(DiffTarget target) => WorkingCopyDiffPresenter.AlternateDiffTargets(target);


    [RelayCommand]
    private async Task ForceRefreshDiffAsync()
    {
        if (SelectedFile is null || _repoPath is null) return;
        if (SelectedFile.Kind == ChangeKind.Untracked)
        {
            await LoadDiffForSelectionAsync(SelectedFile);
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _diffCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        _markdownCts?.Cancel();
        _markdownCts = null;
        var ct = cts.Token;
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
                    cts,
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
                    cts,
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
                cts,
                ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_diffCts, cts))
                return;
            _notifications.Error($"Diff refresh failed: {ex.Message}", () => _ = ForceRefreshDiffAsync(), ex);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _diffCts, null, cts) == cts)
            {
                IsLoadingDiff = false;
                IsDiffRefreshing = false;
                UpdateFileCacheIndicators();
                OnPropertyChanged(nameof(DiffFooterText));
                cts.Dispose();
            }
        }
    }

    private Task RevalidateSelectedDiffAfterStatusAsync(
        RepositoryStatus? previousStatus,
        RepositoryStatus currentStatus) => _diff.RevalidateSelectedDiffAfterStatusAsync(previousStatus, currentStatus);


    private void SoftInvalidateChangedPaths(RepositoryStatus? previous, RepositoryStatus current) => _diff.SoftInvalidateChangedPaths(previous, current);


    private static Dictionary<string, string> BuildPathOidFingerprint(RepositoryStatus status) => WorkingCopyDiffPresenter.BuildPathOidFingerprint(status);


    private static bool IsSelectedFileContentUnchanged(
        RepositoryStatus? previous,
        RepositoryStatus current,
        FileItemViewModel selected) => WorkingCopyDiffPresenter.IsSelectedFileContentUnchanged(previous, current, selected);


    private bool IsPathInWorkingLists(string path, bool preferStaged) => _diff.IsPathInWorkingLists(path, preferStaged);


    private void ClearDiffCacheState() => _diff.ClearDiffCacheState();


    private void ApplyDiffCacheState(DiffWarmEntry entry) => _diff.ApplyDiffCacheState(entry);


    private void UpdateDiffCacheState(DiffWarmKey key) => _diff.UpdateDiffCacheState(key);


    private static string FormatCacheAge(DateTimeOffset completedAt) => WorkingCopyDiffPresenter.FormatCacheAge(completedAt);


    private void UpdateFileCacheIndicators() => _diff.UpdateFileCacheIndicators();


    private Task PresentDiffAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationTokenSource cts,
        CancellationToken ct) => _diff.PresentDiffAsync(file, diff, target, cts, ct);


    private Task LoadMarkdownPreviewTextAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct) => _diff.LoadMarkdownPreviewTextAsync(file, diff, target, ct);


    private void ClearMarkdownPreviewText() => MarkdownPreviewText = null;

    private void NotifyMarkdownPreviewStateChanged()
    {
        OnPropertyChanged(nameof(IsMarkdownFile));
        OnPropertyChanged(nameof(CanShowMarkdownPreview));
        OnPropertyChanged(nameof(ShowMarkdownPreviewPane));
        OnPropertyChanged(nameof(ShowDiffViewer));
        OnPropertyChanged(nameof(MarkdownPreviewEmptyMessage));
    }

    private void ClearSyntaxTokens() => _diff.ClearSyntaxTokens();


    private Task LoadSyntaxTokensAsync(
        FileItemViewModel file,
        FileDiff diff,
        DiffTarget target,
        CancellationToken ct) => _diff.LoadSyntaxTokensAsync(file, diff, target, ct);


    private Task<string?> ReadSideTextAsync(
        ContentId content,
        FileItemViewModel file,
        DiffTarget target,
        bool sideIsNew,
        CancellationToken ct) => _diff.ReadSideTextAsync(content, file, target, sideIsNew, ct);


    private static string? DecodeUtf8(byte[]? bytes) => WorkingCopyDiffPresenter.DecodeUtf8(bytes);


    private Task<FileDiff> LoadUntrackedFileDiffAsync(
        string repoPath,
        FilePath path,
        DiffTarget target,
        CancellationToken ct) => _diff.LoadUntrackedFileDiffAsync(repoPath, path, target, ct);


    private static DiffWarmKey FileStatusWarmKey(FilePath path, DiffTarget target, DiffOptions options) => WorkingCopyDiffPresenter.FileStatusWarmKey(path, target, options);


    private static DiffWarmKey HistoryWarmKey(string oid, FilePath path, DiffOptions options) => WorkingCopyDiffPresenter.HistoryWarmKey(oid, path, options);


    private static DiffWarmKey StashWarmKey(int index, FilePath path, DiffOptions options) => WorkingCopyDiffPresenter.StashWarmKey(index, path, options);


    private void ScheduleFileStatusPrefetch() => _diff.ScheduleFileStatusPrefetch();


    private Task PrefetchFileStatusDiffsAsync(CancellationToken ct) => _diff.PrefetchFileStatusDiffsAsync(ct);


    private Task<FileDiff> StartFileStatusWarm(
        FilePath path,
        DiffTarget target,
        ChangeKind kind,
        DiffOptions options) => _diff.StartFileStatusWarm(path, target, kind, options);


    /// <summary>
    /// Full warm order: selection neighborhood first, then remaining visible files.
    /// Caller takes the first priority-cap paths as priority; the rest drip.
    /// </summary>
    private List<(FilePath Path, DiffTarget Target, ChangeKind Kind)> BuildFileStatusPrefetchOrder(
        int neighborRadius) => _diff.BuildFileStatusPrefetchOrder(neighborRadius);


    private static int ClampPrefetchDripDelayMs(int value) => WorkingCopyDiffPresenter.ClampPrefetchDripDelayMs(value);


    private static int ClampPrefetchIndicatorThrottleMs(int value) => WorkingCopyDiffPresenter.ClampPrefetchIndicatorThrottleMs(value);


    private static int ClampPrefetchPriorityPaths(int value) => WorkingCopyDiffPresenter.ClampPrefetchPriorityPaths(value);


    private static int ClampPrefetchNeighborRadius(int value) => WorkingCopyDiffPresenter.ClampPrefetchNeighborRadius(value);


    private void ApplyOptimisticFileLists() => _status.ApplyOptimisticFileLists();


    private void SoftInvalidatePathsTimed(IReadOnlyList<FilePath> paths)
    {
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage.invalidate");
        activity?.SetTag("invalidate.path_count", paths.Count);
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var path in paths)
                _warmStore.SoftInvalidatePath(path.Value);
        }
        finally
        {
            GitDeltaMeters.WcStageInvalidateMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task YieldUiAfterOptimisticAsync()
    {
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage.yield");
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.Yield();
        }
        finally
        {
            activity?.SetTag("wc.yield_ms", sw.Elapsed.TotalMilliseconds);
        }
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
        ClearMarkdownPreviewText();
    }

    partial void OnMarkdownPreviewTextChanged(string? value) =>
        OnPropertyChanged(nameof(MarkdownPreviewEmptyMessage));

    private void ClearImagePreviewBitmaps()
    {
        ImageBefore?.Dispose();
        ImageAfter?.Dispose();
        ImageBefore = null;
        ImageAfter = null;
    }

    private DiffOptions BuildDiffOptions() =>
        DiffPresentation.BuildDiffOptions(_settings.Current, IgnoreWhitespace, ShowFullFile, ContextLines);

    private void UpdateDiffStats(FileDiff? diff)
    {
        var added = 0;
        var removed = 0;
        if (diff is not null)
        {
            var stats = FileChangeStats.FromDiff(diff);
            added = stats.LinesAdded;
            removed = stats.LinesRemoved;
            SelectedFile?.ApplyChangeStats(stats);
        }
        SelectedAddedLines = added;
        SelectedRemovedLines = removed;
    }

    private FileDiff EnsureIntraLine(FileDiff diff) => _diff.EnsureIntraLine(diff);


    private HashSet<(int HunkIndex, int LineIndexInHunk)> SnapshotExpandedCollapses() => _diff.SnapshotExpandedCollapses();


    private IReadOnlyList<DiffRow> BuildProjectedRows(
        FileDiff diff,
        DiffViewMode viewMode,
        bool showFullFile,
        ISet<(int HunkIndex, int LineIndexInHunk)> expanded) => _diff.BuildProjectedRows(diff, viewMode, showFullFile, expanded);


    private void ProjectRows(FileDiff diff) => _diff.ProjectRows(diff);


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
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage");
        activity?.SetTag("stage.op", "stage");
        activity?.SetTag("stage.file_count", 1);
        var sw = Stopwatch.StartNew();
        try
        {
            var pending = new PendingMutation(file.Path, WasUnstage: false);
            _pending.Add(pending);
            SoftInvalidatePathsTimed([file.Path]);
            ApplyOptimisticFileLists();
            activity?.SetTag(
                "wc.visible_entry_count",
                StagedFileEntries.Count + UnstagedFileEntries.Count + ConflictedFileEntries.Count);
            await YieldUiAfterOptimisticAsync();
            try
            {
                await _staging.StageFileAsync(_repoPath, file.Path);
            }
            catch (Exception ex)
            {
                _notifications.Error($"Stage failed: {ex.Message}", () => _ = StageFileAsync(file), ex);
            }
            finally
            {
                _pending.Remove(pending);
                await RefreshAndMaybeReloadDiffAsync([file.Path]);
            }
        }
        finally
        {
            GitDeltaMeters.WcStageMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    [RelayCommand]
    private async Task UnstageFileAsync(FileItemViewModel? file)
    {
        if (file is null || _repoPath is null) return;
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage");
        activity?.SetTag("stage.op", "unstage");
        activity?.SetTag("stage.file_count", 1);
        var sw = Stopwatch.StartNew();
        try
        {
            var pending = new PendingMutation(file.Path, WasUnstage: true);
            _pending.Add(pending);
            SoftInvalidatePathsTimed([file.Path]);
            ApplyOptimisticFileLists();
            activity?.SetTag(
                "wc.visible_entry_count",
                StagedFileEntries.Count + UnstagedFileEntries.Count + ConflictedFileEntries.Count);
            await YieldUiAfterOptimisticAsync();
            try
            {
                await _staging.UnstageFileAsync(_repoPath, file.Path);
            }
            catch (Exception ex)
            {
                _notifications.Error($"Unstage failed: {ex.Message}", () => _ = UnstageFileAsync(file), ex);
            }
            finally
            {
                _pending.Remove(pending);
                await RefreshAndMaybeReloadDiffAsync([file.Path]);
            }
        }
        finally
        {
            GitDeltaMeters.WcStageMs.Record(sw.Elapsed.TotalMilliseconds);
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
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage");
        activity?.SetTag("stage.op", "stage");
        activity?.SetTag("stage.file_count", files.Count);
        var sw = Stopwatch.StartNew();
        try
        {
            var pendings = files.Select(f => new PendingMutation(f.Path, WasUnstage: false)).ToList();
            var paths = files.Select(f => f.Path).ToList();
            _pending.AddRange(pendings);
            SoftInvalidatePathsTimed(paths);
            ApplyOptimisticFileLists();
            activity?.SetTag(
                "wc.visible_entry_count",
                StagedFileEntries.Count + UnstagedFileEntries.Count + ConflictedFileEntries.Count);
            await YieldUiAfterOptimisticAsync();
            try
            {
                await _staging.StageFilesAsync(_repoPath, paths);
            }
            catch (Exception ex)
            {
                _notifications.Error($"Stage failed: {ex.Message}", () => _ = StageManyAsync(files), ex);
            }
            finally
            {
                foreach (var pending in pendings)
                    _pending.Remove(pending);
                await RefreshAndMaybeReloadDiffAsync(paths);
            }
        }
        finally
        {
            GitDeltaMeters.WcStageMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task UnstageManyAsync(IReadOnlyList<FileItemViewModel> files)
    {
        if (_repoPath is null || files.Count == 0) return;
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stage");
        activity?.SetTag("stage.op", "unstage");
        activity?.SetTag("stage.file_count", files.Count);
        var sw = Stopwatch.StartNew();
        try
        {
            var pendings = files.Select(f => new PendingMutation(f.Path, WasUnstage: true)).ToList();
            var paths = files.Select(f => f.Path).ToList();
            _pending.AddRange(pendings);
            SoftInvalidatePathsTimed(paths);
            ApplyOptimisticFileLists();
            activity?.SetTag(
                "wc.visible_entry_count",
                StagedFileEntries.Count + UnstagedFileEntries.Count + ConflictedFileEntries.Count);
            await YieldUiAfterOptimisticAsync();
            try
            {
                await _staging.UnstageFilesAsync(_repoPath, paths);
            }
            catch (Exception ex)
            {
                _notifications.Error($"Unstage failed: {ex.Message}", () => _ = UnstageManyAsync(files), ex);
            }
            finally
            {
                foreach (var pending in pendings)
                    _pending.Remove(pending);
                await RefreshAndMaybeReloadDiffAsync(paths);
            }
        }
        finally
        {
            GitDeltaMeters.WcStageMs.Record(sw.Elapsed.TotalMilliseconds);
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
            _notifications.Error($"Fetch failed: {ex.Message}", () => _ = FetchAsync(), ex);
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
            _notifications.Error($"{(stage ? "Stage" : "Unstage")} hunk failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Stage lines failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Unstage lines failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Discard failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Discard hunk failed: {ex.Message}", exception: ex);
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
            _notifications.Error($"Discard lines failed: {ex.Message}", exception: ex);
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

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (_repoPath is null || IsCommitting) return;
        if (!HasStagedFiles || string.IsNullOrWhiteSpace(CommitMessage)) return;

        if (!await PendingReview.ConfirmCommitWithUnresolvedAsync().ConfigureAwait(true))
            return;

        // Snapshot inputs before awaiting so a concurrent caller cannot observe cleared state.
        var message = CommitMessage;
        var amend = AmendCommit;
        var noVerify = NoVerify;
        var pushAfter = PushAfterCommit;

        IsCommitting = true;
        try
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _commit.CommitAsync(_repoPath, message, amend, noVerify, hookOutput: null);
            }
            catch (Exception ex)
            {
                _notifications.Error($"Commit failed: {ex.Message}", () => _ = CommitAsync(), ex);
                return;
            }

            GitDeltaMeters.CommitMs.Record(sw.Elapsed.TotalMilliseconds);
            CommitMessage = "";
            AmendCommit = false;

            try
            {
                await RefreshAsync(clearAiReviewAfter: true);
                if (_allHistoryCommits.Count > 0)
                    _ = SoftRefreshHistoryAsync();
                if (pushAfter)
                    await ExecutePushAsync();
            }
            catch (Exception ex)
            {
                _notifications.Error($"Failed to refresh after commit: {ex.Message}", exception: ex);
            }
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
            GitDeltaMeters.PushMs.Record(sw.Elapsed.TotalMilliseconds);
            _notifications.Info("Push completed");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Push failed: {ex.Message}", () => _ = PushAsync(), ex);
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
            GitDeltaMeters.PullMs.Record(sw.Elapsed.TotalMilliseconds);
            await RefreshAsync();
            _notifications.Info("Pull completed");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Pull failed: {ex.Message}", () => _ = PullAsync(), ex);
        }
        finally
        {
            IsPulling = false;
        }
    }

    [RelayCommand]
    private async Task AbortInProgressAsync()
    {
        if (_repoPath is null || !CanUseInProgressBanner) return;
        await _conflicts.AbortAsync(_repoPath);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ContinueInProgressAsync()
    {
        if (_repoPath is null || !CanUseInProgressBanner) return;
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
            _notifications.Error($"Checkout failed: {ex.Message}", exception: ex);
        }
    }

    private async Task StashThenCheckoutAsync(string branch)
    {
        if (_repoPath is null) return;
        await _stash.StashPushAsync(_repoPath, "GitDelta auto-stash");
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
        using var activity = GitDeltaActivity.Source.StartActivity("wc.branches.load");
        var sw = Stopwatch.StartNew();
        try
        {
            var listed = await _branches.ListBranchesAsync(_repoPath).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                Branches.Clear();
                foreach (var b in listed.Where(b => !b.IsRemote))
                    Branches.Add(b);
                ApplyHistoryBranches(listed, preferCurrentBranch: false);
            }, "branches_apply");
            activity?.SetTag("branches.count", listed.Count);
        }
        finally
        {
            GitDeltaMeters.WcBranchesLoadMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCherryPickCommit))]
    private async Task CherryPickCommitAsync(CommitInfo? commit)
    {
        commit ??= SelectedCommit;
        if (_repoPath is null || commit is null || !CanCherryPickHistoryCommit)
            return;

        try
        {
            await _commit.CherryPickAsync(_repoPath, commit.Oid);
            _notifications.Info($"Cherry-picked {commit.ShortOid}");
            await RefreshAsync();
            await LoadBranchesAsync();
            if (IsHistoryMode)
                _ = SoftRefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            _notifications.Error($"Cherry-pick failed: {ex.Message}", exception: ex);
        }
    }

    private bool CanCherryPickCommit(CommitInfo? _) => CanCherryPickHistoryCommit;

    [RelayCommand]
    private async Task CheckoutCommitAsync(CommitInfo? commit)
    {
        commit ??= SelectedCommit;
        if (_repoPath is null || commit is null)
            return;

        try
        {
            await _branches.CheckoutAsync(_repoPath, commit.Oid);
            await RefreshAsync();
            await LoadBranchesAsync();
            if (IsHistoryMode)
                _ = SoftRefreshHistoryAsync();
        }
        catch (GitException ex) when (ex.Message.Contains("local changes", StringComparison.OrdinalIgnoreCase)
                                       || ex.StderrSummary?.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase) == true)
        {
            _notifications.Info("Checkout blocked by local changes. Stash and retry?",
                () => _ = StashThenCheckoutAsync(commit.Oid), "Stash");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Checkout failed: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task RevertCommitAsync(CommitInfo? commit)
    {
        commit ??= SelectedCommit;
        if (_repoPath is null || commit is null)
            return;

        try
        {
            await _commit.RevertAsync(_repoPath, commit.Oid);
            _notifications.Info($"Reverted {commit.ShortOid}");
            await RefreshAsync();
            await LoadBranchesAsync();
            if (IsHistoryMode)
                _ = SoftRefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            _notifications.Error($"Revert failed: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task CopyCommitHashAsync(CommitInfo? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null)
            return;
        await CopyTextToClipboardAsync(commit.Oid);
    }

    [RelayCommand]
    private async Task CopyCommitMessageAsync(CommitInfo? commit)
    {
        commit ??= SelectedCommit;
        if (commit is null)
            return;

        var message = string.IsNullOrEmpty(commit.Body)
            ? commit.Subject
            : $"{commit.Subject}\n\n{commit.Body}";
        await CopyTextToClipboardAsync(message);
    }

    private static async Task CopyTextToClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } window })
            return;

        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null)
            return;

        await clipboard.SetTextAsync(text);
    }

    private async Task LoadStashesAsync()
    {
        if (_repoPath is null) return;
        using var activity = GitDeltaActivity.Source.StartActivity("wc.stashes.load");
        var sw = Stopwatch.StartNew();
        try
        {
            var listed = await _stash.ListStashesAsync(_repoPath).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                var selectedIndex = SelectedStash?.Index;
                Stashes.Clear();
                foreach (var s in listed)
                    Stashes.Add(s);
                if (selectedIndex is int idx)
                    SelectedStash = Stashes.FirstOrDefault(s => s.Index == idx);
            }, "stashes_apply");
            activity?.SetTag("stashes.count", listed.Count);
        }
        catch
        {
            // Stash list failure should not block status refresh.
            activity?.SetTag("stashes.failed", true);
        }
        finally
        {
            GitDeltaMeters.WcStashesLoadMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task LoadStashDiffAsync(FileItemViewModel file, CancellationTokenSource cts, CancellationToken ct)
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
                cts,
                ct);

            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;

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
        catch (DiffTooLargeException ex)
        {
            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;
            DiffEmptyMessage = ex.Message;
            OnPropertyChanged(nameof(DiffFooterText));
            _notifications.Error(ex.Message, exception: ex);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;
            DiffEmptyMessage = $"Failed to load stash diff: {ex.Message}";
            OnPropertyChanged(nameof(DiffFooterText));
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _diffCts, null, cts) == cts)
            {
                IsLoadingDiff = false;
                IsDiffRefreshing = false;
                UpdateDiffCacheState(key);
                UpdateFileCacheIndicators();
                UpdateDiffOverlay();
                cts.Dispose();
            }
        }
    }

    private async Task LoadCommitDiffAsync(FileItemViewModel file, CancellationTokenSource cts, CancellationToken ct)
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
                cts,
                ct);

            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;

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
        catch (DiffTooLargeException ex)
        {
            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;
            DiffEmptyMessage = ex.Message;
            OnPropertyChanged(nameof(DiffFooterText));
            _notifications.Error(ex.Message, exception: ex);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_diffCts, cts) || !ReferenceEquals(SelectedFile, file))
                return;
            DiffEmptyMessage = $"Failed to load commit diff: {ex.Message}";
            OnPropertyChanged(nameof(DiffFooterText));
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _diffCts, null, cts) == cts)
            {
                IsLoadingDiff = false;
                IsDiffRefreshing = false;
                UpdateDiffCacheState(key);
                UpdateFileCacheIndicators();
                UpdateDiffOverlay();
                cts.Dispose();
            }
        }
    }

    private static async Task InvokeOnUiAsync(Action action, string? reason = null)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                InvokeTimed(action, reason);
                return;
            }

            // Unit tests reference Avalonia but have no running lifetime — InvokeAsync would hang.
            if (Application.Current is null)
            {
                InvokeTimed(action, reason);
                return;
            }

            await dispatcher.InvokeAsync(() => InvokeTimed(action, reason));
        }
        catch (InvalidOperationException)
        {
            InvokeTimed(action, reason);
        }
    }

    private static void InvokeTimed(Action action, string? reason)
    {
        if (reason is null)
        {
            action();
            return;
        }

        var sw = Stopwatch.StartNew();
        action();
        var ms = sw.Elapsed.TotalMilliseconds;
        if (ms < SlowUiInvokeMs)
            return;

        using var activity = GitDeltaActivity.Source.StartActivity("ui.invoke.slow");
        activity?.SetTag("invoke.reason", reason);
        activity?.SetTag("invoke.ms", ms);
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
    [ObservableProperty] private int _totalCommentCount;
    [ObservableProperty] private int _unresolvedThreadCount;
    [ObservableProperty] private bool _isViewed;
    [ObservableProperty] private bool _isViewedPending;
    [ObservableProperty] private bool _hasCommentThreads;
    [ObservableProperty] private bool _hasStaleThreads;

    [ObservableProperty] private int? _linesAdded;
    [ObservableProperty] private int? _linesRemoved;
    [ObservableProperty] private int? _changePercent;

    /// <summary>Future AI semantic classification; unset until a later AI overhaul populates it.</summary>
    [ObservableProperty] private AiChangeClassification? _aiChangeClassification;

    public bool HasLineStats => LinesAdded.HasValue && LinesRemoved.HasValue;
    public bool HasChangePercent => ChangePercent.HasValue;
    public bool HasAiChangeClassification => AiChangeClassification.HasValue;

    public string? ChangePercentTooltip =>
        ChangePercent is int p ? $"{p}% of file changed" : null;

    /// <summary>Applies locally computed diff stats for the file list row.</summary>
    public void ApplyChangeStats(FileChangeStats stats)
    {
        if (LinesAdded == stats.LinesAdded
            && LinesRemoved == stats.LinesRemoved
            && ChangePercent == stats.ChangePercent)
            return;

        LinesAdded = stats.LinesAdded;
        LinesRemoved = stats.LinesRemoved;
        ChangePercent = stats.ChangePercent;
    }

    /// <summary>Applies add/delete counts when total/percent may be unknown (e.g. GraphQL).</summary>
    public void ApplyLineCounts(int added, int removed, int? changePercent = null)
    {
        var stats = FileChangeStats.FromCounts(added, removed, totalLines: null, Kind);
        LinesAdded = stats.LinesAdded;
        LinesRemoved = stats.LinesRemoved;
        ChangePercent = changePercent ?? stats.ChangePercent;
    }

    partial void OnLinesAddedChanged(int? value) => OnPropertyChanged(nameof(HasLineStats));
    partial void OnLinesRemovedChanged(int? value) => OnPropertyChanged(nameof(HasLineStats));
    partial void OnChangePercentChanged(int? value)
    {
        OnPropertyChanged(nameof(HasChangePercent));
        OnPropertyChanged(nameof(ChangePercentTooltip));
    }

    partial void OnAiChangeClassificationChanged(AiChangeClassification? value) =>
        OnPropertyChanged(nameof(HasAiChangeClassification));

    public static FileItemViewModel From(StatusEntry e, bool isStagedList) =>
        new(e.Path, e.Kind, isStagedList,
            isPartial: e.IsStaged && e.IsUnstaged,
            isConflicted: e.IsConflicted);
}
