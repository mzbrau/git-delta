using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GitDelta.App.Diagnostics;
using GitDelta.App.Services;
using GitDelta.App.ViewModels;
using GitDelta.App.Views;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace GitDelta.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static IDisposable? _otel;
    private static bool _liveMarkdownConfigured;

    public override void Initialize()
    {
        ConfigureLiveMarkdown();
        AvaloniaXamlLoader.Load(this);
    }

    private static void ConfigureLiveMarkdown()
    {
        if (_liveMarkdownConfigured)
            return;
        _liveMarkdownConfigured = true;

        MarkdownRenderer.ConfigurePipeline += pipeline => pipeline.UseMermaid();
        MarkdownNode.Register<MermaidBlockNode>();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _otel = OpenTelemetryBootstrap.StartIfConfigured();
        Services = ServiceConfiguration.Build();

        var settings = Services.GetRequiredService<ISettingsStore>();
        if (settings is GitDelta.Core.Settings.JsonSettingsStore jsonStore)
            jsonStore.Load();
        else
            settings.LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        // Apply persisted theme (Light / Dark / System).
        ApplyTheme(settings.Current.Theme);

        var env = Services.GetRequiredService<IGitEnvironment>();
        GitExecutableInfo? gitInfo = null;
        string? gitError = null;
        try
        {
            gitInfo = env.DetectAsync().GetAwaiter().GetResult();
            if (!gitInfo.Version.MeetsMinimum)
            {
                gitError =
                    $"Git {gitInfo.Version} is too old. {ProductInfo.DisplayName} requires Git {GitVersion.Minimum} or later.\n\n" +
                    "Install a newer Git from https://git-scm.com or via winget/brew, then restart.";
            }
        }
        catch (Exception ex)
        {
            gitError =
                "Git was not found on the PATH.\n\n" +
                $"{ProductInfo.DisplayName} requires Git 2.30 or later.\n" +
                "Windows: winget install --id Git.Git\n" +
                "macOS: brew install git\n\n" +
                $"Details: {ex.Message}";
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                _otel?.Dispose();
                _otel = null;
            };

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
