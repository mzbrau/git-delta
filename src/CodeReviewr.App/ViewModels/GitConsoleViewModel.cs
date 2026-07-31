using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CodeReviewr.Git;

namespace CodeReviewr.App.ViewModels;

public partial class GitConsoleViewModel : ObservableObject
{
    private readonly IGitCommandLog _log;

    public GitConsoleViewModel(IGitCommandLog log)
    {
        _log = log;
        _log.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RebuildText);
        RebuildText();
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _logText = "";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        RebuildText();
    }

    private void RebuildText()
    {
        var sb = new StringBuilder();
        foreach (var entry in _log.Entries)
        {
            sb.Append('[').Append(entry.Timestamp.ToString("HH:mm:ss")).Append("] ");
            sb.Append(entry.CommandLine);
            if (entry.IsLongLivedStart)
                sb.Append(" — started");
            else if (entry.ExitCode is { } code)
                sb.Append(" — exit ").Append(code);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(entry.Stdout))
            {
                sb.AppendLine(entry.Stdout.TrimEnd());
            }

            if (!string.IsNullOrEmpty(entry.Stderr))
            {
                sb.AppendLine("--- stderr ---");
                sb.AppendLine(entry.Stderr.TrimEnd());
            }

            sb.AppendLine();
        }

        LogText = sb.ToString();
    }
}
