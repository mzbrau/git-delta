using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitDelta.App.Services;

/// <summary>Modal stash / stash-and-restore choice when checkout is blocked by local changes.</summary>
public sealed class AvaloniaCheckoutBlockedDialog : ICheckoutBlockedDialog
{
    public Window? Owner { get; set; }

    public async Task<CheckoutBlockedChoice> ShowAsync(string targetRef)
    {
        if (Owner is null)
            return CheckoutBlockedChoice.Cancel;

        return await Dispatcher.UIThread.InvokeAsync(() => ShowDialogCoreAsync(targetRef))
            .ConfigureAwait(false);
    }

    private async Task<CheckoutBlockedChoice> ShowDialogCoreAsync(string targetRef)
    {
        var choice = CheckoutBlockedChoice.Cancel;
        var dialog = new Window
        {
            Title = "Checkout blocked",
            Width = 460,
            MinHeight = 180,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var messageBlock = new TextBlock
        {
            Text =
                $"Local changes prevent checking out '{targetRef}'. Stash them to continue, and optionally restore them on the new branch.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 20, 20, 12),
        };

        var stashOnlyButton = new Button
        {
            Content = "Stash only",
            MinWidth = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var stashRestoreButton = new Button
        {
            Content = "Stash & restore",
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "Primary" },
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };

        stashOnlyButton.Click += (_, _) =>
        {
            choice = CheckoutBlockedChoice.StashOnly;
            dialog.Close();
        };
        stashRestoreButton.Click += (_, _) =>
        {
            choice = CheckoutBlockedChoice.StashAndRestore;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(20, 0, 20, 20),
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(stashOnlyButton);
        buttons.Children.Add(stashRestoreButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(messageBlock);
        dialog.Content = root;

        await dialog.ShowDialog(Owner!);
        return choice;
    }
}
