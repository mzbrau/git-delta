using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Controls;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;
using CodeReviewr.Review;

namespace CodeReviewr.App.ViewModels;

public partial class ReviewViewModel : ObservableObject
{
    private readonly IPullRequestService _pullRequests;
    private readonly IReviewService _reviewService;
    private readonly IReviewCommentService _comments;
    private readonly IReviewOutbox _outbox;
    private readonly IDurableUserStore _durableStore;
    private readonly IGitCloneService _cloneService;
    private readonly IConfirmDialog _confirm;
    private readonly IReviewSubmitDialog _reviewSubmit;
    private readonly ISettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly IIntraLineDiffer _intraLine;
    private readonly IGitObjectReader _objects;
    private readonly ISyntaxTokenService? _syntaxTokens;
    private readonly List<PendingReviewMutation> _pending = [];
    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _inboxCts;
    private ReviewSession? _session;
    private FileDiff? _currentDiff;
    private IReadOnlyList<ReviewThread> _allThreads = [];
    private DateTimeOffset? _lastInboxRefresh;
    private static readonly TimeSpan InboxRefreshDebounce = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, bool> _prExpandState = new(StringComparer.Ordinal);
    private bool _suppressPrEntrySync;

    public ReviewViewModel(
        IPullRequestService pullRequestService,
        IReviewService reviewService,
        IReviewCommentService commentService,
        IReviewOutbox outbox,
        IDurableUserStore durableStore,
        IGitCloneService cloneService,
        IConfirmDialog confirm,
        IReviewSubmitDialog reviewSubmit,
        ISettingsStore settings,
        NotificationService notifications,
        IIntraLineDiffer intraLine,
        IGitObjectReader objects,
        ISyntaxTokenService? syntaxTokens = null)
    {
        _pullRequests = pullRequestService;
        _reviewService = reviewService;
        _comments = commentService;
        _outbox = outbox;
        _durableStore = durableStore;
        _cloneService = cloneService;
        _confirm = confirm;
        _reviewSubmit = reviewSubmit;
        _settings = settings;
        _notifications = notifications;
        _intraLine = intraLine;
        _objects = objects;
        _syntaxTokens = syntaxTokens;
        ViewMode = settings.Current.DefaultDiffMode;
        _ignoreWhitespace = settings.Current.IgnoreWhitespace;
        _contextLines = settings.Current.ContextLines > 0 ? settings.Current.ContextLines : 3;
        _pullRequestFileListLayout = settings.Current.PullRequestFileListLayout;
        _outbox.DrainCompleted += (_, _) => _ = OnOutboxDrainCompletedAsync();
    }

    public ObservableCollection<PullRequestSummary> NeedsMyReview { get; } = [];
    public ObservableCollection<PullRequestSummary> Reviewed { get; } = [];
    public ObservableCollection<PullRequestSummary> MyPullRequests { get; } = [];
    public ObservableCollection<FileItemViewModel> PrFiles { get; } = [];
    public ObservableCollection<FileItemViewModel> FilteredPrFiles { get; } = [];
    public ObservableCollection<FileListEntry> PrFileEntries { get; } = [];
    public ObservableCollection<DiffRow> DiffRows { get; } = [];
    public ObservableCollection<ReviewThreadViewModel> Threads { get; } = [];
    public ObservableCollection<ReviewThread> UnplaceableThreads { get; } = [];
    public ObservableCollection<ReviewThread> FileLevelThreads { get; } = [];
    public ObservableCollection<IDiffAnnotation> DiffAnnotations { get; } = [];
    public ObservableCollection<ReviewerStatusItem> Reviewers { get; } = [];

    [ObservableProperty] private PullRequestSummary? _selectedPullRequest;
    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private WorkspaceMode _workspaceMode = WorkspaceMode.FileStatus;
    [ObservableProperty] private bool _pullRequestsExpanded = true;
    [ObservableProperty] private bool _needsMyReviewExpanded = true;
    [ObservableProperty] private bool _reviewedExpanded = true;
    [ObservableProperty] private bool _myPullRequestsExpanded = true;
    [ObservableProperty] private bool _isRefreshingInbox;
    [ObservableProperty] private bool _isOpeningPullRequest;
    [ObservableProperty] private bool _isLoadingDiff;
    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private bool _viewedIsLocalOnly;
    [ObservableProperty] private string? _pullRequestTitle;
    [ObservableProperty] private string? _pullRequestSubtitle;
    [ObservableProperty] private string _diffEmptyMessage = "Select a pull request";
    [ObservableProperty] private DiffViewMode _viewMode;
    [ObservableProperty] private bool _ignoreWhitespace;
    [ObservableProperty] private int _contextLines = 3;
    [ObservableProperty] private bool _showFullFile;
    [ObservableProperty] private int _selectedAddedLines;
    [ObservableProperty] private int _selectedRemovedLines;
    [ObservableProperty] private string _localNotes = "";
    [ObservableProperty] private string _newCommentBody = "";
    [ObservableProperty] private bool _isSubmittingReview;
    [ObservableProperty] private string? _pullRequestBody;
    [ObservableProperty] private string _fileThreadSummary = string.Empty;
    [ObservableProperty] private string _conversationThreadSummary = string.Empty;
    [ObservableProperty] private int _pendingCommentCount;
    [ObservableProperty] private bool _isOwnPullRequest;
    [ObservableProperty] private string? _myReviewState;
    [ObservableProperty] private ReviewThread? _selectedThread;
    [ObservableProperty] private IDiffAnnotation? _selectedAnnotation;
    [ObservableProperty] private bool _isConversationSelected;
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private FileListLayoutMode _pullRequestFileListLayout;
    [ObservableProperty] private FileListEntry? _selectedPrFileEntry;
    [ObservableProperty] private ViewedFilter _filterViewed = ViewedFilter.All;
    [ObservableProperty] private bool _filterStale;
    [ObservableProperty] private bool _filterCommented;
    [ObservableProperty] private bool _filterUnresolved;
    [ObservableProperty] private bool _headHasMoved;
    [ObservableProperty] private string? _headMovedBanner;
    [ObservableProperty] private string? _reviewDecision;
    [ObservableProperty] private string? _checkRollupState;
    [ObservableProperty] private string? _mergeStateSummary;
    [ObservableProperty] private bool _filterFocusRequested;
    [ObservableProperty] private FileSyntaxTokens? _leftSyntaxTokens;
    [ObservableProperty] private FileSyntaxTokens? _rightSyntaxTokens;
    [ObservableProperty] private int? _draftCommentLine;
    [ObservableProperty] private int? _draftCommentStartLine;
    [ObservableProperty] private string? _draftCommentSide;
    [ObservableProperty] private bool _hasDraftCommentAnchor;
    [ObservableProperty] private string _draftCommentTargetLabel = "";

