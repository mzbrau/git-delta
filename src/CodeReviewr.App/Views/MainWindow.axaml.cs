using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;

namespace CodeReviewr.App.Views;

public partial class MainWindow : Window
{
    private bool _suppressSelectionSync;
    private bool _multiSelectModifiers;
    private bool _selectionSyncSubscribed;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Opened += OnOpened;
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_selectionSyncSubscribed || DataContext is not MainWindowViewModel vm) return;
        vm.WorkingCopy.SelectionSyncRequested += ApplySelectionToListBoxes;
        _selectionSyncSubscribed = true;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Vm.WindowWidth >= 640) Width = Vm.WindowWidth;
        if (Vm.WindowHeight >= 480) Height = Vm.WindowHeight;
        ApplyColumnWidths();

        if (global::CodeReviewr.App.App.Services.GetService(typeof(AvaloniaConfirmDialog)) is AvaloniaConfirmDialog confirm)
            confirm.Owner = this;

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

    private void OnToggleExplorer(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.ExplorerExpanded = !Vm.WorkingCopy.ExplorerExpanded;

    private void OnToggleBranches(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.BranchesExpanded = !Vm.WorkingCopy.BranchesExpanded;

    private void OnToggleStaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.StagedExpanded = !Vm.WorkingCopy.StagedExpanded;

    private void OnToggleUnstaged(object? sender, RoutedEventArgs e) =>
        Vm.WorkingCopy.UnstagedExpanded = !Vm.WorkingCopy.UnstagedExpanded;

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

            foreach (var file in Vm.WorkingCopy.SelectedFilesSnapshot)
            {
                var list = file.IsConflicted ? ConflictedFileList
                    : file.IsStagedList ? StagedFileList
                    : UnstagedFileList;
                list.SelectedItems?.Add(file);
            }
        }
        finally
        {
            _suppressSelectionSync = false;
        }
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
