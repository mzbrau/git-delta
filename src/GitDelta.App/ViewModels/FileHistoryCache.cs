using GitDelta.Core;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitDelta.App.ViewModels;

public enum FileHistoryLoadState
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

public enum FileHistoryCommitFilesLoadState
{
    NotLoaded,
    Loading,
    Ready,
    Failed,
}

/// <summary>Immutable timeline facts for one history row (created / recent / current).</summary>
public sealed record FileHistoryEntry(
    string Oid,
    string ShortOid,
    string Subject,
    DateTimeOffset AuthorDate,
    string AuthorName,
    bool IsCreated,
    bool IsCurrent);

public sealed partial class FileHistoryCommitFileItem : ObservableObject
{
    public FileHistoryCommitFileItem(FilePath path, ChangeKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public FilePath Path { get; }
    public ChangeKind Kind { get; }
    public string Name => Path.Name;
    public string DisplayPath => Path.Value;
}

public sealed partial class FileHistoryItemViewModel : ObservableObject
{
    public FileHistoryItemViewModel(FileHistoryEntry entry)
    {
        Oid = entry.Oid;
        ShortOid = entry.ShortOid;
        Subject = entry.Subject;
        AuthorDate = entry.AuthorDate;
        AuthorName = entry.AuthorName;
        IsCreated = entry.IsCreated;
        IsCurrent = entry.IsCurrent;
    }

    public string Oid { get; }
    public string ShortOid { get; }
    public string Subject { get; }
    public DateTimeOffset AuthorDate { get; }
    public string AuthorName { get; }
    public bool IsCreated { get; }
    public bool IsCurrent { get; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private FileHistoryCommitFilesLoadState _filesState = FileHistoryCommitFilesLoadState.NotLoaded;
    [ObservableProperty] private string? _filesErrorMessage;

    public ObservableCollection<FileHistoryCommitFileItem> CommitFiles { get; } = [];

    public bool IsFilesNotLoaded => FilesState == FileHistoryCommitFilesLoadState.NotLoaded;
    public bool IsFilesLoading => FilesState == FileHistoryCommitFilesLoadState.Loading;
    public bool IsFilesReady => FilesState == FileHistoryCommitFilesLoadState.Ready;
    public bool IsFilesFailed => FilesState == FileHistoryCommitFilesLoadState.Failed;
    public bool CanExpand => !IsCurrent && !string.IsNullOrEmpty(Oid);

    partial void OnFilesStateChanged(FileHistoryCommitFilesLoadState value)
    {
        OnPropertyChanged(nameof(IsFilesNotLoaded));
        OnPropertyChanged(nameof(IsFilesLoading));
        OnPropertyChanged(nameof(IsFilesReady));
        OnPropertyChanged(nameof(IsFilesFailed));
    }

    public void ApplyFiles(IReadOnlyList<(FilePath Path, ChangeKind Kind)> files)
    {
        CommitFiles.Clear();
        foreach (var (path, kind) in files)
            CommitFiles.Add(new FileHistoryCommitFileItem(path, kind));
        FilesErrorMessage = null;
        FilesState = FileHistoryCommitFilesLoadState.Ready;
    }

    public void ApplyFilesFailure(string message)
    {
        FilesErrorMessage = message;
        FilesState = FileHistoryCommitFilesLoadState.Failed;
    }
}

/// <summary>In-memory cache of on-demand file history timelines, keyed by session+path.</summary>
public sealed class FileHistoryCache
{
    private readonly Dictionary<string, FileHistoryCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public FileHistoryCacheEntry GetOrCreate(string sessionKey, string path)
    {
        var key = CacheKey(sessionKey, path);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
                return existing;

            var created = new FileHistoryCacheEntry(sessionKey, path);
            _entries[key] = created;
            return created;
        }
    }

    public void ClearSession(string sessionKey)
    {
        lock (_lock)
        {
            var prefix = sessionKey + "\0";
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _entries.Remove(key);
        }
    }

    /// <summary>Drops every cached entry regardless of session (e.g. when switching repositories).</summary>
    public void ClearAll()
    {
        lock (_lock)
            _entries.Clear();
    }

    private static string CacheKey(string sessionKey, string path) => sessionKey + "\0" + path;
}

public sealed partial class FileHistoryCacheEntry : ObservableObject
{
    public FileHistoryCacheEntry(string sessionKey, string path)
    {
        SessionKey = sessionKey;
        Path = path;
    }

    public string SessionKey { get; }
    public string Path { get; }

    [ObservableProperty] private FileHistoryLoadState _state = FileHistoryLoadState.NotLoaded;
    [ObservableProperty] private string? _errorMessage;
    public ObservableCollection<FileHistoryItemViewModel> Entries { get; } = [];

    public bool IsNotLoaded => State == FileHistoryLoadState.NotLoaded;
    public bool IsLoading => State == FileHistoryLoadState.Loading;
    public bool IsReady => State == FileHistoryLoadState.Ready;
    public bool IsFailed => State == FileHistoryLoadState.Failed;

    partial void OnStateChanged(FileHistoryLoadState value)
    {
        OnPropertyChanged(nameof(IsNotLoaded));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
    }

    public void ApplyResult(IReadOnlyList<FileHistoryEntry> entries)
    {
        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(new FileHistoryItemViewModel(entry));
        ErrorMessage = null;
        State = FileHistoryLoadState.Ready;
    }

    public void ApplyFailure(string message)
    {
        ErrorMessage = message;
        State = FileHistoryLoadState.Failed;
    }

    public void ClearSelection()
    {
        foreach (var entry in Entries)
            entry.IsSelected = false;
    }

    public static IReadOnlyList<FileHistoryEntry> BuildTimeline(
        CommitInfo? created,
        IReadOnlyList<CommitInfo> recent,
        int maxRecent = 4)
    {
        var list = new List<FileHistoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (created is not null)
        {
            seen.Add(created.Oid);
            list.Add(new FileHistoryEntry(
                created.Oid,
                created.ShortOid,
                created.Subject,
                created.AuthorDate,
                created.AuthorName,
                IsCreated: true,
                IsCurrent: false));
        }

        foreach (var commit in recent.Take(maxRecent))
        {
            if (!seen.Add(commit.Oid))
                continue;

            list.Add(new FileHistoryEntry(
                commit.Oid,
                commit.ShortOid,
                commit.Subject,
                commit.AuthorDate,
                commit.AuthorName,
                IsCreated: false,
                IsCurrent: false));
        }

        list.Sort((a, b) => a.AuthorDate.CompareTo(b.AuthorDate));

        list.Add(new FileHistoryEntry(
            Oid: "",
            ShortOid: "",
            Subject: "Current change",
            AuthorDate: DateTimeOffset.UtcNow,
            AuthorName: "",
            IsCreated: false,
            IsCurrent: true));

        return list;
    }
}
