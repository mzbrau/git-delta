using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeReviewr.App.Services;

public sealed class NotificationService : ObservableObject
{
    public ObservableCollection<AppNotification> Notifications { get; } = [];

    public void Info(string message, Action? undo = null, string? undoLabel = null)
    {
        var n = new AppNotification(message, undo, undoLabel ?? "Undo");
        Notifications.Insert(0, n);
        _ = DismissAfterAsync(n, TimeSpan.FromSeconds(8));
    }

    public void Error(string message, Action? retry = null)
    {
        var n = new AppNotification(message, retry, retry is null ? null : "Retry", isError: true);
        Notifications.Insert(0, n);
    }

    public void Dismiss(AppNotification n) => Notifications.Remove(n);

    private async Task DismissAfterAsync(AppNotification n, TimeSpan delay)
    {
        await Task.Delay(delay);
        Notifications.Remove(n);
    }
}

public sealed class AppNotification(string message, Action? action, string? actionLabel, bool isError = false)
{
    public string Message { get; } = message;
    public Action? Action { get; } = action;
    public string? ActionLabel { get; } = actionLabel;
    public bool IsError { get; } = isError;
}
