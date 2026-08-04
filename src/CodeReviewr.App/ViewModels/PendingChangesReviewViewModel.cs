using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.App.Controls;
using CodeReviewr.App.Services;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.AI;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.App.ViewModels;

/// <summary>
/// Provides the host context <see cref="PendingChangesReviewViewModel"/> needs from
/// <c>WorkingCopyViewModel</c> without creating a dependency from Core/App view-model layers back
/// onto the concrete File Status implementation.
/// </summary>
public interface IPendingChangesReviewHost
{
    /// <summary>Absolute path to the open repository, or null when no repository is open.</summary>
    string? RepositoryPath { get; }

    /// <summary>Stable key identifying this repository for AI caching / exclusion settings (a normalized absolute path).</summary>
    string RepositoryKey { get; }

    /// <summary>Currently selected File Status file, if any.</summary>
    FileItemViewModel? SelectedFile { get; }

    /// <summary>The diff currently presented for <see cref="SelectedFile"/>, if any.</summary>
    FileDiff? CurrentDiff { get; }

    /// <summary>Staged + unstaged + conflicted files, unique by path.</summary>
    IReadOnlyList<FileItemViewModel> PendingFiles { get; }

    int StagedCount { get; }

    int UnstagedCount { get; }

    /// <summary>Builds AI changed-file facts (paths + blob oids) for the given review scope.</summary>
    IReadOnlyList<AiChangedFileFact> BuildChangedFileFacts(AiReviewScope scope);

    /// <summary>Best-effort HEAD commit SHA, or null for a repository with no commits yet.</summary>
    Task<string?> TryGetHeadCommitShaAsync(CancellationToken ct = default);

    /// <summary>Selects the given path in the host's file list (switching to File Status mode if needed) and awaits the diff load.</summary>
    Task SelectFileAsync(FilePath path);

    /// <summary>Clears the File Status file selection and ListBox highlights (used when opening the Comments pane).</summary>
    void ClearFileSelection();
}

/// <summary>
/// PR-parity AI review plus local-only (non-GitHub) comments for File Status / pending changes.
/// Composed into <c>WorkingCopyViewModel</c> and exposed as <c>WorkingCopy.PendingReview</c>.
/// </summary>
public partial class PendingChangesReviewViewModel : ObservableObject
{
    private readonly IAIReviewService _ai;
    private readonly ILocalCommentStore _localComments;
    private readonly ISettingsStore _settings;
    private readonly IConfirmDialog _confirm;
    private readonly NotificationService _notifications;

    private IPendingChangesReviewHost? _host;
    private IDisposable? _aiProgressSubscription;
    private IDisposable? _aiActivityLogSubscription;
    private CancellationTokenSource? _aiFileCts;

    /// <summary>Last file/diff identity bound into annotations — used to skip wipe/reload on same-content revalidate.</summary>
    private string? _boundSelectionPath;
    private string? _boundOldContentId;
    private string? _boundNewContentId;
    private bool _suppressCommentsDeselect;

    public PendingChangesReviewViewModel(
        IAIReviewService ai,
        ILocalCommentStore localComments,
        ISettingsStore settings,
        IConfirmDialog confirm,
        NotificationService notifications)
    {
        _ai = ai;
        _localComments = localComments;
        _settings = settings;
        _confirm = confirm;
        _notifications = notifications;
    }

    /// <summary>Raised after a triage result is applied to the host's files so the host can rebuild AI-suggested layout.</summary>
    public event Action? TriageApplied;

    /// <summary>Raised when the comment draft should receive keyboard focus.</summary>
    public event Action? FocusCommentDraftRequested;

    /// <summary>Raised on the UI thread when <see cref="AiActivityLog"/> grows so the dialog can auto-scroll.</summary>
    public event Action? AiActivityLogUpdated;

    /// <summary>Raised when the expanded local-comment card should reposition (dot click / navigate).</summary>
    public event Action? ExpandedLocalCommentChanged;

    /// <summary>Raised after a local comment is selected so the view can scroll the DiffViewer to its line.</summary>
    public event Action? RequestScrollToSelectedAnnotation;

    public ObservableCollection<IDiffAnnotation> DiffAnnotations { get; } = [];
    public ObservableCollection<AiImportantFileItem> AiImportantFiles { get; } = [];
    public ObservableCollection<AiChatMessage> AiChatMessages { get; } = [];
    public ObservableCollection<LocalCommentItemViewModel> LocalComments { get; } = [];

    public void AttachHost(IPendingChangesReviewHost host)
    {
        _host = host;
        OnPropertyChanged(nameof(SessionKey));
        OnPropertyChanged(nameof(RepositoryKey));
        NotifyAiButtonStateChanged();
    }

    /// <summary>Clears all AI/comment state; call when switching to a different repository.</summary>
    public void ResetState()
    {
        _aiProgressSubscription?.Dispose();
        _aiProgressSubscription = null;
        _aiActivityLogSubscription?.Dispose();
        _aiActivityLogSubscription = null;
        _aiFileCts?.Cancel();
        _aiFileCts = null;

        AiRunState = AiRunState.Idle;
        AiProgress = null;
        AiTriage = null;
        AiImportantFiles.Clear();
        AiFileSummary = null;
        AiFileBandExpanded = true;
        AiFileQuestion = "";
        AiFileAnswer = null;
        AiAdHocInstructions = "";
        AiLastError = null;
        AiActivityLog = "";
        AiCopilotSessionId = null;
        AiReviewFinishedUtc = null;
        ShowAiProgressDialog = false;
        ShowAiInstructionsDialog = false;
        ShowAiChat = false;
        AiChatInput = "";
        AiChatMessages.Clear();
        DiffAnnotations.Clear();
        LocalComments.Clear();
        NewCommentBody = "";
        SelectedAnnotation = null;
        ClearDraftCommentAnchorCore();
        ExpandedFileLevelComment = null;
        IsCommentsSelected = false;
        OnPropertyChanged(nameof(HasAiImportantFiles));
        OnPropertyChanged(nameof(CanClearAiChat));
        NotifyCommentCountsChanged();
    }

    // ---------------------------------------------------------------------
    // Identity.
    // ---------------------------------------------------------------------

    /// <summary>The repository key reported by the host (a normalized absolute path).</summary>
    public string RepositoryKey => _host?.RepositoryKey ?? "(no repository)";

