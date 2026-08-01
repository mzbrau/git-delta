using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;

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
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.WorkingCopy.NotifyWindowActivated();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (!_selectionSyncSubscribed)
        {
            vm.WorkingCopy.SelectionSyncRequested += ApplySelectionToListBoxes;
            _selectionSyncSubscribed = true;
        }

        if (!_gitConsoleSubscribed)
        {
            vm.GitConsole.LinesUpdated += ScrollGitConsoleToEnd;
            _gitConsoleSubscribed = true;
        }
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

        // Defer repo open so the window can paint first.
        Dispatcher.UIThread.Post(() => _ = Vm.TryOpenLastRepositoryAsync(), DispatcherPriority.Background);
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

    private void OnToggleWorkspace(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.WorkspaceExpanded = !Vm.WorkingCopy.WorkspaceExpanded;

    private void OnToggleBranches(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.BranchesExpanded = !Vm.WorkingCopy.BranchesExpanded;

    private void OnToggleStashes(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StashesExpanded = !Vm.WorkingCopy.StashesExpanded;

    private void OnTogglePullRequests(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.PullRequestsExpanded = !Vm.WorkingCopy.PullRequestsExpanded;

    private void OnTogglePrRequested(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.PrRequestedExpanded = !Vm.WorkingCopy.PrRequestedExpanded;

    private void OnTogglePrRaised(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.PrRaisedExpanded = !Vm.WorkingCopy.PrRaisedExpanded;

    private void OnToggleStaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StagedExpanded = !Vm.WorkingCopy.StagedExpanded;

    private void OnToggleUnstaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.UnstagedExpanded = !Vm.WorkingCopy.UnstagedExpanded;

    private void OnStashFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is FileItemViewModel file)
            Vm.WorkingCopy.SetFileSelection([file]);
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
        if (list.SelectedItem is FileItemViewModel file)
            Vm.WorkingCopy.SetFileSelection([file]);
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

        if (!_multiSelectModifiers && sender is ListBox source)
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
                        hf.SelectedItem = file;
                    continue;
                }

                var list = file.IsConflicted ? ConflictedFileList
                    : file.IsStagedList ? StagedFileList
                    : UnstagedFileList;
                // Only select items that exist in the list — prevents phantom SelectedItems
                // when a stale History FileItemViewModel leaks into File Status sync.
                var match = FindInList(list, file);
                if (match is not null)
                    list.SelectedItems?.Add(match);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private static FileItemViewModel? FindInList(ListBox? list, FileItemViewModel file)
    {
        if (list?.Items is null) return null;
        foreach (var item in list.Items)
        {
            if (item is FileItemViewModel candidate
                && string.Equals(candidate.Path.Value, file.Path.Value, StringComparison.Ordinal)
                && candidate.IsStagedList == file.IsStagedList)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CollectSelected(ListBox? list, List<FileItemViewModel> into)
    {
        if (list?.SelectedItems is null) return;
        foreach (var item in list.SelectedItems)
        {
            if (item is FileItemViewModel file)
                into.Add(file);
        }
    }

    private void OnColumnSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        Vm.CaptureColumnWidthsFromGrid(MainColumns);
}
