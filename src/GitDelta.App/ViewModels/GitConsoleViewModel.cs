using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.Git;

namespace GitDelta.App.ViewModels;

public enum GitConsoleLineKind
{
    Command,
    Stdout,
    Stderr,
    Meta,
}

public sealed record GitConsoleLine(GitConsoleLineKind Kind, string Text);

public partial class GitConsoleViewModel : ObservableObject
{
    private readonly IGitCommandLog _log;
    private int _rebuildQueued;

    public GitConsoleViewModel(IGitCommandLog log)
    {
        _log = log;
        _log.Changed += (_, _) => QueueRebuild();
        RebuildLines();
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private IReadOnlyList<GitConsoleLine> _lines = Array.Empty<GitConsoleLine>();

    /// <summary>Raised after lines are rebuilt so the view can auto-scroll when expanded.</summary>
    public event Action? LinesUpdated;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            LinesUpdated?.Invoke();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        RebuildLines();
    }

    private void QueueRebuild()
    {
        // Coalesce bursts of git log updates onto a single UI rebuild.
        if (Interlocked.Exchange(ref _rebuildQueued, 1) != 0)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _rebuildQueued, 0);
            RebuildLines();
        });
    }

    private void RebuildLines()
    {
        var built = new List<GitConsoleLine>();
        foreach (var entry in _log.Entries)
        {
            var header = $"[{entry.Timestamp:HH:mm:ss}] {entry.CommandLine}";
            if (entry.IsLongLivedStart)
                header += " — started";
            else if (entry.ExitCode is { } code)
                header += $" — exit {code}";
            built.Add(new GitConsoleLine(GitConsoleLineKind.Command, header));

            if (!string.IsNullOrEmpty(entry.Stdout))
            {
                foreach (var line in entry.Stdout.TrimEnd().Split('\n'))
                    built.Add(new GitConsoleLine(GitConsoleLineKind.Stdout, line.TrimEnd('\r')));
            }

            if (!string.IsNullOrEmpty(entry.Stderr))
            {
                built.Add(new GitConsoleLine(GitConsoleLineKind.Meta, "--- stderr ---"));
                foreach (var line in entry.Stderr.TrimEnd().Split('\n'))
                    built.Add(new GitConsoleLine(GitConsoleLineKind.Stderr, line.TrimEnd('\r')));
            }

            built.Add(new GitConsoleLine(GitConsoleLineKind.Meta, ""));
        }

        // Replace the list atomically. Clearing ObservableCollection item-by-item
        // was crashing Avalonia's WeakEvent Subscription when git log updated
        // while the ItemsControl was collapsed / mid-layout.
        Lines = built;
        LinesUpdated?.Invoke();
    }
}
