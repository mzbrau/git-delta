using Avalonia.Controls;
using Avalonia.Layout;

namespace CodeReviewr.App.Services;

/// <summary>Modal Push/Pop stash dialog owned by the main window.</summary>
public sealed class AvaloniaStashDialog : IStashDialog
{
    public Window? Owner { get; set; }

    public async Task<StashDialogResult?> ShowAsync()
    {
        if (Owner is null)
            return new StashDialogResult(StashDialogAction.Push, null, IncludeUntracked: true);

        StashDialogResult? result = null;
        var dialog = new Window
        {
            Title = "Stash",
            Width = 420,
            MinHeight = 200,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var pushRadio = new RadioButton
        {
            Content = "Push",
            IsChecked = true,
            GroupName = "StashAction",
            Margin = new Avalonia.Thickness(0, 0, 16, 0),
        };
        var popRadio = new RadioButton
        {
            Content = "Pop",
            GroupName = "StashAction",
        };

        var modeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(20, 20, 20, 12),
        };
        modeRow.Children.Add(pushRadio);
        modeRow.Children.Add(popRadio);

        var nameBox = new TextBox
        {
            PlaceholderText = "Stash message (optional)",
            Margin = new Avalonia.Thickness(20, 0, 20, 8),
        };

        var includeUntracked = new CheckBox
        {
            Content = "Include untracked",
            IsChecked = true,
            Margin = new Avalonia.Thickness(20, 0, 20, 12),
        };

        var pushOptions = new StackPanel();
        pushOptions.Children.Add(nameBox);
        pushOptions.Children.Add(includeUntracked);

        var confirmButton = new Button
        {
            Content = "Push",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };

        void SyncModeUi()
        {
            var isPush = pushRadio.IsChecked == true;
            pushOptions.IsVisible = isPush;
            confirmButton.Content = isPush ? "Push" : "Pop";
        }

        pushRadio.IsCheckedChanged += (_, _) => SyncModeUi();
        popRadio.IsCheckedChanged += (_, _) => SyncModeUi();
        SyncModeUi();

        confirmButton.Click += (_, _) =>
        {
            var isPush = pushRadio.IsChecked == true;
            result = new StashDialogResult(
                isPush ? StashDialogAction.Push : StashDialogAction.Pop,
                string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim(),
                includeUntracked.IsChecked == true);
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(20, 8, 20, 20),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(confirmButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(modeRow, Dock.Top);
        root.Children.Add(buttons);
        root.Children.Add(modeRow);
        root.Children.Add(pushOptions);
        dialog.Content = root;

        await dialog.ShowDialog(Owner);
        return result;
    }
}
