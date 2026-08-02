using Avalonia.Controls;
using Avalonia.Layout;

namespace CodeReviewr.App.Services;

/// <summary>Modal review-submit dialog with an optional summary body.</summary>
public sealed class AvaloniaReviewSubmitDialog : IReviewSubmitDialog
{
    public Window? Owner { get; set; }

    public async Task<string?> ShowAsync(string title, string confirmLabel)
    {
        if (Owner is null)
            return "";

        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            MinHeight = 200,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var messageBlock = new TextBlock
        {
            Text = "Add an optional summary for your review.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 20, 20, 8),
        };

        var bodyBox = new TextBox
        {
            PlaceholderText = "Optional review summary",
            AcceptsReturn = true,
            MinHeight = 80,
            Margin = new Avalonia.Thickness(20, 0, 20, 12),
        };

        var confirmButton = new Button
        {
            Content = confirmLabel,
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

        confirmButton.Click += (_, _) =>
        {
            result = bodyBox.Text?.Trim() ?? "";
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
        DockPanel.SetDock(messageBlock, Dock.Top);
        root.Children.Add(buttons);
        root.Children.Add(messageBlock);
        root.Children.Add(bodyBox);
        dialog.Content = root;

        await dialog.ShowDialog(Owner);
        return result;
    }
}
