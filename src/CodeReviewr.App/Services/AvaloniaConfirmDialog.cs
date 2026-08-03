using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodeReviewr.App.Services;

/// <summary>Shows a modal confirm/cancel dialog owned by the main window.</summary>
public sealed class AvaloniaConfirmDialog : IConfirmDialog
{
    public Window? Owner { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Discard")
    {
        if (Owner is null)
            return true;

        return await Dispatcher.UIThread.InvokeAsync(() => ShowDialogCoreAsync(title, message, confirmLabel))
            .ConfigureAwait(false);
    }

    private async Task<bool> ShowDialogCoreAsync(string title, string message, string confirmLabel)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            MinHeight = 160,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 20, 20, 12),
        };

        var confirmButton = new Button
        {
            Content = confirmLabel,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true,
        };

        confirmButton.Click += (_, _) =>
        {
            result = true;
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
        buttons.Children.Add(confirmButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(messageBlock);
        dialog.Content = root;

        await dialog.ShowDialog(Owner!);
        return result;
    }
}
