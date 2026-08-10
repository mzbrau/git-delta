using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.Core;
using GitDelta.Core.Settings;

namespace GitDelta.App.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<ShortcutBindingRowViewModel> ShortcutBindings { get; } = [];

    [ObservableProperty] private bool _isCapturingShortcut;
    [ObservableProperty] private string? _shortcutCaptureHint;
    [ObservableProperty] private string? _shortcutConflictSummary;

    public KeyboardShortcutBindings SettingsShortcutsSnapshot() =>
        _settings.Current.Shortcuts ?? KeyboardShortcutBindings.CreateDefaults();

    /// <summary>
    /// Handles a resolved shortcut id. Returns true when the action was consumed.
    /// </summary>
    public bool TryHandleKeyboardShortcut(string id)
    {
        switch (id)
        {
            case KeyboardShortcutIds.QuickOpen:
                if (!WorkingCopy.HasRepository)
                    return false;
                _ = ShowQuickOpenCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.ToggleNavigator:
                IsNavigatorCollapsed = !IsNavigatorCollapsed;
                return true;

            case KeyboardShortcutIds.ToggleFilePanel:
                if (ShowPullRequestPane)
                    Review.ToggleFilePanelCommand.Execute(null);
                else if (ShowFileStatusPane)
                    WorkingCopy.PendingReview.ToggleFilePanelCommand.Execute(null);
                else
                    return false;
                return true;

            case KeyboardShortcutIds.ToggleDiffMode:
                ToggleActiveDiffMode();
                return true;

            case KeyboardShortcutIds.ToggleShowFullFile:
                if (ShowPullRequestPane)
                    Review.ToggleShowFullFileCommand.Execute(null);
                else
                    WorkingCopy.ToggleShowFullFileCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.ToggleIgnoreWhitespace:
                if (ShowPullRequestPane)
                    Review.ToggleIgnoreWhitespaceCommand.Execute(null);
                else
                    WorkingCopy.ToggleIgnoreWhitespaceCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.ToggleFileListQueryMode:
                ToggleActiveFileListQueryMode();
                return true;

            case KeyboardShortcutIds.ToggleFileListLayout:
                ToggleActiveFileListLayout();
                return true;

            case KeyboardShortcutIds.Push:
                if (!WorkingCopy.HasRepository)
                    return false;
                _ = WorkingCopy.PushCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.Pull:
                if (!WorkingCopy.HasRepository)
                    return false;
                _ = WorkingCopy.PullCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.Fetch:
                if (!WorkingCopy.HasRepository)
                    return false;
                _ = WorkingCopy.FetchCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.ViewRemote:
                if (!WorkingCopy.HasRepository)
                    return false;
                _ = WorkingCopy.ViewRemoteCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.RevealInFileManager:
                if (!WorkingCopy.HasRepository)
                    return false;
                WorkingCopy.RevealRepositoryInFileManagerCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.SubmitReview:
                if (!Review.IsPullRequestMode)
                    return false;
                _ = Review.SubmitCommentShortcutCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.FocusFileFilter:
            case KeyboardShortcutIds.FocusFileFilterSlash:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.RequestFileFilterFocusCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.NextFile:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.SelectNextFileCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.PreviousFile:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.SelectPreviousFileCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.ToggleViewed:
                if (!Review.IsPullRequestMode)
                    return false;
                _ = Review.ToggleSelectedViewedCommand.ExecuteAsync(null);
                return true;

            case KeyboardShortcutIds.NextThread:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.SelectNextThreadCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.PreviousThread:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.SelectPreviousThreadCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.FocusCommentDraft:
                if (!Review.IsPullRequestMode)
                    return false;
                Review.FocusCommentDraftCommand.Execute(null);
                return true;

            case KeyboardShortcutIds.EscapeDismiss:
                if (!Review.IsPullRequestMode)
                    return false;
                if (Review.IsMentionPopupOpen)
                    Review.DismissMentionPopupCommand.Execute(null);
                else if (Review.HasDraftCommentAnchor || Review.IsEditingComment)
                    Review.ClearDraftCommentAnchorCommand.Execute(null);
                else if (Review.HasExpandedInlineThread || Review.ShowSideThreadPanel)
                    Review.ClearExpandedThreadCommand.Execute(null);
                else
                    return false;
                return true;

            default:
                return false;
        }
    }

    private void ToggleActiveDiffMode()
    {
        if (ShowPullRequestPane)
        {
            var next = Review.ViewMode == DiffViewMode.Unified
                ? DiffViewMode.SideBySide
                : DiffViewMode.Unified;
            Review.SetViewModeCommand.Execute(next);
        }
        else
        {
            var next = WorkingCopy.ViewMode == DiffViewMode.Unified
                ? DiffViewMode.SideBySide
                : DiffViewMode.Unified;
            WorkingCopy.SetViewModeCommand.Execute(next);
        }
    }

    private void ToggleActiveFileListQueryMode()
    {
        if (ShowPullRequestPane)
        {
            var next = Review.FileListQueryMode == FileListQueryMode.Filter
                ? FileListQueryMode.Search
                : FileListQueryMode.Filter;
            Review.SetFileListQueryModeCommand.Execute(next);
        }
        else if (ShowHistoryPane)
        {
            var next = WorkingCopy.HistoryFileQueryMode == FileListQueryMode.Filter
                ? FileListQueryMode.Search
                : FileListQueryMode.Filter;
            WorkingCopy.SetHistoryFileQueryModeCommand.Execute(next);
        }
        else
        {
            var next = WorkingCopy.FileStatusQueryMode == FileListQueryMode.Filter
                ? FileListQueryMode.Search
                : FileListQueryMode.Filter;
            WorkingCopy.SetFileStatusQueryModeCommand.Execute(next);
        }
    }

    private void ToggleActiveFileListLayout()
    {
        if (ShowPullRequestPane)
        {
            var next = Review.PullRequestFileListLayout == FileListLayoutMode.Flat
                ? FileListLayoutMode.Tree
                : FileListLayoutMode.Flat;
            Review.SetPullRequestFileListLayoutCommand.Execute(next);
        }
        else if (ShowHistoryPane)
        {
            var next = WorkingCopy.HistoryFileListLayout == FileListLayoutMode.Flat
                ? FileListLayoutMode.Tree
                : FileListLayoutMode.Flat;
            WorkingCopy.SetHistoryFileListLayoutCommand.Execute(next);
        }
        else
        {
            var next = WorkingCopy.FileStatusListLayout == FileListLayoutMode.Flat
                ? FileListLayoutMode.Tree
                : FileListLayoutMode.Flat;
            WorkingCopy.SetFileStatusListLayoutCommand.Execute(next);
        }
    }

    public void ReloadShortcutBindingsUi()
    {
        ShortcutBindings.Clear();
        var effective = KeyboardShortcutResolver.ResolveEffective(_settings.Current.Shortcuts);
        foreach (var def in KeyboardShortcutCatalog.All)
        {
            effective.TryGetValue(def.Id, out var gesture);
            ShortcutBindings.Add(new ShortcutBindingRowViewModel(def, gesture ?? def.DefaultGesture));
        }

        RefreshShortcutConflictHints();
    }

    private void RefreshShortcutConflictHints()
    {
        var draft = new KeyboardShortcutBindings();
        foreach (var row in ShortcutBindings)
            draft.Bindings[row.Id] = row.Gesture;

        var conflicts = KeyboardShortcutResolver.FindConflicts(draft);
        foreach (var row in ShortcutBindings)
            row.ConflictHint = null;

        foreach (var (idA, idB, gesture) in conflicts)
        {
            var rowA = ShortcutBindings.FirstOrDefault(r => r.Id == idA);
            var rowB = ShortcutBindings.FirstOrDefault(r => r.Id == idB);
            if (rowA is not null)
                rowA.ConflictHint = $"Conflicts with {rowB?.DisplayName ?? idB} ({gesture})";
            if (rowB is not null)
                rowB.ConflictHint = $"Conflicts with {rowA?.DisplayName ?? idA} ({gesture})";
        }

        ShortcutConflictSummary = conflicts.Count == 0
            ? null
            : $"{conflicts.Count} conflicting shortcut pair(s). Duplicate chords are ambiguous—change one binding so each chord is unique.";
    }

    [RelayCommand]
    private void BeginCaptureShortcut(ShortcutBindingRowViewModel? row)
    {
        if (row is null)
            return;

        foreach (var r in ShortcutBindings)
            r.IsCapturing = false;

        row.IsCapturing = true;
        IsCapturingShortcut = true;
        ShortcutCaptureHint = $"Press a key chord for “{row.DisplayName}” (Esc to cancel)";
    }

    [RelayCommand]
    private void CancelCaptureShortcut()
    {
        foreach (var r in ShortcutBindings)
            r.IsCapturing = false;
        IsCapturingShortcut = false;
        ShortcutCaptureHint = null;
    }

    /// <summary>Applies a captured chord to the row currently capturing. Returns true if consumed.</summary>
    public bool TryApplyCapturedShortcut(string gestureText)
    {
        var row = ShortcutBindings.FirstOrDefault(r => r.IsCapturing);
        if (row is null)
            return false;

        if (string.IsNullOrEmpty(gestureText))
        {
            CancelCaptureShortcut();
            return true;
        }

        if (!KeyboardShortcutGesture.TryParse(gestureText, out var g) || g.IsEmpty)
            return true;

        // Reject if another row already uses this gesture.
        var conflict = ShortcutBindings.FirstOrDefault(r =>
            !ReferenceEquals(r, row)
            && KeyboardShortcutGesture.TryParse(r.Gesture, out var other)
            && !other.IsEmpty
            && string.Equals(other.Text, g.Text, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            ShortcutCaptureHint = $"Already used by “{conflict.DisplayName}”. Choose another or clear that binding first.";
            return true;
        }

        row.Gesture = g.Text;
        row.IsCapturing = false;
        IsCapturingShortcut = false;
        ShortcutCaptureHint = null;
        PersistShortcutBindings();
        RefreshShortcutConflictHints();
        return true;
    }

    [RelayCommand]
    private void ClearShortcut(ShortcutBindingRowViewModel? row)
    {
        if (row is null)
            return;
        row.Gesture = "";
        PersistShortcutBindings();
        RefreshShortcutConflictHints();
    }

    [RelayCommand]
    private void ResetShortcut(ShortcutBindingRowViewModel? row)
    {
        if (row is null)
            return;
        row.Gesture = row.DefaultGesture;
        PersistShortcutBindings();
        RefreshShortcutConflictHints();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        foreach (var row in ShortcutBindings)
            row.Gesture = row.DefaultGesture;
        PersistShortcutBindings();
        RefreshShortcutConflictHints();
    }

    private void PersistShortcutBindings()
    {
        _settings.Update(s =>
        {
            s.Shortcuts ??= KeyboardShortcutBindings.CreateDefaults();
            s.Shortcuts.Bindings.Clear();
            foreach (var row in ShortcutBindings)
                s.Shortcuts.Bindings[row.Id] = row.Gesture;
        });
        _ = _settings.SaveAsync();
    }
}
