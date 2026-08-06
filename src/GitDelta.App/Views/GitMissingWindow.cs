using Avalonia.Controls;
using Avalonia.Platform;
using GitDelta.Core;

namespace GitDelta.App.Views;

public partial class GitMissingWindow : Window
{
    public GitMissingWindow() : this("Git was not found.") { }

    public GitMissingWindow(string message)
    {
        Title = $"{ProductInfo.DisplayName} — Git required";
        Width = 520;
        Height = 280;
        CanResize = false;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://GitDelta.App/Assets/logo.png")));
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Git is required",
                    FontSize = 20,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new Button
                {
                    Content = "Quit",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => Close()),
                },
            },
        };
    }
}
