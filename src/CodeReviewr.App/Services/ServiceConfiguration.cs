using CodeReviewr.App.ViewModels;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Caching;
using CodeReviewr.Core.Settings;
using CodeReviewr.Diff;
using CodeReviewr.Git;
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
        services.AddCodeReviewrGit();
        services.AddCodeReviewrDiff();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<WorkingCopyViewModel>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DiagnosticsOverlayViewModel>();

        return services.BuildServiceProvider();
    }
}
