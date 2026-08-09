using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitDelta.App.Services;

/// <summary>Modal dialog to confirm PR branch checkout and pick a local clone.</summary>
public sealed class AvaloniaCheckoutPullRequestDialog : ICheckoutPullRequestDialog
{
    public Window? Owner { get; set; }

    public async Task<CheckoutPullRequestDialogResult> ShowAsync(CheckoutPullRequestDialogModel model)
    {
        if (Owner is null)
            return new CheckoutPullRequestDialogResult(false, null);

        return await Dispatcher.UIThread.InvokeAsync(() => ShowDialogCoreAsync(model))
            .ConfigureAwait(false);
    }

    private async Task<CheckoutPullRequestDialogResult> ShowDialogCoreAsync(CheckoutPullRequestDialogModel model)
    {
        CheckoutPullRequestCandidate? selected = model.Candidates.FirstOrDefault(c => c.IsCurrentRepository)
            ?? model.Candidates.FirstOrDefault();
        var confirmed = false;

        var dialog = new Window
        {
            Title = "Checkout branch",
            Width = 520,
            MinHeight = 220,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var titleBlock = new TextBlock
        {
            Text = model.PullRequestTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(20, 20, 20, 4),
        };
        var subtitle = new TextBlock
        {
            Text = $"Check out `{model.BranchName}` from {model.NameWithOwner} locally. Git Delta will fetch, switch to the selected clone, and leave pull request mode.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
            Margin = new Avalonia.Thickness(20, 0, 20, 12),
        };

        var list = new ListBox
        {
            Margin = new Avalonia.Thickness(20, 0, 20, 12),
            MaxHeight = 220,
            SelectedItem = selected,
        };
        foreach (var candidate in model.Candidates)
            list.Items.Add(candidate);

        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CheckoutPullRequestCandidate>((item, _) =>
        {
            var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(6, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
            });
            panel.Children.Add(new TextBlock
            {
                Text = item.Path,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = item.StatusSummary,
                FontSize = 11,
                Opacity = 0.8,
            });
            return panel;
        });

        list.SelectionChanged += (_, _) =>
        {
            selected = list.SelectedItem as CheckoutPullRequestCandidate;
        };

        var confirmButton = new Button
        {
            Content = "Checkout",
            MinWidth = 96,
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
            confirmed = selected is not null;
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
        var body = new StackPanel();
        body.Children.Add(titleBlock);
        body.Children.Add(subtitle);
        if (model.Candidates.Count > 1)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Multiple local clones match this repository. Choose where to check out:",
                FontSize = 12,
                Margin = new Avalonia.Thickness(20, 0, 20, 8),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = "Local clone:",
                FontSize = 12,
                Margin = new Avalonia.Thickness(20, 0, 20, 8),
            });
        }

        body.Children.Add(list);
        root.Children.Add(body);
        dialog.Content = root;

        await dialog.ShowDialog(Owner!);
        return new CheckoutPullRequestDialogResult(confirmed, selected?.Path);
    }
}
