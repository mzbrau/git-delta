using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Serilog;
using Velopack;

namespace GitDelta.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        AttachGlobalExceptionHandlers();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogFatal(ex, "Fatal error during application startup.");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void AttachGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogFatal(ex, "Unhandled AppDomain exception.");
            else
                Log.Fatal("Unhandled AppDomain exception: {Object}", e.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled UI-thread exception.");
            // Keep the process alive for transient UI faults; log path is under LocalApplicationData.
            e.Handled = true;
        };
    }

    private static void LogFatal(Exception ex, string message)
    {
        try
        {
            Log.Fatal(ex, message);
            Log.CloseAndFlush();
        }
        catch
        {
            // Best-effort — avoid secondary failures during crash reporting.
        }
    }
}
