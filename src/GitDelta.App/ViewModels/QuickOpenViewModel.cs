using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.App.ViewModels;

/// <summary>Ctrl+T quick-open overlay over tracked repository files.</summary>
public sealed partial class QuickOpenViewModel : ObservableObject
{
    private readonly IGitHistoryService _history;
    private IReadOnlyList<FilePath> _allFiles = [];
    private string? _loadedRepoPath;

    public QuickOpenViewModel(IGitHistoryService history)
    {
        _history = history;
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private FilePath? _selectedResult;

    public ObservableCollection<FilePath> Results { get; } = [];

    public event Func<FilePath, Task>? FileChosen;
    public event Action? FocusRequested;

    partial void OnQueryChanged(string value) => ApplyFilter();

    public async Task ShowAsync(string repositoryPath) =>
        await OpenAsync(repositoryPath).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenAsync(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return;

        IsOpen = true;
        Query = "";
        SelectedResult = null;
        FocusRequested?.Invoke();
        await EnsureFilesAsync(repositoryPath).ConfigureAwait(true);
        ApplyFilter();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        Query = "";
        SelectedResult = null;
        Results.Clear();
    }

    [RelayCommand]
    private async Task AcceptAsync(FilePath? path)
    {
        FilePath? chosen = path ?? SelectedResult;
        if (chosen is null && Results.Count > 0)
            chosen = Results[0];
        if (chosen is null)
            return;

        Close();
        if (FileChosen is not null)
            await FileChosen(chosen.Value).ConfigureAwait(false);
    }

    private async Task EnsureFilesAsync(string repositoryPath)
    {
        if (string.Equals(_loadedRepoPath, repositoryPath, StringComparison.Ordinal) && _allFiles.Count > 0)
            return;

        IsLoading = true;
        try
        {
            _allFiles = await _history.ListTrackedFilesAsync(repositoryPath).ConfigureAwait(true);
            _loadedRepoPath = repositoryPath;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Results.Clear();
        var q = Query.Trim();
        IEnumerable<FilePath> match = _allFiles;
        if (!string.IsNullOrEmpty(q))
        {
            match = _allFiles
                .Where(p => p.Value.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || IsSubsequenceMatch(p.Value, q))
                .OrderBy(p => Score(p.Value, q))
                .ThenBy(p => p.Value, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var path in match.Take(50))
            Results.Add(path);

        SelectedResult = Results.FirstOrDefault();
    }

    private static int Score(string path, string query)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    private static bool IsSubsequenceMatch(string haystack, string needle)
    {
        var hi = 0;
        for (var ni = 0; ni < needle.Length; ni++)
        {
            var c = char.ToLowerInvariant(needle[ni]);
            var found = false;
            while (hi < haystack.Length)
            {
                if (char.ToLowerInvariant(haystack[hi++]) == c)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}
