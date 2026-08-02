using CodeReviewr.App.ViewModels;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Core.Settings;
using CodeReviewr.Diff;
using CodeReviewr.Git;
using CodeReviewr.GitHub;
using CodeReviewr.Persistence;
using CodeReviewr.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodeReviewr.App.Services;

public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeReviewr",
            "logs",
            "codereviewr-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, buffered: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddCodeReviewrPersistence();
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        services.AddCodeReviewrGitHub();
        services.AddCodeReviewrReview();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<WorkingCopyViewModel>();
        services.AddTransient<ReviewViewModel>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<AvaloniaConfirmDialog>();
        services.AddSingleton<IConfirmDialog>(sp => sp.GetRequiredService<AvaloniaConfirmDialog>());
        services.AddSingleton<AvaloniaStashDialog>();
        services.AddSingleton<IStashDialog>(sp => sp.GetRequiredService<AvaloniaStashDialog>());
        services.AddSingleton<DiagnosticsOverlayViewModel>();
        services.AddSingleton<GitConsoleViewModel>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();
        provider.GetRequiredService<IDisposableCacheStore>().EnsureSchema();
        return provider;
    }
}