    public event Action? FocusCommentDraftRequested;
    public event Action? FocusFileFilterRequested;
    public event Action? ExpandedThreadChanged;

    /// <summary>True when a placeable thread is selected and should render as an inline card under the line.</summary>
    public bool HasExpandedInlineThread =>
        SelectedThread is { Anchor: not null, IsFileLevel: false, IsUnplaceable: false } &&
        !HasDraftCommentAnchor;

    /// <summary>Side panel only for file-level / unplaceable threads (placeable ones use the inline card).</summary>
    public bool ShowSideThreadPanel =>
        SelectedThread is { } t &&
        (t.IsFileLevel || t.IsUnplaceable || t.Anchor is null) &&
        !HasDraftCommentAnchor;

    public bool HasFileFilter => !string.IsNullOrWhiteSpace(FileFilter);
    public bool IsPullRequestFlatLayout => PullRequestFileListLayout == FileListLayoutMode.Flat;
    public bool IsPullRequestTreeLayout => PullRequestFileListLayout == FileListLayoutMode.Tree;
    public bool IsUnifiedView => ViewMode == DiffViewMode.Unified;
    public bool IsSideBySideView => ViewMode == DiffViewMode.SideBySide;
    public Material.Icons.MaterialIconKind PullRequestLayoutIcon =>
        PullRequestFileListLayout == FileListLayoutMode.Tree
            ? Material.Icons.MaterialIconKind.FileTree
            : Material.Icons.MaterialIconKind.FormatListBulleted;
    public int ViewedFileCount => PrFiles.Count(f => f.IsViewed);
    public int TotalFileCount => PrFiles.Count;
    public int CommentCount => _allThreads.Sum(t => t.Comments.Count);
    public int UnresolvedCount => _allThreads.Count(t => !t.IsResolved);
    public int OutdatedCount => _allThreads.Count(t => t.IsOutdated);
    public bool HasPendingComments => PendingCommentCount > 0;
    public bool HasReviewers => Reviewers.Count > 0;
    public bool HasApproved =>
        string.Equals(MyReviewState, "APPROVED", StringComparison.OrdinalIgnoreCase);
    public bool HasRequestedChanges =>
        string.Equals(MyReviewState, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase);
    public bool CanSubmitPendingComments => HasPendingComments && !IsSubmittingReview;
    public bool CanSubmitVerdict => !IsOwnPullRequest && !IsSubmittingReview;
    public string PendingCommentsTooltip => PendingCommentCount <= 0
        ? "No pending comments to submit"
        : PendingCommentCount == 1
            ? "1 pending comment — submit without approval"
            : $"{PendingCommentCount} pending comments — submit without approval";
    public string ApproveTooltip => IsOwnPullRequest
        ? "You cannot review your own pull request"
        : "Approve";
    public string RequestChangesTooltip => IsOwnPullRequest
        ? "You cannot review your own pull request"
        : "Request changes";
    public string ProgressSummary =>
        $"{ViewedFileCount} / {TotalFileCount} files viewed · {CommentCount} comments · {UnresolvedCount} unresolved";

    public IReadOnlyList<StatusCheckItem> StatusChecks { get; private set; } = [];
    public IReadOnlyList<PullRequestTimelineEntry> Timeline { get; private set; } = [];

    public bool IsPullRequestMode => WorkspaceMode == WorkspaceMode.PullRequest;
    public bool HasSelectedPullRequest => SelectedPullRequest is not null;
    public string DiffFooterText =>
        SelectedFile is null ? "" : $"+{SelectedAddedLines} -{SelectedRemovedLines}";

    public int[] ContextLineOptions { get; } = [1, 3, 5, 10, 20];

    public int ContextLinesIndex
    {
        get
        {
            var idx = Array.IndexOf(ContextLineOptions, ContextLines);
            return idx >= 0 ? idx : 1;
        }
        set
        {
            if (value < 0 || value >= ContextLineOptions.Length) return;
            ContextLines = ContextLineOptions[value];
        }
    }

    partial void OnWorkspaceModeChanged(WorkspaceMode value) => OnPropertyChanged(nameof(IsPullRequestMode));

    partial void OnSelectedPullRequestChanged(PullRequestSummary? value) =>
        OnPropertyChanged(nameof(HasSelectedPullRequest));

    partial void OnViewModeChanged(DiffViewMode value)
    {
        OnPropertyChanged(nameof(IsUnifiedView));
        OnPropertyChanged(nameof(IsSideBySideView));
        if (_currentDiff is not null)
            ProjectRows(_currentDiff);
    }

