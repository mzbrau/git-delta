using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeReviewr.App.Controls;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Review;

namespace CodeReviewr.App.Views;

public partial class MainWindow : Window
{
    private bool _suppressSelectionSync;
    private bool _multiSelectModifiers;
    private bool _selectionSyncSubscribed;
    private bool _gitConsoleSubscribed;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Opened += OnOpened;
        Activated += OnActivated;
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnWindowKeyDown;
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.NotifyWindowActivated();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (!_selectionSyncSubscribed)
        {
            vm.WorkingCopy.SelectionClearRequested += ClearFileStatusListSelection;
            vm.WorkingCopy.SelectionSyncRequested += ApplySelectionToListBoxes;
            _selectionSyncSubscribed = true;
        }

        if (!_gitConsoleSubscribed)
        {
            vm.GitConsole.LinesUpdated += ScrollGitConsoleToEnd;
            _gitConsoleSubscribed = true;
        }

        vm.Review.FocusCommentDraftRequested += FocusPrCommentDraft;
        vm.Review.FocusFileFilterRequested += FocusPrFileFilter;
        vm.Review.ExpandedThreadChanged += PositionInlineThreadCard;
    }

    private void FocusPrCommentDraft()
    {
        if (DataContext is MainWindowViewModel { Review.HasDraftCommentAnchor: true } vm)
        {
            PositionInlineCommentDraft(vm);
            if (this.FindControl<TextBox>("InlineCommentDraftBox") is { } inlineBox)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    inlineBox.Focus();
                    inlineBox.CaretIndex = inlineBox.Text?.Length ?? 0;
                }, DispatcherPriority.Input);
                return;
            }
        }

        if (this.FindControl<TextBox>("PrCommentDraftBox") is { } box)
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }
    }

    private void PositionInlineCommentDraft(MainWindowViewModel vm)
    {
        if (this.FindControl<Border>("InlineCommentDraft") is not { } draft)
            return;

        var side = string.Equals(vm.Review.DraftCommentSide, "LEFT", StringComparison.OrdinalIgnoreCase)
            ? DiffSide.Old
            : DiffSide.New;
        var line = vm.Review.DraftCommentLine ?? 1;

        double left = 48;
        double top = 24;
        double hostHeight = 400;
        double hostWidth = 800;
        if (TryGetPrLineAnchorRect(vm, side, line, out var anchor, out hostHeight, out hostWidth))
        {
            left = Math.Max(8, anchor.X);
            top = Math.Max(8, anchor.Y);
            var maxTop = Math.Max(8, hostHeight - 180);
            if (top > maxTop)
                top = maxTop;
            var maxWidth = Math.Max(280, hostWidth - left - 16);
            draft.Width = Math.Min(520, maxWidth);
        }

        Canvas.SetLeft(draft, left);
        Canvas.SetTop(draft, top);
    }

    private void PositionInlineThreadCard()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (!vm.Review.HasExpandedInlineThread ||
            vm.Review.SelectedThread?.Anchor is not { } range ||
            this.FindControl<Border>("InlineThreadCard") is not { } card)
            return;

        var side = range.End.Side;
        var line = range.End.Line;

        double left = 48;
        double top = 24;
        if (TryGetPrLineAnchorRect(vm, side, line, out var anchor, out var hostHeight, out var hostWidth))
        {
            left = Math.Max(8, anchor.X);
            top = Math.Max(8, anchor.Y);
            var maxTop = Math.Max(8, hostHeight - 220);
            if (top > maxTop)
                top = maxTop;
            var maxWidth = Math.Max(280, hostWidth - left - 16);
            card.Width = Math.Min(520, maxWidth);
        }

        Canvas.SetLeft(card, left);
        Canvas.SetTop(card, top);
    }

    private bool TryGetPrLineAnchorRect(
        MainWindowViewModel vm,
        DiffSide side,
        int line,
        out Avalonia.Rect anchor,
        out double hostHeight,
        out double hostWidth)
    {
        anchor = default;
        hostHeight = 400;
        hostWidth = 800;

        if (vm.Review.ShowMarkdownPreviewPane &&
            this.FindControl<MarkdownFilePreview>("PrMarkdownPreview") is { } preview)
        {
            hostHeight = preview.Bounds.Height;
            hostWidth = preview.Bounds.Width;
            return preview.TryGetLineAnchorRect(side, line, out anchor);
        }

        if (this.FindControl<DiffViewer>("PrDiffViewer") is { } viewer)
        {
            hostHeight = viewer.Bounds.Height;
            hostWidth = viewer.Bounds.Width;
            return viewer.TryGetLineAnchorRect(side, line, out anchor);
        }

        return false;
    }

    private void OnFileOrUnplaceableThreadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: ReviewThread thread } ||
            DataContext is not MainWindowViewModel vm)
            return;

        vm.Review.SelectedAnnotation = null;
        vm.Review.SelectedThread = thread;
        e.Handled = true;
    }

    private void FocusPrFileFilter()
    {
        if (this.FindControl<TextBox>("PrFileFilterBox") is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.Review.IsPullRequestMode)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = vm.Review.SubmitCommentShortcutCommand.ExecuteAsync(null);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            e.Handled = true;
            vm.Review.RequestFileFilterFocusCommand.Execute(null);
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
            return;

        if (IsTextEntryFocused())
            return;

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.Review.HasExpandedInlineThread || vm.Review.ShowSideThreadPanel)
                {
                    e.Handled = true;
                    vm.Review.ClearExpandedThreadCommand.Execute(null);
                }
                else if (vm.Review.HasDraftCommentAnchor)
                {
                    e.Handled = true;
                    vm.Review.ClearDraftCommentAnchorCommand.Execute(null);
                }
                break;
            case Key.J:
            case Key.Down:
                e.Handled = true;
                vm.Review.SelectNextFileCommand.Execute(null);
                break;
            case Key.K:
            case Key.Up:
                e.Handled = true;
                vm.Review.SelectPreviousFileCommand.Execute(null);
                break;
            case Key.V:
                e.Handled = true;
                _ = vm.Review.ToggleSelectedViewedCommand.ExecuteAsync(null);
                break;
            case Key.N:
                e.Handled = true;
                vm.Review.SelectNextThreadCommand.Execute(null);
                break;
            case Key.P:
                e.Handled = true;
                vm.Review.SelectPreviousThreadCommand.Execute(null);
                break;
            case Key.C:
                e.Handled = true;
                vm.Review.FocusCommentDraftCommand.Execute(null);
                break;
            case Key.Oem2:
                e.Handled = true;
                vm.Review.RequestFileFilterFocusCommand.Execute(null);
                break;
        }
    }

    private bool IsTextEntryFocused()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused)
            return false;

        return focused is TextBox or AutoCompleteBox;
    }

    private void ScrollGitConsoleToEnd()
    {
        if (!Vm.GitConsole.IsExpanded) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ScrollViewer>("GitConsoleScroll") is { } scroll)
                scroll.Offset = new Avalonia.Vector(scroll.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Vm.WindowWidth >= 640) Width = Vm.WindowWidth;
        if (Vm.WindowHeight >= 480) Height = Vm.WindowHeight;
        ApplyColumnWidths();

        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaConfirmDialog)) is AvaloniaConfirmDialog confirm)
            confirm.Owner = this;
        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaStashDialog)) is AvaloniaStashDialog stashDialog)
            stashDialog.Owner = this;
        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaReviewSubmitDialog)) is AvaloniaReviewSubmitDialog reviewSubmit)
            reviewSubmit.Owner = this;

        // Defer repo open so the window can paint first.
        Dispatcher.UIThread.Post(() => _ = Vm.TryOpenLastRepositoryAsync(), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => _ = Vm.Review.RefreshInboxCommand.ExecuteAsync(null), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => _ = Vm.EnsureRepositoryCatalogAsync(), DispatcherPriority.Background);
    }

    private void OnRepoSwitcherFlyoutOpened(object? sender, EventArgs e) =>
        _ = Vm.EnsureRepositoryCatalogAsync();

    private void OnRepositoryEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        if (RepoSwitcherButton.Flyout is FlyoutBase flyout)
            flyout.Hide();

        _ = Vm.SelectRepositoryCommand.ExecuteAsync(path);
    }

    private void ApplyColumnWidths()
    {
        if (MainColumns.ColumnDefinitions.Count < 5) return;
        MainColumns.ColumnDefinitions[0].Width = Vm.NavigatorColumnWidth;
        MainColumns.ColumnDefinitions[2].Width = Vm.FileListColumnWidth;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        Vm.WindowWidth = Width;
        Vm.WindowHeight = Height;
        Vm.PersistLayout();
    }

    private async void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Git Repository",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.OpenRepositoryPathAsync(path);
    }

    private async void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            await Vm.OpenRepositoryPathAsync(path);
    }

    private void OnNotificationAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppNotification n })
        {
            n.Action?.Invoke();
            Vm.Notifications.Dismiss(n);
        }
    }

    private void OnNotificationDismiss(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppNotification n })
            Vm.Notifications.Dismiss(n);
    }

    private void OnToggleWorkspace(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.WorkspaceExpanded = !Vm.WorkingCopy.WorkspaceExpanded;

    private void OnToggleBranches(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.BranchesExpanded = !Vm.WorkingCopy.BranchesExpanded;

    private void OnToggleStashes(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StashesExpanded = !Vm.WorkingCopy.StashesExpanded;

    private void OnTogglePullRequests(object? sender, RoutedEventArgs e) =>
        Vm.Review.PullRequestsExpanded = !Vm.Review.PullRequestsExpanded;

    private void OnToggleNeedsMyReview(object? sender, RoutedEventArgs e) =>
        Vm.Review.NeedsMyReviewExpanded = !Vm.Review.NeedsMyReviewExpanded;

    private void OnToggleReviewed(object? sender, RoutedEventArgs e) =>
        Vm.Review.ReviewedExpanded = !Vm.Review.ReviewedExpanded;

    private void OnToggleMyPullRequests(object? sender, RoutedEventArgs e) =>
        Vm.Review.MyPullRequestsExpanded = !Vm.Review.MyPullRequestsExpanded;

    private void OnPullRequestSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is CodeReviewr.GitHub.PullRequestSummary summary)
            _ = Vm.Review.SelectPullRequestCommand.ExecuteAsync(summary);
    }

    private void OnToggleStaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StagedExpanded = !Vm.WorkingCopy.StagedExpanded;

    private void OnToggleUnstaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.UnstagedExpanded = !Vm.WorkingCopy.UnstagedExpanded;

    private void OnStashFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (TryHandleFolderSelection(list, e.AddedItems, isHistory: false))
            return;
        if (list.SelectedItem is FileListEntry { File: { } file })
            Vm.WorkingCopy.SetFileSelection([file]);
        else if (list.SelectedItem is FileItemViewModel legacy)
            Vm.WorkingCopy.SetFileSelection([legacy]);
        else
            Vm.WorkingCopy.SetFileSelection([]);
    }

    private void OnHistoryCommitSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        if (sender is not ListBox list) return;
        Vm.WorkingCopy.SelectCommit(list.SelectedItem as CommitInfo);
    }

    private void OnHistoryFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;
        if (sender is not ListBox list) return;
        if (TryHandleFolderSelection(list, e.AddedItems, isHistory: true))
            return;
        if (list.SelectedItem is FileListEntry { File: { } file })
            Vm.WorkingCopy.SetFileSelection([file]);
        else if (list.SelectedItem is FileItemViewModel legacy)
            Vm.WorkingCopy.SetFileSelection([legacy]);
        else
            Vm.WorkingCopy.SetFileSelection([]);
    }

    private void OnToggleNavigatorCollapsed(object? sender, RoutedEventArgs e)
    {
        Vm.IsNavigatorCollapsed = !Vm.IsNavigatorCollapsed;
        ApplyColumnWidths();
    }

    private void OnFileCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: FileItemViewModel file })
        {
            e.Handled = true;
            _ = Vm.WorkingCopy.ToggleFileStagedCommand.ExecuteAsync(file);
        }
    }

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _multiSelectModifiers = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (!e.GetCurrentPoint(sender as Control).Properties.IsRightButtonPressed
            || sender is not ListBox list)
            return;

        var source = e.Source as Control;
        while (source is not null && source is not ListBoxItem)
            source = source.GetVisualParent() as Control;

        if (source is not ListBoxItem { DataContext: FileItemViewModel file })
            return;

        if (list.SelectedItems?.Contains(file) == true)
            return;

        _suppressSelectionSync = true;
        try
        {
            if (!_multiSelectModifiers)
            {
                ClearPeerSelections(list);
                list.SelectedItems?.Clear();
            }

            list.SelectedItems?.Add(file);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        SyncFileSelection();
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync) return;

        if (sender is ListBox source)
        {
            if (TryHandleFolderSelection(source, e.AddedItems, isHistory: false))
                return;

            if (!_multiSelectModifiers)
            {
                _suppressSelectionSync = true;
                try
                {
                    ClearPeerSelections(source);
                }
                finally
                {
                    _suppressSelectionSync = false;
                }
            }
        }

        SyncFileSelection();
    }

    private void ClearPeerSelections(ListBox source)
    {
        if (!ReferenceEquals(source, StagedFileList))
            StagedFileList.SelectedItems?.Clear();
        if (!ReferenceEquals(source, UnstagedFileList))
            UnstagedFileList.SelectedItems?.Clear();
        if (!ReferenceEquals(source, ConflictedFileList))
            ConflictedFileList.SelectedItems?.Clear();
    }

    private void SyncFileSelection()
    {
        var selected = new List<FileItemViewModel>();
        CollectSelected(StagedFileList, selected);
        CollectSelected(UnstagedFileList, selected);
        CollectSelected(ConflictedFileList, selected);
        Vm.WorkingCopy.SetFileSelection(selected);
    }

    private void ClearFileStatusListSelection()
    {
        _suppressSelectionSync = true;
        try
        {
            StagedFileList.SelectedItems?.Clear();
            UnstagedFileList.SelectedItems?.Clear();
            ConflictedFileList.SelectedItems?.Clear();
            if (this.FindControl<ListBox>("StashFileList") is { } stashFiles)
                stashFiles.SelectedItems?.Clear();
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private void ApplySelectionToListBoxes()
    {
        _suppressSelectionSync = true;
        try
        {
            StagedFileList.SelectedItems?.Clear();
            UnstagedFileList.SelectedItems?.Clear();
            ConflictedFileList.SelectedItems?.Clear();
            if (this.FindControl<ListBox>("HistoryFileList") is { } historyFiles)
                historyFiles.SelectedItem = null;

            foreach (var file in Vm.WorkingCopy.SelectedFilesSnapshot)
            {
                if (Vm.WorkingCopy.IsHistoryMode)
                {
                    if (this.FindControl<ListBox>("HistoryFileList") is { } hf)
                    {
                        var historyMatch = FindEntryInList(hf, file);
                        if (historyMatch is not null)
                            hf.SelectedItem = historyMatch;
                    }
                    continue;
                }

                var list = file.IsConflicted ? ConflictedFileList
                    : file.IsStagedList ? StagedFileList
                    : UnstagedFileList;
                // Only select items that exist in the list — prevents phantom SelectedItems
                // when a stale History FileItemViewModel leaks into File Status sync.
                var match = FindEntryInList(list, file);
                if (match is not null)
                    list.SelectedItems?.Add(match);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private bool TryHandleFolderSelection(ListBox list, System.Collections.IList added, bool isHistory)
    {
        foreach (var item in added)
        {
            if (item is not FileListEntry { IsFolder: true, FolderKey: { } key })
                continue;

            _suppressSelectionSync = true;
            try
            {
                list.SelectedItems?.Remove(item);
                if (ReferenceEquals(list.SelectedItem, item))
                    list.SelectedItem = null;
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            if (isHistory)
                Vm.WorkingCopy.ToggleHistoryFolderCommand.Execute(key);
            else
                Vm.WorkingCopy.ToggleFileStatusFolderCommand.Execute(key);
            return true;
        }

        return false;
    }

    private static FileListEntry? FindEntryInList(ListBox? list, FileItemViewModel file)
    {
        if (list?.Items is null) return null;
        foreach (var item in list.Items)
        {
            if (item is FileListEntry { File: { } candidate } entry
                && string.Equals(candidate.Path.Value, file.Path.Value, StringComparison.Ordinal)
                && candidate.IsStagedList == file.IsStagedList)
            {
                return entry;
            }
        }

        return null;
    }

    private static FileItemViewModel? FindInList(ListBox? list, FileItemViewModel file)
    {
        return FindEntryInList(list, file)?.File;
    }

    private static void CollectSelected(ListBox? list, List<FileItemViewModel> into)
    {
        if (list?.SelectedItems is null) return;
        foreach (var item in list.SelectedItems)
        {
            if (item is FileListEntry { File: { } file })
                into.Add(file);
            else if (item is FileItemViewModel legacy)
                into.Add(legacy);
        }
    }

    private void OnColumnSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Vm.CaptureColumnWidthsFromGrid(MainColumns);
}
