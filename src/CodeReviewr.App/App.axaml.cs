using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CodeReviewr.App.Services;
using CodeReviewr.App.ViewModels;
using CodeReviewr.App.Views;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewr.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ServiceConfiguration.Build();

        var settings = Services.GetRequiredService<ISettingsStore>();
        _ = settings.LoadAsync();

        // Forge Control UI is dark-only; keep settings Theme for future light variant.
        ApplyTheme("Dark");

        var env = Services.GetRequiredService<IGitEnvironment>();
        GitExecutableInfo? gitInfo = null;
        string? gitError = null;
        try
        {
            gitInfo = env.DetectAsync().GetAwaiter().GetResult();
            if (!gitInfo.Version.MeetsMinimum)
            {
                gitError =
                    $"Git {gitInfo.Version} is too old. CodeReviewr requires Git {GitVersion.Minimum} or later.\n\n" +
                    "Install a newer Git from https://git-scm.com or via winget/brew, then restart.";
            }
        }
        catch (Exception ex)
        {
            gitError =
                "Git was not found on the PATH.\n\n" +
                "CodeReviewr requires Git 2.30 or later.\n" +
                "Windows: winget install --id Git.Git\n" +
                "macOS: brew install git\n\n" +
                $"Details: {ex.Message}";
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (gitError is not null)
            {
                desktop.MainWindow = new GitMissingWindow(gitError);
            }
            else
            {
                var vm = Services.GetRequiredService<MainWindowViewModel>();
                vm.GitInfo = gitInfo;
                vm.Diagnostics.SetGitInfo(gitInfo);
                desktop.MainWindow = new MainWindow { DataContext = vm };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