    partial void OnPendingCommentCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingComments));
        OnPropertyChanged(nameof(PendingCommentsTooltip));
        OnPropertyChanged(nameof(CanSubmitPendingComments));
        SubmitCommentReviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSubmittingReviewChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmitPendingComments));
        OnPropertyChanged(nameof(CanSubmitVerdict));
        SubmitCommentReviewCommand.NotifyCanExecuteChanged();
        SubmitApproveCommand.NotifyCanExecuteChanged();
        SubmitRequestChangesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOwnPullRequestChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmitVerdict));
        OnPropertyChanged(nameof(ApproveTooltip));
        OnPropertyChanged(nameof(RequestChangesTooltip));
        SubmitApproveCommand.NotifyCanExecuteChanged();
        SubmitRequestChangesCommand.NotifyCanExecuteChanged();
    }

    partial void OnMyReviewStateChanged(string? value)
    {
        OnPropertyChanged(nameof(HasApproved));
        OnPropertyChanged(nameof(HasRequestedChanges));
    }

    partial void OnIgnoreWhitespaceChanged(bool value) => _ = ReloadSelectedDiffAsync();
    partial void OnContextLinesChanged(int value) => _ = ReloadSelectedDiffAsync();
    partial void OnShowFullFileChanged(bool value) => _ = ReloadSelectedDiffAsync();
    partial void OnSelectedFileChanged(FileItemViewModel? value)
    {
        ClearDraftCommentAnchor();
        if (value is not null)
            IsConversationSelected = false;
        SyncSelectedPrFileEntry();
        _ = LoadDiffForSelectionAsync(value);
    }

    partial void OnSelectedPrFileEntryChanged(FileListEntry? value)
    {
        if (_suppressPrEntrySync) return;
        if (value is { IsFolder: true, FolderKey: { } key })
        {
            TogglePrFolder(key);
            _suppressPrEntrySync = true;
            try { SyncSelectedPrFileEntry(); }
            finally { _suppressPrEntrySync = false; }
            return;
        }

        SelectedFile = value?.File;
    }

    partial void OnPullRequestFileListLayoutChanged(FileListLayoutMode value)
    {
        _settings.Update(s => s.PullRequestFileListLayout = value);
        _ = _settings.SaveAsync();
        RebuildPrFileEntries();
        OnPropertyChanged(nameof(IsPullRequestFlatLayout));
        OnPropertyChanged(nameof(IsPullRequestTreeLayout));
        OnPropertyChanged(nameof(PullRequestLayoutIcon));
    }

    [RelayCommand]
    private void SetPullRequestFileListLayout(FileListLayoutMode mode) => PullRequestFileListLayout = mode;

    [RelayCommand]
    private void TogglePrFolder(string? folderKey)
    {
        if (string.IsNullOrEmpty(folderKey)) return;
        var expanded = FileListLayoutHelper.IsExpanded(_prExpandState, folderKey);
        _prExpandState[folderKey] = !expanded;
        RebuildPrFileEntries();
    }

    partial void OnIsConversationSelectedChanged(bool value)
    {
        if (value)
        {
            ClearDraftCommentAnchor();
            SelectedFile = null;
            DiffRows.Clear();
            _currentDiff = null;
            DiffEmptyMessage = "Pull request context";
        }
    }

    partial void OnFileFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasFileFilter));
        ApplyPrFileFilter();
    }

    partial void OnFilterViewedChanged(ViewedFilter value) => ApplyPrFileFilter();
    partial void OnFilterStaleChanged(bool value) => ApplyPrFileFilter();
    partial void OnFilterCommentedChanged(bool value) => ApplyPrFileFilter();
    partial void OnFilterUnresolvedChanged(bool value) => ApplyPrFileFilter();

    partial void OnFilterFocusRequestedChanged(bool value)
    {
        if (value)
        {
            FilterFocusRequested = false;
            FocusFileFilterRequested?.Invoke();
        }
    }

    partial void OnLocalNotesChanged(string value)
    {
        if (_session is null) return;
        _ = SaveLocalNotesAsync(value);
    }

    partial void OnSelectedAnnotationChanged(IDiffAnnotation? value)
    {
        SelectedThread = value is ReviewThreadAnnotation annotation ? annotation.Thread : null;
    }

    partial void OnSelectedThreadChanged(ReviewThread? value)
    {
        OnPropertyChanged(nameof(HasExpandedInlineThread));
        OnPropertyChanged(nameof(ShowSideThreadPanel));
        ExpandedThreadChanged?.Invoke();
    }

    partial void OnHasDraftCommentAnchorChanged(bool value)
    {
        OnPropertyChanged(nameof(HasExpandedInlineThread));
        OnPropertyChanged(nameof(ShowSideThreadPanel));
        ExpandedThreadChanged?.Invoke();
    }

    partial void OnIsOfflineChanged(bool value) { }

    [RelayCommand]
    private void ClearFileFilter() => FileFilter = "";

    [RelayCommand]
    private void SelectConversation() => IsConversationSelected = true;

    [RelayCommand]
    private void RequestFileFilterFocus() => FilterFocusRequested = true;

    [RelayCommand]
    private void SelectNextFile() => MoveFileSelection(step: 1);

    [RelayCommand]
    private void SelectPreviousFile() => MoveFileSelection(step: -1);

    [RelayCommand]
    private void SelectNextThread() => MoveThreadSelection(step: 1);

    [RelayCommand]
    private void SelectPreviousThread() => MoveThreadSelection(step: -1);

    [RelayCommand]
    private void FocusCommentDraft() => FocusCommentDraftRequested?.Invoke();

    [RelayCommand]
    private async Task ToggleSelectedViewedAsync()
    {
        if (SelectedFile is not null)
            await ToggleViewedAsync(SelectedFile);
    }

    [RelayCommand]
    private Task SubmitCommentShortcutAsync() => SubmitCommentReviewAsync();

    private void MoveFileSelection(int step)
    {
        if (FilteredPrFiles.Count == 0) return;

        var index = SelectedFile is null
            ? (step > 0 ? -1 : FilteredPrFiles.Count)
            : FilteredPrFiles.IndexOf(SelectedFile);
        if (index < 0) index = step > 0 ? -1 : FilteredPrFiles.Count;

        var next = index + step;
        if (next < 0 || next >= FilteredPrFiles.Count) return;

        IsConversationSelected = false;
        SelectedFile = FilteredPrFiles[next];
    }

    private void MoveThreadSelection(int step)
    {
        if (Threads.Count == 0) return;

        var currentIndex = SelectedThread is null
            ? (step > 0 ? -1 : Threads.Count)
            : Threads.ToList().FindIndex(t => t.NodeId == SelectedThread.NodeId);
        if (currentIndex < 0) currentIndex = step > 0 ? -1 : Threads.Count;

        var next = currentIndex + step;
        if (next < 0 || next >= Threads.Count) return;

        var threadVm = Threads[next];
        SelectedThread = _allThreads.FirstOrDefault(t => t.NodeId == threadVm.NodeId);
        var annotation = DiffAnnotations
            .OfType<ReviewThreadAnnotation>()
            .FirstOrDefault(a => a.Thread.NodeId == threadVm.NodeId);
        if (annotation is not null)
            SelectedAnnotation = annotation;
    }

    private void ApplyPrFileFilter()
    {
        InvokeOnUiAsync(() =>
        {
            // Capture path before Clear() — ListBox TwoWay binding nulls SelectedFile when the
            // item is removed, so we must restore by path after refill.
            var selectedPath = SelectedFile?.Path.Value;

            FilteredPrFiles.Clear();
            foreach (var file in PrFiles.Where(MatchesPrFileFilter))
                FilteredPrFiles.Add(file);

            RebuildPrFileEntries();

            if (selectedPath is not null)
            {
                SelectedFile = FilteredPrFiles.FirstOrDefault(f =>
                                   string.Equals(f.Path.Value, selectedPath, StringComparison.Ordinal))
                               ?? FilteredPrFiles.FirstOrDefault();
            }

            UpdateProgressSummary();
        }).GetAwaiter().GetResult();
    }

    private void RebuildPrFileEntries()
    {
        // Suppress for the whole rebuild: PrFileEntries.Clear() makes the ListBox
        // TwoWay-null SelectedPrFileEntry, which would clear SelectedFile and wipe DiffRows.
        _suppressPrEntrySync = true;
        try
        {
            FileListLayoutHelper.Rebuild(
                PrFileEntries,
                FilteredPrFiles,
                PullRequestFileListLayout,
                flatUsesFullPath: true,
                _prExpandState);

            if (SelectedFile is null)
            {
                SelectedPrFileEntry = null;
                return;
            }

            SelectedPrFileEntry = PrFileEntries.FirstOrDefault(e =>
                e.File is not null &&
                string.Equals(e.File.Path.Value, SelectedFile.Path.Value, StringComparison.Ordinal));
        }
        finally
        {
            _suppressPrEntrySync = false;
        }
    }

    private void SyncSelectedPrFileEntry()
    {
        _suppressPrEntrySync = true;
        try
        {
            if (SelectedFile is null)
            {
                SelectedPrFileEntry = null;
                return;
            }

            SelectedPrFileEntry = PrFileEntries.FirstOrDefault(e =>
                e.File is not null &&
                string.Equals(e.File.Path.Value, SelectedFile.Path.Value, StringComparison.Ordinal));
        }
        finally
        {
            _suppressPrEntrySync = false;
        }
    }

    private bool MatchesPrFileFilter(FileItemViewModel file)
    {
        if (!string.IsNullOrWhiteSpace(FileFilter))
        {
            if (!file.Path.Value.Contains(FileFilter, StringComparison.OrdinalIgnoreCase) &&
                !file.Path.Name.Contains(FileFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (FilterViewed == ViewedFilter.Viewed && !file.IsViewed)
            return false;
        if (FilterViewed == ViewedFilter.NotViewed && file.IsViewed)
            return false;
        if (FilterStale && !file.HasStaleThreads)
            return false;
        if (FilterCommented && !file.HasCommentThreads)
            return false;
        if (FilterUnresolved && file.UnresolvedThreadCount == 0)
            return false;

        return true;
    }

    private void UpdateProgressSummary()
    {
        OnPropertyChanged(nameof(ViewedFileCount));
        OnPropertyChanged(nameof(TotalFileCount));
        OnPropertyChanged(nameof(CommentCount));
        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(OutdatedCount));
        OnPropertyChanged(nameof(ProgressSummary));
        UpdateConversationThreadSummary();
    }

    private void UpdateConversationThreadSummary()
    {
        var unresolved = UnresolvedCount;
        var outdated = OutdatedCount;
        if (unresolved == 0 && outdated == 0)
        {
            ConversationThreadSummary = string.Empty;
            return;
        }

        var parts = new List<string>();
        if (unresolved > 0)
            parts.Add($"{unresolved} unresolved");
        if (outdated > 0)
            parts.Add($"{outdated} outdated");
        ConversationThreadSummary = string.Join(" · ", parts);
    }

    private void UpdatePullRequestContext(ReviewSession session)
    {
        var detail = session.Detail;
        PullRequestBody = detail.Body;
        ReviewDecision = detail.Summary.ReviewDecision;
        CheckRollupState = detail.CheckRollupState;
        StatusChecks = detail.StatusChecks ?? [];
        Timeline = detail.Timeline ?? [];
        OnPropertyChanged(nameof(StatusChecks));
        OnPropertyChanged(nameof(Timeline));

        MergeStateSummary = FormatMergeState(detail.Mergeable, detail.MergeStateStatus);
        UpdateHeadMovedState(session, detail.Summary.HeadOid);
        ApplyReviewers(detail.Reviewers ?? []);
        MyReviewState = detail.ViewerReviewState;
        IsOwnPullRequest =
            !string.IsNullOrWhiteSpace(detail.Summary.AuthorLogin) &&
            string.Equals(
                detail.Summary.AuthorLogin,
                detail.Summary.AccountLogin,
                StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyReviewers(IReadOnlyList<PullRequestReviewerStatus> reviewers)
    {
        Reviewers.Clear();
        foreach (var reviewer in reviewers)
        {
            var item = new ReviewerStatusItem(reviewer.Login, reviewer.AvatarUrl, reviewer.State);
            Reviewers.Add(item);
            _ = item.LoadAvatarAsync();
        }

        OnPropertyChanged(nameof(HasReviewers));
    }

    private static string? FormatMergeState(bool? mergeable, string? mergeStateStatus)
    {
        if (mergeable is true && string.Equals(mergeStateStatus, "CLEAN", StringComparison.OrdinalIgnoreCase))
            return "Mergeable";
        if (mergeable is false)
            return $"Not mergeable ({mergeStateStatus ?? "blocked"})";
        return mergeStateStatus;
    }

    private void UpdateHeadMovedState(ReviewSession session, string? remoteHeadOid)
    {
        if (string.IsNullOrWhiteSpace(remoteHeadOid))
        {
            HeadHasMoved = false;
            HeadMovedBanner = null;
            return;
        }

        var sessionHead = session.Head.Value;
        HeadHasMoved = !string.Equals(sessionHead, remoteHeadOid, StringComparison.OrdinalIgnoreCase);
        HeadMovedBanner = HeadHasMoved
            ? $"Head moved — session pinned to {sessionHead[..7]}, latest is {remoteHeadOid[..7]}"
            : null;
    }

    private void UpdateFileThreadFlags()
    {
        foreach (var file in PrFiles)
        {
            var path = file.Path.Value;
            var threads = _allThreads
                .Where(t => string.Equals(t.Path, path, StringComparison.Ordinal))
                .ToList();
            file.HasCommentThreads = threads.Count > 0;
            file.HasStaleThreads = threads.Any(t => t.IsOutdated);
            file.UnresolvedThreadCount = threads.Count(t => !t.IsResolved);
        }
    }

    public void NotifyWindowActivated()
    {
        _ = _outbox.DrainAsync();
        IsOffline = _outbox.IsOffline;

        if (DateTimeOffset.UtcNow - (_lastInboxRefresh ?? DateTimeOffset.MinValue) < InboxRefreshDebounce)
            return;

        _ = RefreshInboxCoreAsync(silent: true);
    }

    [RelayCommand]
    private Task RefreshInboxAsync() => RefreshInboxCoreAsync(silent: false);

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _notifications.Error($"Could not open URL: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearExpandedThread()
    {
        SelectedThread = null;
        SelectedAnnotation = null;
    }

    [RelayCommand]
    private void BeginLineComment(LineCommentRequest? request)
    {
        if (request is null || SelectedFile is null)
            return;

        // Collapse any expanded thread so draft and thread cards don't stack.
        SelectedThread = null;
        SelectedAnnotation = null;

        DraftCommentSide = request.Side == DiffSide.Old ? "LEFT" : "RIGHT";
        DraftCommentLine = request.Line;
        DraftCommentStartLine = request.StartLine;
        HasDraftCommentAnchor = true;
        DraftCommentTargetLabel = request.StartLine is { } start && start != request.Line
            ? $"Commenting on L{start}–L{request.Line} ({DraftCommentSide})"
            : $"Commenting on L{request.Line} ({DraftCommentSide})";

        ApplyProvisionalDraftAnnotation();
        FocusCommentDraftRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearDraftCommentAnchor()
    {
        if (!HasDraftCommentAnchor && DraftCommentLine is null)
            return;

        DraftCommentLine = null;
        DraftCommentStartLine = null;
        DraftCommentSide = null;
        HasDraftCommentAnchor = false;
        DraftCommentTargetLabel = "";
        RemoveProvisionalDraftAnnotations();
    }

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        if (_session is null || SelectedFile is null || string.IsNullOrWhiteSpace(NewCommentBody))
            return;

        var body = NewCommentBody.Trim();
        var line = DraftCommentLine;
        var startLine = DraftCommentStartLine;
        var side = DraftCommentSide ?? "RIGHT";
        var pending = new PendingReviewMutation(PendingReviewMutationKind.AddComment, ClientId: Guid.NewGuid().ToString("N"));
        _pending.Add(pending);
        ApplyOptimisticThreads(body, pending.ClientId);
        if (line is not null)
            ApplyProvisionalDraftAnnotation();
        NewCommentBody = "";
        DraftCommentLine = null;
        DraftCommentStartLine = null;
        DraftCommentSide = null;
        HasDraftCommentAnchor = false;
        DraftCommentTargetLabel = "";

        try
        {
            await _comments.AddPendingCommentAsync(
                    _session,
                    body,
                    SelectedFile.Path,
                    line,
                    startLine,
                    side)
                .ConfigureAwait(false);
            IsOffline = _outbox.IsOffline;
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to queue comment: {ex.Message}");
        }
        finally
        {
            _pending.Remove(pending);
            await RefreshThreadsAsync().ConfigureAwait(false);
            await RefreshPendingCommentCountAsync().ConfigureAwait(false);
        }
    }

    private void ApplyProvisionalDraftAnnotation()
    {
        if (DraftCommentLine is not int line || DraftCommentSide is null || _currentDiff is null)
            return;

        var side = DraftCommentSide == "LEFT" ? DiffSide.Old : DiffSide.New;
        var content = side == DiffSide.Old ? _currentDiff.OldContent : _currentDiff.NewContent;
        RemoveProvisionalDraftAnnotations();
        DiffAnnotations.Add(new PendingLineCommentAnnotation(side, line, DraftCommentStartLine, content));
    }

    private void RemoveProvisionalDraftAnnotations()
    {
        for (var i = DiffAnnotations.Count - 1; i >= 0; i--)
        {
            if (DiffAnnotations[i] is PendingLineCommentAnnotation)
                DiffAnnotations.RemoveAt(i);
        }
    }

    [RelayCommand]
    private async Task ToggleViewedAsync(FileItemViewModel? file)
    {
        if (_session is null || file is null) return;

        var pending = new PendingReviewMutation(
            PendingReviewMutationKind.ToggleViewed,
            ClientId: file.Path.Value,
            TargetViewed: !file.IsViewed);
        _pending.Add(pending);
        file.IsViewed = pending.TargetViewed!.Value;
        file.IsViewedPending = true;

        try
        {
            if (pending.TargetViewed!.Value)
                await _comments.MarkFileViewedAsync(_session, file.Path).ConfigureAwait(false);
            else
                await _comments.UnmarkFileViewedAsync(_session, file.Path).ConfigureAwait(false);
            IsOffline = _outbox.IsOffline;
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to toggle viewed: {ex.Message}");
        }
        finally
        {
            _pending.Remove(pending);
            file.IsViewedPending = false;
            await ApplyViewedStateAsync().ConfigureAwait(false);
            ApplyPrFileFilter();
        }
    }

    [RelayCommand]
    private async Task ResolveThreadAsync(ReviewThreadViewModel? thread)
    {
        if (_session is null || thread is null) return;

        var wasResolved = thread.IsResolved;
        var pending = new PendingReviewMutation(PendingReviewMutationKind.ResolveThread, ClientId: thread.NodeId);
        _pending.Add(pending);
        thread.IsResolved = !wasResolved;
        thread.IsPending = true;

        try
        {
            if (wasResolved)
                await _comments.UnresolveThreadAsync(_session, thread.NodeId).ConfigureAwait(false);
            else
                await _comments.ResolveThreadAsync(_session, thread.NodeId).ConfigureAwait(false);
            IsOffline = _outbox.IsOffline;
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to update thread: {ex.Message}");
        }
        finally
        {
            _pending.Remove(pending);
            thread.IsPending = false;
            await RefreshThreadsAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmitVerdict))]
    private Task SubmitApproveAsync() => SubmitReviewAsync(SubmitReviewEvent.Approve);

    [RelayCommand(CanExecute = nameof(CanSubmitPendingComments))]
    private Task SubmitCommentReviewAsync() => SubmitReviewAsync(SubmitReviewEvent.Comment);

    [RelayCommand(CanExecute = nameof(CanSubmitVerdict))]
    private Task SubmitRequestChangesAsync() => SubmitReviewAsync(SubmitReviewEvent.RequestChanges);

    [RelayCommand]
    private void ToggleIgnoreWhitespace() => IgnoreWhitespace = !IgnoreWhitespace;

    [RelayCommand]
    private void SetViewMode(DiffViewMode mode) => ViewMode = mode;

    private async Task SubmitReviewAsync(SubmitReviewEvent reviewEvent)
    {
        if (_session is null) return;

        string body = "";
        if (reviewEvent is SubmitReviewEvent.Approve or SubmitReviewEvent.RequestChanges)
        {
            var title = reviewEvent == SubmitReviewEvent.Approve ? "Approve pull request" : "Request changes";
            var confirmLabel = reviewEvent == SubmitReviewEvent.Approve ? "Approve" : "Request changes";
            var dialogBody = await _reviewSubmit.ShowAsync(title, confirmLabel).ConfigureAwait(false);
            if (dialogBody is null)
                return;
            body = dialogBody;
        }

        IsSubmittingReview = true;
        try
        {
            await _comments.SubmitReviewAsync(_session, reviewEvent, body).ConfigureAwait(false);
            if (reviewEvent == SubmitReviewEvent.Approve)
                MyReviewState = "APPROVED";
            else if (reviewEvent == SubmitReviewEvent.RequestChanges)
                MyReviewState = "CHANGES_REQUESTED";
            _notifications.Info("Review submitted.");
        }
        catch (HeadMovedException ex)
        {
            _notifications.Error($"Cannot submit: head moved from {ex.ExpectedSha[..7]} to {ex.ActualSha[..7]}.");
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to submit review: {ex.Message}");
        }
        finally
        {
            IsSubmittingReview = false;
            IsOffline = _outbox.IsOffline;
            await RefreshThreadsAsync().ConfigureAwait(false);
            await RefreshPendingCommentCountAsync().ConfigureAwait(false);
        }
    }

    private async Task RefreshInboxCoreAsync(bool silent)
    {
        if (IsRefreshingInbox) return;

        _inboxCts?.Cancel();
        _inboxCts = new CancellationTokenSource();
        var ct = _inboxCts.Token;
        IsRefreshingInbox = true;

        try
        {
            var inbox = await _pullRequests.GetInboxAsync(ct).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                NeedsMyReview.Clear();
                Reviewed.Clear();
                MyPullRequests.Clear();
                foreach (var pr in inbox)
                {
                    switch (pr.Section)
                    {
                        case InboxSection.NeedsMyReview:
                            NeedsMyReview.Add(pr);
                            break;
                        case InboxSection.Reviewed:
                            Reviewed.Add(pr);
                            break;
                        case InboxSection.MyPullRequests:
                            MyPullRequests.Add(pr);
                            break;
                    }
                }
            });

            _lastInboxRefresh = DateTimeOffset.UtcNow;
            IsOffline = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!silent)
        {
            IsOffline = true;
            _notifications.Error($"Failed to refresh pull requests: {ex.Message}",
                () => _ = RefreshInboxAsync());
        }
        finally
        {
            IsRefreshingInbox = false;
        }
    }

    [RelayCommand]
    private async Task SelectPullRequestAsync(PullRequestSummary? summary)
    {
        if (summary is null) return;

        SelectedPullRequest = summary;
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        await InvokeOnUiAsync(() =>
        {
            IsOpeningPullRequest = true;
            WorkspaceMode = WorkspaceMode.PullRequest;
            DiffEmptyMessage = "Loading pull request…";
            IsConversationSelected = false;
            SelectedFile = null;
            PrFiles.Clear();
            FilteredPrFiles.Clear();
            DiffRows.Clear();
            Threads.Clear();
            UnplaceableThreads.Clear();
            FileLevelThreads.Clear();
            DiffAnnotations.Clear();
            PullRequestTitle = $"#{summary.Number} {summary.Title}";
            PullRequestSubtitle = summary.NameWithOwner;
        }).ConfigureAwait(false);

        // Let the loading overlay paint before git/network work.
        await Task.Yield();

        try
        {
            var session = await OpenSessionWithClonePromptAsync(summary, ct).ConfigureAwait(false);
            if (session is null || ct.IsCancellationRequested)
            {
                if (!ct.IsCancellationRequested)
                {
                    await InvokeOnUiAsync(() =>
                    {
                        WorkspaceMode = WorkspaceMode.FileStatus;
                        DiffEmptyMessage = "Select a pull request";
                        PullRequestTitle = null;
                        PullRequestSubtitle = null;
                    }).ConfigureAwait(false);
                }

                return;
            }

            _session = session;
            await InvokeOnUiAsync(() =>
            {
                WorkspaceMode = WorkspaceMode.PullRequest;
                PullRequestTitle = $"#{session.Detail.Summary.Number} {session.Detail.Summary.Title}";
                PullRequestSubtitle =
                    $"{session.Detail.Summary.BaseRefName} ← {session.Detail.Summary.HeadRefName} · {session.Detail.Summary.NameWithOwner}";
                UpdatePullRequestContext(session);
                PrFiles.Clear();
                FilteredPrFiles.Clear();
                foreach (var (path, kind) in session.Files)
                    PrFiles.Add(new FileItemViewModel(path, kind, isStagedList: false));
                ApplyPrFileFilter();
            }).ConfigureAwait(false);

            LocalNotes = await _durableStore.GetNoteAsync(summary.NodeId, ct).ConfigureAwait(false) ?? "";
            ViewedIsLocalOnly = !await _comments.SupportsRemoteViewedStateAsync(session, ct).ConfigureAwait(false);
            await ApplyViewedStateAsync().ConfigureAwait(false);
            await RefreshThreadsAsync().ConfigureAwait(false);
            await RefreshPendingCommentCountAsync().ConfigureAwait(false);
            await _outbox.DrainAsync(ct).ConfigureAwait(false);
            IsOffline = _outbox.IsOffline;

            if (ct.IsCancellationRequested)
                return;

            await InvokeOnUiAsync(() =>
            {
                if (FilteredPrFiles.Count > 0)
                    SelectedFile = FilteredPrFiles[0];
                else
                    DiffEmptyMessage = "Select a file to view its diff";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to open pull request: {ex.Message}",
                () => _ = SelectPullRequestAsync(summary));
            await InvokeOnUiAsync(() =>
            {
                WorkspaceMode = WorkspaceMode.FileStatus;
                DiffEmptyMessage = "Select a pull request";
            }).ConfigureAwait(false);
        }
        finally
        {
            // A newer selection owns the flag; do not clear if this open was cancelled.
            if (!ct.IsCancellationRequested)
            {
                await InvokeOnUiAsync(() => IsOpeningPullRequest = false).ConfigureAwait(false);
            }
        }
    }

    [RelayCommand]
    private void SelectFileStatus() => ClearPullRequestMode();

    public void ClearPullRequestMode()
    {
        WorkspaceMode = WorkspaceMode.FileStatus;
        SelectedPullRequest = null;
        SelectedFile = null;
        _session = null;
        PrFiles.Clear();
        FilteredPrFiles.Clear();
        DiffRows.Clear();
        Threads.Clear();
        UnplaceableThreads.Clear();
        FileLevelThreads.Clear();
        DiffAnnotations.Clear();
        PullRequestTitle = null;
        PullRequestSubtitle = null;
        PullRequestBody = null;
        ReviewDecision = null;
        CheckRollupState = null;
        MergeStateSummary = null;
        StatusChecks = [];
        Timeline = [];
        Reviewers.Clear();
        OnPropertyChanged(nameof(HasReviewers));
        MyReviewState = null;
        IsOwnPullRequest = false;
        HeadHasMoved = false;
        HeadMovedBanner = null;
        IsConversationSelected = false;
        FileFilter = "";
        FilterViewed = ViewedFilter.All;
        FilterStale = false;
        FilterCommented = false;
        FilterUnresolved = false;
        OnPropertyChanged(nameof(StatusChecks));
        OnPropertyChanged(nameof(Timeline));
        FileThreadSummary = string.Empty;
        ConversationThreadSummary = string.Empty;
        PendingCommentCount = 0;
        SelectedThread = null;
        SelectedAnnotation = null;
        _allThreads = [];
        LocalNotes = "";
        DiffEmptyMessage = "Select a pull request";
    }

    [RelayCommand]
    private void SelectFile(FileItemViewModel? file) => SelectedFile = file;

    private async Task<ReviewSession?> OpenSessionWithClonePromptAsync(
        PullRequestSummary summary,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _reviewService.OpenAsync(summary, ct).ConfigureAwait(false);
            }
            catch (LocalCloneRequiredException ex)
            {
                var confirmed = await _confirm.ConfirmAsync(
                        "Clone repository?",
                        $"Clone {ex.Owner}/{ex.Name} into the development folder?",
                        "Clone")
                    .ConfigureAwait(false);

                if (!confirmed)
                    return null;

                Directory.CreateDirectory(Path.GetDirectoryName(ex.SuggestedPath)!);
                await _cloneService.CloneAsync(ex.CloneUrl, ex.SuggestedPath, progress: null, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task LoadDiffForSelectionAsync(FileItemViewModel? file)
    {
        _diffCts?.Cancel();
        _diffCts = new CancellationTokenSource();
        var ct = _diffCts.Token;

        if (file is null || _session is null)
        {
            DiffRows.Clear();
            _currentDiff = null;
            DiffEmptyMessage = SelectedPullRequest is null
                ? "Select a pull request"
                : "Select a file to view its diff";
            return;
        }

        IsLoadingDiff = true;
        try
        {
            var options = BuildDiffOptions();
            var diff = await _reviewService
                .GetDiffAsync(_session, file.Path, options, ct)
                .ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                _currentDiff = ApplyIntraLine(diff);
                UpdateDiffStats(_currentDiff);
                ProjectRows(_currentDiff);
                DiffEmptyMessage = DiffRows.Count == 0 ? "No differences" : "";
            });

            await LoadSyntaxTokensAsync(file, _currentDiff!, ct).ConfigureAwait(false);
            await UpdateThreadAnnotationsAsync(file, diff, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer load or a filter rebuild owns the UI; do not overwrite DiffEmptyMessage
            // with "Select a file…" when the same file is still selected.
        }
        catch (DiffTooLargeException ex)
        {
            DiffRows.Clear();
            LeftSyntaxTokens = null;
            RightSyntaxTokens = null;
            DiffEmptyMessage = ex.Message;
        }
        catch (Exception ex)
        {
            DiffRows.Clear();
            LeftSyntaxTokens = null;
            RightSyntaxTokens = null;
            DiffEmptyMessage = $"Failed to load diff: {ex.Message}";
        }
        finally
        {
            IsLoadingDiff = false;
            OnPropertyChanged(nameof(DiffFooterText));
        }
    }

    private async Task ReloadSelectedDiffAsync()
    {
        if (SelectedFile is not null)
            await LoadDiffForSelectionAsync(SelectedFile);
    }

    private async Task RefreshThreadsAsync()
    {
        if (_session is null) return;

        try
        {
            var threads = await _comments.GetThreadsAsync(_session).ConfigureAwait(false);
            _allThreads = threads;
            await InvokeOnUiAsync(() =>
            {
                Threads.Clear();
                foreach (var thread in threads)
                {
                    Threads.Add(new ReviewThreadViewModel(
                        thread.NodeId,
                        thread.Path,
                        thread.IsResolved,
                        thread.Comments.Select(c => c.Body).ToList()));
                }

                foreach (var file in PrFiles)
                    file.UnresolvedThreadCount = CountUnresolvedThreads(file.Path.Value);

                UpdateFileThreadFlags();
                ApplyOptimisticThreadsOverlay();
                UpdateProgressSummary();
            });
        }
        catch (Exception) when (_outbox.IsOffline)
        {
            IsOffline = true;
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to refresh threads: {ex.Message}");
        }

        ApplyPrFileFilter();
    }

    private async Task OnOutboxDrainCompletedAsync()
    {
        IsOffline = _outbox.IsOffline;
        await RefreshThreadsAsync().ConfigureAwait(false);
        await ApplyViewedStateAsync().ConfigureAwait(false);
        await RefreshPendingCommentCountAsync().ConfigureAwait(false);
    }

    private async Task RefreshPendingCommentCountAsync()
    {
        if (_session is null)
        {
            await InvokeOnUiAsync(() => PendingCommentCount = 0).ConfigureAwait(false);
            return;
        }

        var summary = _session.Detail.Summary;
        var remoteCount = 0;
        try
        {
            remoteCount = await _pullRequests.GetPendingReviewCommentCountAsync(
                    summary.Host,
                    summary.AccountLogin,
                    summary.Owner,
                    summary.Name,
                    summary.Number)
                .ConfigureAwait(false);
        }
        catch
        {
            // Keep local outbox count if the network call fails.
        }

        var pending = await _outbox.ListPendingAsync(summary.NodeId).ConfigureAwait(false);
        var localCount = pending.Count(e => e.Kind == OutboxKind.AddComment);

        await InvokeOnUiAsync(() => PendingCommentCount = remoteCount + localCount)
            .ConfigureAwait(false);
    }

    private async Task ApplyViewedStateAsync()
    {
        if (_session is null) return;

        var prNodeId = _session.Detail.Summary.NodeId;
        var localViewed = await _durableStore.ListAsync(prNodeId).ConfigureAwait(false);
        var localSet = localViewed.Select(v => v.Path).ToHashSet(StringComparer.Ordinal);

        await InvokeOnUiAsync(() =>
        {
            foreach (var file in PrFiles)
            {
                var pending = _pending.FirstOrDefault(p =>
                    p.Kind == PendingReviewMutationKind.ToggleViewed && p.ClientId == file.Path.Value);
                if (pending?.TargetViewed is bool target)
                {
                    file.IsViewed = target;
                    file.IsViewedPending = true;
                    continue;
                }

                file.IsViewed = localSet.Contains(file.Path.Value);
                file.IsViewedPending = false;
            }

            UpdateProgressSummary();
        });
    }

    private void ApplyOptimisticThreads(string body, string clientId)
    {
        InvokeOnUiAsync(() =>
        {
            var thread = new ReviewThreadViewModel(clientId, SelectedFile!.Path.Value, isResolved: false, [body])
            {
                IsPending = true,
            };
            Threads.Insert(0, thread);
        }).GetAwaiter().GetResult();
    }

    private void ApplyOptimisticThreadsOverlay()
    {
        foreach (var pending in _pending.Where(p => p.Kind == PendingReviewMutationKind.AddComment))
        {
            if (Threads.Any(t => t.NodeId == pending.ClientId))
                continue;
            if (SelectedFile is null) continue;
            Threads.Insert(0, new ReviewThreadViewModel(pending.ClientId, SelectedFile.Path.Value, false, ["…"])
            {
                IsPending = true,
            });
        }

        foreach (var pending in _pending.Where(p => p.Kind == PendingReviewMutationKind.ResolveThread))
        {
            var thread = Threads.FirstOrDefault(t => t.NodeId == pending.ClientId);
            if (thread is not null)
                thread.IsResolved = !thread.IsResolved;
        }
    }

    private async Task SaveLocalNotesAsync(string markdown)
    {
        if (_session is null) return;
        try
        {
            await _durableStore.SetNoteAsync(_session.Detail.Summary.NodeId, markdown).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to save notes: {ex.Message}");
        }
    }

    private async Task LoadSyntaxTokensAsync(FileItemViewModel file, FileDiff diff, CancellationToken ct)
    {
        if (_syntaxTokens is null || _session is null)
        {
            await InvokeOnUiAsync(() =>
            {
                LeftSyntaxTokens = null;
                RightSyntaxTokens = null;
            });
            return;
        }

        try
        {
            FileSyntaxTokens? left = null;
            FileSyntaxTokens? right = null;

            if (!diff.OldContent.IsEmpty)
            {
                var bytes = await _objects.ReadBlobAsync(_session.RepositoryPath, diff.OldContent, ct)
                    .ConfigureAwait(false);
                var text = DecodeUtf8(bytes);
                if (text is not null)
                {
                    left = await _syntaxTokens.TokeniseAsync(diff.OldContent, file.Path, text, ct)
                        .ConfigureAwait(false);
                }
            }

            if (!diff.NewContent.IsEmpty)
            {
                var bytes = await _objects.ReadBlobAsync(_session.RepositoryPath, diff.NewContent, ct)
                    .ConfigureAwait(false);
                var text = DecodeUtf8(bytes);
                if (text is not null)
                {
                    right = await _syntaxTokens.TokeniseAsync(diff.NewContent, file.Path, text, ct)
                        .ConfigureAwait(false);
                }
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
            await InvokeOnUiAsync(() =>
            {
                LeftSyntaxTokens = null;
                RightSyntaxTokens = null;
            });
        }
    }

    private static string? DecodeUtf8(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private DiffOptions BuildDiffOptions()
    {
        var baseOptions = _settings.Current.ToDiffOptions() with
        {
            IgnoreAllSpace = IgnoreWhitespace,
            ContextLines = ShowFullFile ? 100_000 : Math.Max(1, ContextLines),
        };
        return baseOptions;
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
        const int collapseThreshold = 8;
        var threshold = ShowFullFile ? 0 : collapseThreshold;
        IReadOnlyList<DiffRow> rows = ViewMode == DiffViewMode.SideBySide
            ? SideBySideRowProjector.Project(diff, threshold, _intraLine, new HashSet<(int, int)>())
            : UnifiedRowProjector.Project(diff, threshold, _intraLine, new HashSet<(int, int)>());
        foreach (var row in rows)
            DiffRows.Add(row);
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

    private async Task UpdateThreadAnnotationsAsync(
        FileItemViewModel file,
        FileDiff diff,
        CancellationToken ct)
    {
        if (_session is null)
            return;

        var resolved = await _comments.ResolveAnchorsAsync(_session, _allThreads, file.Path, diff, ct)
            .ConfigureAwait(false);

        var placeable = resolved.Where(t => !t.IsUnplaceable && !t.IsFileLevel && t.Anchor is not null).ToList();
        var unplaceable = resolved.Where(t => t.IsUnplaceable).ToList();
        var fileLevel = resolved.Where(t => t.IsFileLevel).ToList();

        await InvokeOnUiAsync(() =>
        {
            DiffAnnotations.Clear();
            UnplaceableThreads.Clear();
            FileLevelThreads.Clear();

            foreach (var thread in placeable)
                DiffAnnotations.Add(new ReviewThreadAnnotation(thread));

            foreach (var thread in unplaceable)
                UnplaceableThreads.Add(thread);

            foreach (var thread in fileLevel)
                FileLevelThreads.Add(thread);

            FileThreadSummary = string.Empty;

            file.UnresolvedThreadCount = CountUnresolvedThreads(file.Path.Value);
            UpdateConversationThreadSummary();
        });
    }

    private int CountUnresolvedThreads(string path) =>
        _allThreads.Count(t =>
            string.Equals(t.Path, path, StringComparison.Ordinal) &&
            !t.IsResolved);

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

            if (Avalonia.Application.Current is null)
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

    private sealed record PendingReviewMutation(
        PendingReviewMutationKind Kind,
        string ClientId,
        bool? TargetViewed = null);

    private enum PendingReviewMutationKind
    {
        AddComment,
        ToggleViewed,
        ResolveThread,
    }
}

public partial class ReviewThreadViewModel : ObservableObject
{
    public ReviewThreadViewModel(string nodeId, string path, bool isResolved, IReadOnlyList<string> commentBodies)
    {
        NodeId = nodeId;
        Path = path;
        IsResolved = isResolved;
        CommentBodies = commentBodies;
    }

    public string NodeId { get; }
    public string Path { get; }
    public IReadOnlyList<string> CommentBodies { get; }
    [ObservableProperty] private bool _isResolved;
    [ObservableProperty] private bool _isPending;
}
