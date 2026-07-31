using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;

namespace CodeReviewr.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

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

    private void OnFileCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: FileItemViewModel file })
        {
            e.Handled = true;
            _ = Vm.WorkingCopy.ToggleFileStagedCommand.ExecuteAsync(file);
        }
    }
}