    /// <summary>AI session key for pending changes — distinct from any pull-request session for the same repository.</summary>
    public string SessionKey => $"local:{RepositoryKey}";

    // ---------------------------------------------------------------------
    // AI review surface (mirrors ReviewViewModel's Phase 3 properties/commands).
    // ---------------------------------------------------------------------

    [ObservableProperty] private AiRunState _aiRunState = AiRunState.Idle;
    [ObservableProperty] private AiRunProgress? _aiProgress;
    [ObservableProperty] private AiPrTriageResult? _aiTriage;
    [ObservableProperty] private bool _aiReviewSectionExpanded = true;
    [ObservableProperty] private string _aiAdHocInstructions = "";
    [ObservableProperty] private bool _showAiProgressDialog;
    [ObservableProperty] private bool _showAiInstructionsDialog;
    [ObservableProperty] private AiFileSummaryResult? _aiFileSummary;
    [ObservableProperty] private bool _aiFileBandExpanded = true;
    [ObservableProperty] private string _aiFileQuestion = "";
    [ObservableProperty] private string? _aiFileAnswer;
    [ObservableProperty] private string _aiChatInput = "";
    [ObservableProperty] private bool _showAiChat;
    [ObservableProperty] private bool _isAiChatBusy;
    [ObservableProperty] private bool _aiShowDismissedAnnotations;
    [ObservableProperty] private string? _aiLastError;
    [ObservableProperty] private string? _aiCopilotSessionId;
    [ObservableProperty] private DateTimeOffset? _aiReviewFinishedUtc;
    [ObservableProperty] private string _aiActivityLog = "";

    /// <summary>Review scope for the working-copy AI request: all pending changes, or staged only.</summary>
    [ObservableProperty] private AiReviewScope _selectedReviewScope = AiReviewScope.WorkingCopyAll;

    /// <summary>The annotation currently selected in the diff gutter (AI insight or local comment marker).</summary>
    [ObservableProperty] private IDiffAnnotation? _selectedAnnotation;

    public bool IsScopeStaged => SelectedReviewScope == AiReviewScope.WorkingCopyStaged;
    public bool IsScopeAll => SelectedReviewScope == AiReviewScope.WorkingCopyAll;

    public AiLineAnnotation? SelectedAiAnnotation => SelectedAnnotation as AiLineAnnotation;

    public LocalLineCommentAnnotation? SelectedLocalCommentAnnotation =>
        SelectedAnnotation as LocalLineCommentAnnotation;

    /// <summary>File-level local comment opened from the Comments pane (no gutter annotation).</summary>
    [ObservableProperty] private LocalCommentItemViewModel? _expandedFileLevelComment;

    public LocalCommentItemViewModel? SelectedLocalCommentItem
    {
        get
        {
            if (ExpandedFileLevelComment is not null)
                return ExpandedFileLevelComment;
            if (SelectedLocalCommentAnnotation is { } ann)
                return LocalComments.FirstOrDefault(c =>
                    string.Equals(c.Id, ann.Record.Id, StringComparison.Ordinal));
            return null;
        }
    }

    public bool HasExpandedAiAnnotation => SelectedAiAnnotation is not null && !HasDraftCommentAnchor;

    public bool HasExpandedLocalComment =>
        (SelectedLocalCommentAnnotation is not null || ExpandedFileLevelComment is not null)
        && !HasDraftCommentAnchor;

    [RelayCommand]
    private void SetReviewScope(AiReviewScope scope) => SelectedReviewScope = scope;

    partial void OnSelectedReviewScopeChanged(AiReviewScope value)
    {
        OnPropertyChanged(nameof(IsScopeStaged));
        OnPropertyChanged(nameof(IsScopeAll));
    }

    partial void OnSelectedAnnotationChanged(IDiffAnnotation? value)
    {
        if (value is not null)
            ExpandedFileLevelComment = null;

        OnPropertyChanged(nameof(SelectedAiAnnotation));
        OnPropertyChanged(nameof(HasExpandedAiAnnotation));
        OnPropertyChanged(nameof(SelectedLocalCommentAnnotation));
        OnPropertyChanged(nameof(SelectedLocalCommentItem));
        OnPropertyChanged(nameof(HasExpandedLocalComment));
        ExpandedLocalCommentChanged?.Invoke();
    }

