using GitDelta.AI;
using GitDelta.App.ViewModels;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Caching;
using GitDelta.Core.Settings;
using GitDelta.Diff;
using GitDelta.Git;
using GitDelta.GitHub;
using GitDelta.Persistence;
using GitDelta.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace GitDelta.App.Services;

public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitDelta",
            "logs",
            "gitdelta-.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, buffered: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IDiffCache, MemoryDiffCache>();
        services.AddGitDeltaPersistence();
        services.AddGitDeltaGit();
        services.AddGitDeltaDiff();
        services.AddGitDeltaGitHub();
        services.AddGitDeltaReview();
        services.AddGitDeltaAI();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<PendingChangesReviewViewModel>();
        services.AddTransient<WorkingCopyViewModel>();
        services.AddTransient<ReviewViewModel>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<AvaloniaConfirmDialog>();
        services.AddSingleton<IConfirmDialog>(sp => sp.GetRequiredService<AvaloniaConfirmDialog>());
        services.AddSingleton<AvaloniaStashDialog>();
        services.AddSingleton<IStashDialog>(sp => sp.GetRequiredService<AvaloniaStashDialog>());
        services.AddSingleton<AvaloniaReviewSubmitDialog>();
        services.AddSingleton<IReviewSubmitDialog>(sp => sp.GetRequiredService<AvaloniaReviewSubmitDialog>());
        services.AddSingleton<DiagnosticsOverlayViewModel>();
        services.AddSingleton<GitConsoleViewModel>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDurableUserStore>().EnsureSchema();
        provider.GetRequiredService<IDisposableCacheStore>().EnsureSchema();
        return provider;
    }
}
