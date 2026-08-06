using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitDelta.App.Services;

public sealed class NotificationService : ObservableObject
{
    public ObservableCollection<AppNotification> Notifications { get; } = [];

    public void Info(string message, Action? undo = null, string? undoLabel = null)
    {
        var n = new AppNotification(message, undo, undo is null ? null : (undoLabel ?? "Undo"));
        RunOnUi(() => Notifications.Insert(0, n));
        _ = DismissAfterAsync(n, TimeSpan.FromSeconds(8));
    }

    public void Error(string message, Action? retry = null, Exception? exception = null, string? detail = null)
    {
        var n = new AppNotification(
            message,
            retry,
            retry is null ? null : "Retry",
            isError: true,
            detail: detail ?? exception?.ToString());
        RunOnUi(() => Notifications.Insert(0, n));
        _ = DismissAfterAsync(n, TimeSpan.FromSeconds(12));
    }

    public void Dismiss(AppNotification n) => RunOnUi(() => Notifications.Remove(n));

    private async Task DismissAfterAsync(AppNotification n, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        RunOnUi(() => Notifications.Remove(n));
    }

    private static void RunOnUi(Action action)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (Application.Current is null)
            {
                action();
                return;
            }

            dispatcher.Post(action);
        }
        catch (InvalidOperationException)
        {
            action();
        }
    }
}

public sealed class AppNotification(
    string message,
    Action? action,
    string? actionLabel,
    bool isError = false,
    string? detail = null)
{
    public string Message { get; } = message;
    public Action? Action { get; } = action;
    public string? ActionLabel { get; } = actionLabel;
    public bool IsError { get; } = isError;
    public string? Detail { get; } = detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Text suitable for clipboard: message plus full exception detail when present.</summary>
    public string CopyText =>
        HasDetail ? $"{Message}{Environment.NewLine}{Environment.NewLine}{Detail}" : Message;
}