    partial void OnExpandedFileLevelCommentChanged(LocalCommentItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedLocalCommentItem));
        OnPropertyChanged(nameof(HasExpandedLocalComment));
        ExpandedLocalCommentChanged?.Invoke();
    }

    [RelayCommand]
    private void ClearSelectedAnnotation()
    {
        SelectedAnnotation = null;
        ExpandedFileLevelComment = null;
    }

    public bool HasAiTriage => AiTriage is not null;
    public bool HasAiImportantFiles => AiImportantFiles.Count > 0;
    public bool IsAiRunActive => AiRunState == AiRunState.Running;
    public bool CanResumeAiReview => AiRunState is AiRunState.Incomplete or AiRunState.PausedBudget;
    public bool CanRerunAiReview => AiRunState is AiRunState.Complete or AiRunState.Failed;
    public bool HasAiFileSummary => AiFileSummary is not null;
    public bool ShowAiFileBand =>
        !string.IsNullOrWhiteSpace(SelectedFileAiGuidance) || AiFileSummary is not null;
    public bool HasAiFileAnswer => !string.IsNullOrWhiteSpace(AiFileAnswer);
    public bool CanSendAiChat =>
        !string.IsNullOrWhiteSpace(AiChatInput) && !IsAiRunActive && !IsAiChatBusy;
    public bool CanClearAiChat => _host is not null && AiChatMessages.Count > 0;

    public string AiChatSelectedFileLabel =>
        _host?.SelectedFile is null ? "No file selected" : _host.SelectedFile.Path.Value;

    public string AiChatPlaceholder =>
        _host?.SelectedFile is null
            ? "Ask about your pending changes…"
            : $"Ask about {_host.SelectedFile.Name}…";

    public string? SelectedFileAiGuidance
    {
        get
        {
            var guidance = _host?.SelectedFile?.AiGuidance;
            return string.IsNullOrWhiteSpace(guidance) ? null : guidance;
        }
    }

    public Material.Icons.MaterialIconKind AiFileBandChevron =>
        AiFileBandExpanded
            ? Material.Icons.MaterialIconKind.ChevronDown
            : Material.Icons.MaterialIconKind.ChevronRight;

    public bool IsRepositoryExcludedFromAi =>
        _host is not null &&
        _settings.Current.AiExcludedRepositories.Contains(RepositoryKey, StringComparer.OrdinalIgnoreCase);

    public string AiRiskBadgeText => AiTriage?.Risk switch
    {
        AiRiskLevel.Low => "LOW RISK",
        AiRiskLevel.Medium => "MEDIUM RISK",
        AiRiskLevel.High => "HIGH RISK",
        AiRiskLevel.Critical => "CRITICAL RISK",
        _ => "",
    };

    public string? AiMeasuredFactsText => AiTriage is { Measured: { } measured }
        ? $"{measured.FilesChanged} files changed, +{measured.LinesAdded} -{measured.LinesRemoved} lines"
        : null;

    public string AiProgressText
    {
        get
        {
            var progress = AiProgress;
            if (progress is null) return "";

            var stage = progress.Stage.ToString();
            var elapsed = progress.Elapsed < TimeSpan.FromHours(1)
                ? progress.Elapsed.ToString(@"mm\:ss")
                : progress.Elapsed.ToString(@"h\:mm\:ss");
            var turns = progress.TurnBudget is > 0
                ? $"{progress.TurnsUsed}/{progress.TurnBudget} turns"
                : $"{progress.TurnsUsed} turns";
            var files = progress.FilesTotal > 0
                ? $" · {progress.FilesCompleted}/{progress.FilesTotal} files"
                : "";
            var message = string.IsNullOrWhiteSpace(progress.Message) ? "" : $" — {progress.Message}";
            return $"{stage} · {elapsed} · {turns}{files}{message}";
        }
    }

    public string AiStatusDialogTitle => AiRunState switch
    {
        AiRunState.Running => "AI review in progress",
        AiRunState.Complete => "AI review complete",
        AiRunState.Failed => "AI review failed",
        AiRunState.Incomplete => "AI review incomplete",
        AiRunState.PausedBudget => "AI review paused",
        _ => "AI review status",
    };

    public string AiDiagnosticsText
    {
        get
        {
            var settings = _settings.Current;
            var runTimeout = settings.AiRunTimeoutSeconds <= 0
                ? "unlimited"
                : $"{settings.AiRunTimeoutSeconds}s";
            var lines = new List<string>
            {
                $"State: {AiRunState}",
                $"Turn idle timeout: {settings.AiTurnTimeoutSeconds}s",
                $"Run timeout: {runTimeout}",
                $"Turn budget: {settings.AiTurnBudget}",
            };

            if (AiProgress is { } progress)
            {
                lines.Add($"Stage: {progress.Stage}");
                lines.Add($"Elapsed: {progress.Elapsed:g}");
                lines.Add($"Turns used: {progress.TurnsUsed}");
                if (!string.IsNullOrWhiteSpace(progress.Message))
                    lines.Add($"Progress: {progress.Message}");
            }

            if (!string.IsNullOrWhiteSpace(AiCopilotSessionId))
                lines.Add($"Copilot session: {AiCopilotSessionId}");

            if (!string.IsNullOrWhiteSpace(AiLastError))
                lines.Add($"Error: {AiLastError}");

            if (!string.IsNullOrWhiteSpace(AiActivityLog))
            {
                lines.Add("");
                lines.Add("--- Activity log ---");
                lines.Add(AiActivityLog.TrimEnd());
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasAiDiagnostics =>
        !string.IsNullOrWhiteSpace(AiLastError) ||
        !string.IsNullOrWhiteSpace(AiActivityLog) ||
        AiRunState is AiRunState.Failed or AiRunState.Incomplete or AiRunState.Running;

    public string AiButtonLabel => AiRunState switch
    {
        AiRunState.Running => "Reviewing…",
        AiRunState.Incomplete or AiRunState.PausedBudget => "Resume AI review",
        AiRunState.Complete => "Re-run AI review",
        AiRunState.Failed => "Retry AI review",
        _ => "AI review",
    };

    public bool AiButtonEnabled =>
        _host is not null &&
        _settings.Current.AiAssistanceEnabled &&
        !IsRepositoryExcludedFromAi;

    public string AiButtonTooltip
    {
        get
        {
            if (_host is null) return "Open a repository first";
            if (!_settings.Current.AiAssistanceEnabled) return "Enable AI assistance in Settings → AI";
            if (IsRepositoryExcludedFromAi) return "This repository is excluded from AI assistance";
            if (IsAiRunActive) return "Show AI review status";
            return "Run an AI-assisted review of your pending changes";
        }
    }

    /// <summary>Recomputes AI button bindings after settings or repository context change.</summary>
    public void NotifyAiButtonStateChanged()
    {
        _ = InvokeOnUiAsync(() =>
        {
            OnPropertyChanged(nameof(AiButtonLabel));
            OnPropertyChanged(nameof(AiButtonEnabled));
            OnPropertyChanged(nameof(AiButtonTooltip));
            OnPropertyChanged(nameof(IsRepositoryExcludedFromAi));
            OnPropertyChanged(nameof(AiStatusDialogTitle));
            OnPropertyChanged(nameof(AiDiagnosticsText));
            OnPropertyChanged(nameof(HasAiDiagnostics));
            RequestAiReviewCommand.NotifyCanExecuteChanged();
        });
    }

    partial void OnAiRunStateChanged(AiRunState value)
    {
        OnPropertyChanged(nameof(IsAiRunActive));
        OnPropertyChanged(nameof(CanResumeAiReview));
        OnPropertyChanged(nameof(CanRerunAiReview));
        OnPropertyChanged(nameof(CanSendAiChat));
        OnPropertyChanged(nameof(AiStatusDialogTitle));
        OnPropertyChanged(nameof(HasAiDiagnostics));
        NotifyAiButtonStateChanged();
    }

    partial void OnAiProgressChanged(AiRunProgress? value)
    {
        OnPropertyChanged(nameof(AiProgressText));
        OnPropertyChanged(nameof(AiDiagnosticsText));
    }

    partial void OnAiActivityLogChanged(string value)
    {
        OnPropertyChanged(nameof(AiDiagnosticsText));
        OnPropertyChanged(nameof(HasAiDiagnostics));
        AiActivityLogUpdated?.Invoke();
    }

    partial void OnAiLastErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(AiDiagnosticsText));
        OnPropertyChanged(nameof(HasAiDiagnostics));
    }

    partial void OnAiCopilotSessionIdChanged(string? value) => OnPropertyChanged(nameof(AiDiagnosticsText));

    partial void OnAiTriageChanged(AiPrTriageResult? value)
    {
        AiReviewSessionHelpers.RebuildImportantFiles(value, AiImportantFiles);
        OnPropertyChanged(nameof(HasAiTriage));
        OnPropertyChanged(nameof(AiRiskBadgeText));
        OnPropertyChanged(nameof(AiMeasuredFactsText));
        OnPropertyChanged(nameof(HasAiImportantFiles));
        NotifyAiFileBandChanged();
    }

    partial void OnAiFileSummaryChanged(AiFileSummaryResult? value)
    {
        OnPropertyChanged(nameof(HasAiFileSummary));
        NotifyAiFileBandChanged();
    }

    partial void OnAiFileBandExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(AiFileBandChevron));

    partial void OnAiFileAnswerChanged(string? value) => OnPropertyChanged(nameof(HasAiFileAnswer));

    partial void OnAiChatInputChanged(string value) => OnPropertyChanged(nameof(CanSendAiChat));

    partial void OnIsAiChatBusyChanged(bool value) => OnPropertyChanged(nameof(CanSendAiChat));

    [RelayCommand]
    private void ToggleAiReviewSection() => AiReviewSectionExpanded = !AiReviewSectionExpanded;

    [RelayCommand]
    private void ToggleAiFileBand() => AiFileBandExpanded = !AiFileBandExpanded;

    [RelayCommand]
    private async Task SelectAiImportantFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || _host is null)
            return;

        await _host.SelectFileAsync(FilePath.From(path)).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(AiButtonEnabled))]
    private async Task RequestAiReviewAsync()
    {
        if (_host is null || !AiButtonEnabled)
            return;

        if (IsAiRunActive)
        {
            ShowAiProgressDialog = true;
            return;
        }

        if (!_settings.Current.AiDisclosureAcknowledged)
        {
            var acknowledged = await _confirm.ConfirmAsync(
                    "Send code to GitHub Copilot?",
                    "AI review sends your pending changes' files and metadata to GitHub Copilot. " +
                    "Nothing is sent unless you start a review.",
                    "I understand, continue")
                .ConfigureAwait(false);
            if (!acknowledged)
                return;

            _settings.Update(s => s.AiDisclosureAcknowledged = true);
            await _settings.SaveAsync().ConfigureAwait(false);
        }

        var fileCount = _host.PendingFiles.Count;
        if (fileCount > _settings.Current.AiLargePrFileThreshold)
        {
            var proceed = await _confirm.ConfirmAsync(
                    "Large set of pending changes",
                    $"You have {fileCount} changed files, which may take a while and use " +
                    "a large turn budget. Continue?",
                    "Start review")
                .ConfigureAwait(false);
            if (!proceed)
                return;
        }

        SelectedReviewScope = _host.UnstagedCount > 0 ? AiReviewScope.WorkingCopyAll : AiReviewScope.WorkingCopyStaged;
        AiAdHocInstructions = "";
        ShowAiInstructionsDialog = true;
    }

    [RelayCommand]
    private void CancelAiInstructions() => ShowAiInstructionsDialog = false;

    [RelayCommand]
    private void DismissAiProgressDialog() => ShowAiProgressDialog = false;

    [RelayCommand]
    private async Task ConfirmStartAiReviewAsync()
    {
        ShowAiInstructionsDialog = false;
        await StartAiReviewAsync(discardCached: false, resume: false).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ResumeAiReviewAsync()
    {
        if (!CanResumeAiReview) return;
        await StartAiReviewAsync(discardCached: false, resume: true).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RerunAiReviewAsync()
    {
        if (!CanRerunAiReview) return;
        await StartAiReviewAsync(discardCached: true, resume: false).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task CancelAiReviewAsync()
    {
        if (_host is null) return;
        try
        {
            await _ai.CancelAsync(RepositoryKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to cancel AI review: {ex.Message}", exception: ex);
        }
    }

    private async Task StartAiReviewAsync(bool discardCached, bool resume)
    {
        if (_host is null) return;
        var host = _host;
        var repositoryKey = RepositoryKey;

        _aiProgressSubscription?.Dispose();
        _aiActivityLogSubscription?.Dispose();
        _aiProgressSubscription = _ai.ObserveProgress(repositoryKey, progress =>
            _ = InvokeOnUiAsync(() => AiProgress = progress));
        _aiActivityLogSubscription = _ai.ObserveActivityLog(repositoryKey, line =>
            _ = InvokeOnUiAsync(() =>
            {
                AiActivityLog = string.IsNullOrEmpty(AiActivityLog)
                    ? line
                    : AiActivityLog + Environment.NewLine + line;
            }));

        await InvokeOnUiAsync(() =>
        {
            AiLastError = null;
            AiActivityLog = "";
            AiRunState = AiRunState.Running;
            ShowAiProgressDialog = true;
        }).ConfigureAwait(false);

        try
        {
            var request = await BuildAiReviewRequestAsync(host, discardCached, resume).ConfigureAwait(false);
            var snapshot = await _ai.StartReviewAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (!ReferenceEquals(_host, host)) return;

            await InvokeOnUiAsync(() => ApplyAiRunSnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_host, host)) return;
            await InvokeOnUiAsync(() =>
            {
                AiRunState = AiRunState.Failed;
                AiLastError = ex.Message;
                ShowAiProgressDialog = true;
                _notifications.Error(
                    $"AI review failed: {ex.Message}",
                    () => _ = StartAiReviewAsync(discardCached, resume),
                    ex,
                    detail: AiDiagnosticsText);
            }).ConfigureAwait(false);
        }
    }

    private async Task<AiReviewRequest> BuildAiReviewRequestAsync(
        IPendingChangesReviewHost host, bool discardCached, bool resume)
    {
        var facts = host.BuildChangedFileFacts(SelectedReviewScope);
        var headSha = await host.TryGetHeadCommitShaAsync().ConfigureAwait(false) ?? "";

        return new AiReviewRequest(
            SessionKey: SessionKey,
            RepositoryPath: host.RepositoryPath ?? "",
            RepositoryKey: host.RepositoryKey,
            HeadSha: "", // The coordinator fills this with the materialised tree OID for WC scopes.
            MergeBaseSha: headSha,
            Title: "Pending changes",
            Body: null,
            Author: null,
            BaseBranch: null,
            HeadBranch: null,
            ChangedFiles: facts,
            AdHocInstructions: string.IsNullOrWhiteSpace(AiAdHocInstructions) ? null : AiAdHocInstructions.Trim(),
            DiscardCached: discardCached,
            Resume: resume,
            Scope: SelectedReviewScope);
    }

    private void ApplyAiRunSnapshot(AiRunSnapshot snapshot)
    {
        AiRunState = snapshot.State;
        AiCopilotSessionId = snapshot.CopilotSessionId;
        AiLastError = snapshot.ErrorMessage;
        AiReviewFinishedUtc = snapshot.FinishedUtc ?? snapshot.StartedUtc;
        ApplyAiTriageFields(snapshot.Triage);

        if (snapshot.State == AiRunState.Complete)
            ShowAiProgressDialog = false;
        else if (snapshot.State is AiRunState.Failed or AiRunState.Incomplete)
            ShowAiProgressDialog = true;

        if ((snapshot.State is AiRunState.Failed or AiRunState.Incomplete) &&
            !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            _notifications.Error(
                $"AI review: {snapshot.ErrorMessage}",
                detail: AiDiagnosticsText);
        }

        NotifyAiFileBandChanged();
        TriageApplied?.Invoke();
    }

    private void ApplyAiTriageFields(AiPrTriageResult? triage)
    {
        AiTriage = triage;
        if (_host is not null)
            AiReviewSessionHelpers.ApplyTriageToFiles(triage, _host.PendingFiles);
    }

    /// <summary>Re-applies the cached triage onto the host's current file instances (call after RebuildFileLists).</summary>
    public void ReapplyTriageToFiles()
    {
        if (_host is null || AiTriage is null)
            return;

        AiReviewSessionHelpers.ApplyTriageToFiles(AiTriage, _host.PendingFiles);
        UpdateFileUnresolvedCommentCounts();
        NotifyAiFileBandChanged();
    }

    /// <summary>Pushes per-file unresolved local-comment counts onto the host's file list rows.</summary>
    public void UpdateFileUnresolvedCommentCounts()
    {
        if (_host is null)
            return;

        var counts = LocalComments
            .Where(c => !c.IsResolved)
            .GroupBy(c => c.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var file in _host.PendingFiles)
            file.UnresolvedThreadCount = counts.TryGetValue(file.Path.Value, out var n) ? n : 0;
    }

    private void NotifyAiFileBandChanged()
    {
        OnPropertyChanged(nameof(SelectedFileAiGuidance));
        OnPropertyChanged(nameof(ShowAiFileBand));
        OnPropertyChanged(nameof(HasAiFileSummary));
        OnPropertyChanged(nameof(AiChatSelectedFileLabel));
        OnPropertyChanged(nameof(AiChatPlaceholder));
    }

    /// <summary>Hydrates the latest completed/failed run from durable cache without starting a live agent session.</summary>
    public async Task LoadCachedAiRunAsync(CancellationToken ct = default)
    {
        if (_host is null || !_settings.Current.AiAssistanceEnabled)
            return;

        var host = _host;
        try
        {
            var cached = await _ai.GetCachedRunAsync(SessionKey, ct).ConfigureAwait(false);
            if (cached is null || ct.IsCancellationRequested || !ReferenceEquals(_host, host))
                return;

            var request = await BuildAiReviewRequestAsync(host, discardCached: false, resume: false)
                .ConfigureAwait(false);
            await _ai.AttachCachedRunAsync(request, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || !ReferenceEquals(_host, host))
                return;

            await InvokeOnUiAsync(() => ApplyAiRunSnapshot(cached)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Cached-run lookup is best-effort and must never block opening the repository.
        }
    }

    [RelayCommand]
    private async Task AskFileQuestionAsync()
    {
        if (_host?.SelectedFile is not { } file || string.IsNullOrWhiteSpace(AiFileQuestion))
            return;

        var question = AiFileQuestion.Trim();
        try
        {
            var answer = await _ai
                .AskAsync(new AiQuestionRequest(SessionKey, file.Path.Value, question))
                .ConfigureAwait(false);
            await InvokeOnUiAsync(() => AiFileAnswer = answer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await InvokeOnUiAsync(() => AiFileAnswer = $"Error: {ex.Message}").ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void InsertAiAnnotationAsComment(AiAnnotationResult? annotation)
    {
        if (annotation is null || _host?.SelectedFile is null)
            return;

        BeginLineComment(new LineCommentRequest(
            annotation.Side,
            annotation.EndLine,
            annotation.StartLine != annotation.EndLine ? annotation.StartLine : null));
        NewCommentBody = annotation.Body;
    }

    [RelayCommand]
    private async Task DismissAiAnnotationAsync(AiAnnotationResult? annotation)
    {
        if (annotation is null) return;
        await SetAiAnnotationReadStateAsync(annotation, AiAnnotationReadState.Dismissed).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task MarkAiAnnotationReadAsync(AiAnnotationResult? annotation)
    {
        if (annotation is null || annotation.ReadState != AiAnnotationReadState.Unread) return;
        await SetAiAnnotationReadStateAsync(annotation, AiAnnotationReadState.Read).ConfigureAwait(false);
    }

    private async Task SetAiAnnotationReadStateAsync(AiAnnotationResult annotation, AiAnnotationReadState state)
    {
        try
        {
            await _ai.SetAnnotationReadStateAsync(annotation.Id, state).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; the overlay still updates locally below.
        }

        await InvokeOnUiAsync(() =>
        {
            var existing = DiffAnnotations.OfType<AiLineAnnotation>()
                .FirstOrDefault(a => a.Result.Id == annotation.Id);
            if (existing is null) return;

            var index = DiffAnnotations.IndexOf(existing);
            var updated = annotation with { ReadState = state };
            if (state == AiAnnotationReadState.Dismissed && !AiShowDismissedAnnotations)
                DiffAnnotations.RemoveAt(index);
            else
                DiffAnnotations[index] = new AiLineAnnotation(updated, existing.Range);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RunAiInlineActionAsync(string? action)
    {
        if (_host?.SelectedFile is not { } file || string.IsNullOrWhiteSpace(action) || DraftCommentLine is not int endLine)
            return;

        var lines = ResolveDraftLineTexts();
        if (lines.Count == 0)
        {
            _notifications.Info("No line content available for this action.");
            return;
        }

        var startLine = DraftCommentStartLine ?? endLine;
        var context = string.Join('\n', lines);
        try
        {
            var result = await _ai.RunInlineActionAsync(new AiInlineActionRequest(
                    SessionKey,
                    file.Path.Value,
                    action,
                    context,
                    startLine,
                    endLine))
                .ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                NewCommentBody = string.IsNullOrWhiteSpace(NewCommentBody)
                    ? result
                    : NewCommentBody.TrimEnd() + "\n\n" + result;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"AI action failed: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task ToggleAiChatAsync()
    {
        ShowAiChat = !ShowAiChat;
        OnPropertyChanged(nameof(AiChatSelectedFileLabel));
        OnPropertyChanged(nameof(AiChatPlaceholder));
        if (!ShowAiChat || _host is null || AiChatMessages.Count > 0)
            return;

        try
        {
            var history = await _ai.GetChatHistoryAsync(SessionKey).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                foreach (var message in history)
                    AiChatMessages.Add(message);
                OnPropertyChanged(nameof(CanClearAiChat));
            }).ConfigureAwait(false);
        }
        catch
        {
            // Chat history is best-effort.
        }
    }

    [RelayCommand]
    private async Task ClearAiChatAsync()
    {
        if (_host is null)
            return;

        AiChatMessages.Clear();
        OnPropertyChanged(nameof(CanClearAiChat));

        try
        {
            await _ai.ClearChatHistoryAsync(SessionKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to clear chat history: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task SendAiChatAsync()
    {
        if (_host is null || string.IsNullOrWhiteSpace(AiChatInput) || IsAiChatBusy)
            return;

        var sessionKey = SessionKey;
        var question = AiChatInput.Trim();
        AiChatInput = "";
        AiChatMessages.Add(new AiChatMessage("user", question, DateTimeOffset.UtcNow));
        OnPropertyChanged(nameof(CanClearAiChat));
        IsAiChatBusy = true;

        try
        {
            string? linesContext = null;
            if (!string.IsNullOrWhiteSpace(_host.SelectedFile?.AiGuidance))
                linesContext = $"File guidance from triage:\n{_host.SelectedFile.AiGuidance.Trim()}";

            var reply = await _ai.ChatAsync(new AiQuestionRequest(
                    sessionKey,
                    _host.SelectedFile?.Path.Value,
                    question,
                    linesContext))
                .ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                AiChatMessages.Add(new AiChatMessage("assistant", reply, DateTimeOffset.UtcNow));
                OnPropertyChanged(nameof(CanClearAiChat));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await InvokeOnUiAsync(() =>
            {
                AiChatMessages.Add(new AiChatMessage("assistant", $"Error: {ex.Message}", DateTimeOffset.UtcNow));
                OnPropertyChanged(nameof(CanClearAiChat));
            }).ConfigureAwait(false);
        }
        finally
        {
            await InvokeOnUiAsync(() => IsAiChatBusy = false).ConfigureAwait(false);
        }
    }

    private async Task LoadAiFileDetailAsync(FileItemViewModel file, FileDiff diff, CancellationToken ct)
    {
        if (_host is null || !_settings.Current.AiAssistanceEnabled)
            return;

        var sessionKey = SessionKey;
        try
        {
            var summary = await _ai.GetFileSummaryAsync(sessionKey, file.Path.Value, ct).ConfigureAwait(false);
            if (summary is null)
            {
                var beforeOid = diff.OldContent.IsEmpty ? null : diff.OldContent.Value;
                var afterOid = diff.NewContent.IsEmpty ? null : diff.NewContent.Value;
                _ = _ai.RequestFileDepthAsync(
                    new AiFileDepthRequest(sessionKey, file.Path.Value, beforeOid, afterOid),
                    CancellationToken.None);
            }

            var annotations = await _ai
                .GetFileAnnotationsAsync(sessionKey, file.Path.Value, AiShowDismissedAnnotations, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            await InvokeOnUiAsync(() =>
            {
                if (!ReferenceEquals(_host?.SelectedFile, file) || !ReferenceEquals(_host?.CurrentDiff, diff))
                    return;

                AiFileSummary = summary;

                foreach (var stale in DiffAnnotations.OfType<AiLineAnnotation>().ToList())
                    DiffAnnotations.Remove(stale);

                foreach (var annotation in annotations)
                {
                    var content = annotation.Side == DiffSide.Old ? diff.OldContent : diff.NewContent;
                    var start = new DiffAnchor(annotation.Side, content, annotation.StartLine);
                    var end = new DiffAnchor(annotation.Side, content, annotation.EndLine);
                    DiffAnnotations.Add(new AiLineAnnotation(annotation, new AnnotationRange(start, end)));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // AI file overlay is best-effort and must never block the diff view.
        }
    }

    // ---------------------------------------------------------------------
    // Selection sync — called by the host whenever the selected file / diff changes.
    // ---------------------------------------------------------------------

    /// <summary>Called by the host after loading a new diff for the selected file (or clearing the selection).</summary>
    public void OnFileSelectionChanged(FileItemViewModel? file, FileDiff? diff)
    {
        var path = file?.Path.Value;
        var oldId = diff?.OldContent.Value;
        var newId = diff?.NewContent.Value;
        var sameContent = file is not null
                          && diff is not null
                          && string.Equals(path, _boundSelectionPath, StringComparison.Ordinal)
                          && string.Equals(oldId, _boundOldContentId, StringComparison.Ordinal)
                          && string.Equals(newId, _boundNewContentId, StringComparison.Ordinal);

        if (sameContent)
        {
            // Soft status refresh remapped FileItemViewModel instances; keep AI overlays and only refresh local markers.
            ReloadLocalCommentAnnotationsForSelection();
            NotifyAiFileBandChanged();
            return;
        }

        _aiFileCts?.Cancel();
        _aiFileCts = null;

        _boundSelectionPath = path;
        _boundOldContentId = oldId;
        _boundNewContentId = newId;

        AiFileQuestion = "";
        AiFileAnswer = null;
        AiFileSummary = null;
        SelectedAnnotation = null;

        foreach (var stale in DiffAnnotations.OfType<AiLineAnnotation>().ToList())
            DiffAnnotations.Remove(stale);

        ReloadLocalCommentAnnotationsForSelection();
        NotifyAiFileBandChanged();

        if (file is not null && !_suppressCommentsDeselect)
            IsCommentsSelected = false;

        if (file is null || diff is null)
            return;

        if (HasAiTriage && _settings.Current.AiAssistanceEnabled)
        {
            _aiFileCts = new CancellationTokenSource();
            _ = LoadAiFileDetailAsync(file, diff, _aiFileCts.Token);
        }
    }

    // ---------------------------------------------------------------------
    // Local (non-GitHub) comments.
    // ---------------------------------------------------------------------

    [ObservableProperty] private string _newCommentBody = "";
    [ObservableProperty] private int? _draftCommentLine;
    [ObservableProperty] private int? _draftCommentStartLine;
    [ObservableProperty] private string? _draftCommentSide;
    [ObservableProperty] private bool _hasDraftCommentAnchor;
    [ObservableProperty] private string _draftCommentTargetLabel = "";
    [ObservableProperty] private bool _isCommentsSelected;

    partial void OnHasDraftCommentAnchorChanged(bool value)
    {
        OnPropertyChanged(nameof(HasExpandedAiAnnotation));
        OnPropertyChanged(nameof(HasExpandedLocalComment));
        ExpandedLocalCommentChanged?.Invoke();
    }

    partial void OnIsCommentsSelectedChanged(bool value)
    {
        if (!value)
            return;

        ClearDraftCommentAnchorCore();
        SelectedAnnotation = null;
        ExpandedFileLevelComment = null;
        _suppressCommentsDeselect = true;
        try
        {
            _host?.ClearFileSelection();
        }
        finally
        {
            _suppressCommentsDeselect = false;
        }

        _boundSelectionPath = null;
        _boundOldContentId = null;
        _boundNewContentId = null;
    }

    public int UnresolvedCommentCount => LocalComments.Count(c => !c.IsResolved);
    public bool HasUnresolvedComments => UnresolvedCommentCount > 0;

    private void NotifyCommentCountsChanged()
    {
        OnPropertyChanged(nameof(UnresolvedCommentCount));
        OnPropertyChanged(nameof(HasUnresolvedComments));
        UpdateFileUnresolvedCommentCounts();
    }

    /// <summary>Reloads all local comments for the current repository (call on repo open / after mutations).</summary>
    public async Task RefreshLocalCommentsAsync(CancellationToken ct = default)
    {
        if (_host is null)
            return;

        try
        {
            var records = await _localComments.ListAsync(RepositoryKey, ct).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                LocalComments.Clear();
                foreach (var record in records)
                    LocalComments.Add(new LocalCommentItemViewModel(record));
                NotifyCommentCountsChanged();
                ReloadLocalCommentAnnotationsForSelection();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to load local comments: {ex.Message}", exception: ex);
        }
    }

    /// <summary>Blocks a commit when there are unresolved local comments unless the user confirms.</summary>
    public async Task<bool> ConfirmCommitWithUnresolvedAsync()
    {
        if (UnresolvedCommentCount <= 0)
            return true;

        var noun = UnresolvedCommentCount == 1 ? "comment" : "comments";
        return await _confirm.ConfirmAsync(
                "Unresolved comments",
                $"You have {UnresolvedCommentCount} unresolved {noun}. Commit anyway?",
                "Commit anyway")
            .ConfigureAwait(false);
    }

    private void ReloadLocalCommentAnnotationsForSelection()
    {
        var selectedId = SelectedLocalCommentAnnotation?.Record.Id;

        foreach (var stale in DiffAnnotations.OfType<LocalLineCommentAnnotation>().ToList())
            DiffAnnotations.Remove(stale);

        var file = _host?.SelectedFile;
        var diff = _host?.CurrentDiff;
        if (file is null || diff is null)
            return;

        foreach (var comment in LocalComments.Where(c =>
                     !c.IsFileLevel && string.Equals(c.Path, file.Path.Value, StringComparison.Ordinal)))
        {
            var content = comment.Side == DiffSide.Old ? diff.OldContent : diff.NewContent;
            DiffAnnotations.Add(new LocalLineCommentAnnotation(comment.ToRecord(), content));
        }

        if (selectedId is not null)
        {
            SelectedAnnotation = DiffAnnotations.OfType<LocalLineCommentAnnotation>()
                .FirstOrDefault(a => string.Equals(a.Record.Id, selectedId, StringComparison.Ordinal));
        }
    }

    [RelayCommand]
    private void BeginLineComment(LineCommentRequest? request)
    {
        if (request is null || _host?.SelectedFile is null)
            return;

        SelectedAnnotation = null;
        DraftCommentSide = request.Side == DiffSide.Old ? "LEFT" : "RIGHT";
        DraftCommentLine = request.Line;
        DraftCommentStartLine = request.StartLine;
        HasDraftCommentAnchor = true;
        DraftCommentTargetLabel = request.StartLine is { } start && start != request.Line
            ? $"Commenting on L{start}–L{request.Line} ({DraftCommentSide})"
            : $"Commenting on L{request.Line} ({DraftCommentSide})";

        FocusCommentDraftRequested?.Invoke();
    }

    [RelayCommand]
    private void BeginFileComment()
    {
        if (_host?.SelectedFile is null)
            return;

        SelectedAnnotation = null;
        DraftCommentSide = null;
        DraftCommentLine = null;
        DraftCommentStartLine = null;
        HasDraftCommentAnchor = true;
        DraftCommentTargetLabel = "Commenting on file";

        FocusCommentDraftRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearDraftCommentAnchor() => ClearDraftCommentAnchorCore();

    private void ClearDraftCommentAnchorCore()
    {
        DraftCommentLine = null;
        DraftCommentStartLine = null;
        DraftCommentSide = null;
        HasDraftCommentAnchor = false;
        DraftCommentTargetLabel = "";
        NewCommentBody = "";
    }

    private IReadOnlyList<string> ResolveDraftLineTexts()
    {
        if (DraftCommentLine is not int endLine || _host?.CurrentDiff is not { } diff)
            return [];

        var startLine = DraftCommentStartLine ?? endLine;
        var from = Math.Min(startLine, endLine);
        var to = Math.Max(startLine, endLine);
        var leftSide = string.Equals(DraftCommentSide, "LEFT", StringComparison.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                var lineNumber = leftSide ? line.OldLine : line.NewLine;
                if (lineNumber is null || lineNumber < from || lineNumber > to)
                    continue;
                result.Add(line.Text.ToString());
            }
        }

        return result;
    }

    [RelayCommand]
    private void InsertSuggestion()
    {
        var lines = ResolveDraftLineTexts();
        if (lines.Count == 0)
        {
            _notifications.Info("No line content available for a suggestion.");
            return;
        }

        var block = "```suggestion\n" + string.Join('\n', lines) + "\n```";
        NewCommentBody = string.IsNullOrWhiteSpace(NewCommentBody)
            ? block
            : NewCommentBody.TrimEnd() + "\n\n" + block;
        FocusCommentDraftRequested?.Invoke();
    }

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        if (_host is null || string.IsNullOrWhiteSpace(NewCommentBody))
            return;

        var file = _host.SelectedFile;
        if (file is null)
            return;

        var body = NewCommentBody.Trim();
        var isFileLevel = DraftCommentLine is null;
        var endLine = DraftCommentLine ?? 0;
        var startLine = DraftCommentStartLine ?? endLine;
        var side = string.Equals(DraftCommentSide, "LEFT", StringComparison.OrdinalIgnoreCase)
            ? DiffSide.Old
            : DiffSide.New;

        string? contentId = null;
        if (!isFileLevel && _host.CurrentDiff is { } diff)
        {
            var content = side == DiffSide.Old ? diff.OldContent : diff.NewContent;
            contentId = content.IsEmpty ? null : content.Value;
        }

        ClearDraftCommentAnchorCore();

        try
        {
            var record = await _localComments.AddAsync(new LocalCommentCreate(
                    RepositoryKey, file.Path.Value, startLine, endLine, side, body, contentId))
                .ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                LocalComments.Add(new LocalCommentItemViewModel(record));
                NotifyCommentCountsChanged();
                ReloadLocalCommentAnnotationsForSelection();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to save comment: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task ResolveLocalCommentAsync(LocalCommentItemViewModel? comment) =>
        await SetLocalCommentResolvedAsync(comment, resolved: true).ConfigureAwait(false);

    [RelayCommand]
    private async Task UnresolveLocalCommentAsync(LocalCommentItemViewModel? comment) =>
        await SetLocalCommentResolvedAsync(comment, resolved: false).ConfigureAwait(false);

    private async Task SetLocalCommentResolvedAsync(LocalCommentItemViewModel? comment, bool resolved)
    {
        if (comment is null || comment.IsResolved == resolved)
            return;

        try
        {
            await _localComments.SetResolvedAsync(comment.Id, resolved).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                comment.IsResolved = resolved;
                NotifyCommentCountsChanged();
                ReloadLocalCommentAnnotationsForSelection();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to update comment: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private async Task DeleteLocalCommentAsync(LocalCommentItemViewModel? comment)
    {
        if (comment is null)
            return;

        var confirmed = await _confirm
            .ConfirmAsync("Delete comment", "Delete this comment? This cannot be undone.", "Delete")
            .ConfigureAwait(false);
        if (!confirmed)
            return;

        try
        {
            await _localComments.DeleteAsync(comment.Id).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (SelectedLocalCommentAnnotation?.Record.Id == comment.Id
                    || ExpandedFileLevelComment?.Id == comment.Id
                    || SelectedLocalCommentItem?.Id == comment.Id)
                {
                    SelectedAnnotation = null;
                    ExpandedFileLevelComment = null;
                }

                LocalComments.Remove(comment);
                NotifyCommentCountsChanged();
                ReloadLocalCommentAnnotationsForSelection();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _notifications.Error($"Failed to delete comment: {ex.Message}", exception: ex);
        }
    }

    [RelayCommand]
    private void SelectComments() => IsCommentsSelected = true;

    [RelayCommand]
    private async Task SelectCommentAsync(LocalCommentItemViewModel? comment)
    {
        if (comment is null || _host is null)
            return;

        await _host.SelectFileAsync(FilePath.From(comment.Path)).ConfigureAwait(false);

        await InvokeOnUiAsync(() =>
        {
            IsCommentsSelected = false;

            if (comment.IsFileLevel)
            {
                SelectedAnnotation = null;
                ExpandedFileLevelComment = comment;
                return;
            }

            ExpandedFileLevelComment = null;
            var match = DiffAnnotations.OfType<LocalLineCommentAnnotation>()
                .FirstOrDefault(a => string.Equals(a.Record.Id, comment.Id, StringComparison.Ordinal));
            if (match is not null)
            {
                SelectedAnnotation = match;
                RequestScrollToSelectedAnnotation?.Invoke();
            }

            ExpandedLocalCommentChanged?.Invoke();
        }).ConfigureAwait(false);
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
}

/// <summary>A saved local-only (non-GitHub) review comment on a File Status file.</summary>
public partial class LocalCommentItemViewModel : ObservableObject
{
    private readonly string? _contentId;
    private readonly DateTimeOffset _createdUtc;

    public LocalCommentItemViewModel(LocalCommentRecord record)
    {
        Id = record.Id;
        RepositoryKey = record.RepositoryKey;
        Path = record.Path;
        StartLine = record.StartLine;
        EndLine = record.EndLine;
        Side = record.Side;
        _contentId = record.ContentId;
        _createdUtc = record.CreatedUtc;
        _body = record.Body;
        _isResolved = record.IsResolved;
    }

    public string Id { get; }
    public string RepositoryKey { get; }
    public string Path { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public DiffSide Side { get; }

    [ObservableProperty] private string _body;
    [ObservableProperty] private bool _isResolved;

    public bool IsFileLevel => StartLine <= 0 && EndLine <= 0;

    public string LineLabel => IsFileLevel
        ? "File comment"
        : StartLine != EndLine ? $"L{StartLine}\u2013L{EndLine}" : $"L{EndLine}";

    public string Label => $"{System.IO.Path.GetFileName(Path)} · {LineLabel}";

    /// <summary>Snapshots the current mutable state back into an immutable store record.</summary>
    public LocalCommentRecord ToRecord() => new(
        Id, RepositoryKey, Path, StartLine, EndLine, Side, Body, IsResolved, _contentId, _createdUtc, DateTimeOffset.UtcNow);
}
