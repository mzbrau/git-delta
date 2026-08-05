using Avalonia;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace CodeReviewr.App.ViewModels;

/// <summary>Visible row in a flat, tree, or search-results file list.</summary>
public partial class FileListEntry : ObservableObject
{
    public FileListEntry(int depth, string label, FileItemViewModel file)
    {
        Depth = depth;
        Label = label;
        File = file;
        IsFolder = false;
        IsSearchGroup = false;
        IsSearchHit = false;
        FolderKey = null;
    }

    public FileListEntry(int depth, string label, string folderKey, bool isExpanded)
    {
        Depth = depth;
        Label = label;
        FolderKey = folderKey;
        IsFolder = true;
        IsSearchGroup = false;
        IsSearchHit = false;
        File = null;
        _isExpanded = isExpanded;
    }

    /// <summary>Expandable file header wrapping nested search hits.</summary>
    public static FileListEntry ForSearchGroup(
        int depth,
        string label,
        FileItemViewModel file,
        bool isExpanded) =>
        new(depth, label, file, isSearchGroup: true, isExpanded);

    /// <summary>Nested search-hit row under a search group.</summary>
    public static FileListEntry ForSearchHit(
        int depth,
        FileItemViewModel file,
        ChangedLineSearch.Hit hit)
    {
        var label = $"{hit.LineNumber}: {hit.Snippet}";
        return new FileListEntry(depth, label, file, hit);
    }

    private FileListEntry(
        int depth,
        string label,
        FileItemViewModel file,
        bool isSearchGroup,
        bool isExpanded)
    {
        Depth = depth;
        Label = label;
        File = file;
        IsFolder = false;
        IsSearchGroup = isSearchGroup;
        IsSearchHit = false;
        FolderKey = isSearchGroup ? file.Path.Value : null;
        _isExpanded = isExpanded;
        HitSide = null;
        HitLine = null;
        HitSnippetMatchIndex = 0;
        HitSnippetMatchLength = 0;
    }

    private FileListEntry(
        int depth,
        string label,
        FileItemViewModel file,
        ChangedLineSearch.Hit hit)
    {
        Depth = depth;
        Label = label;
        File = file;
        IsFolder = false;
        IsSearchGroup = false;
        IsSearchHit = true;
        FolderKey = null;
        HitSide = hit.Side;
        HitLine = hit.LineNumber;
        HitSnippet = hit.Snippet;
        HitSnippetMatchIndex = hit.SnippetMatchIndex;
        HitSnippetMatchLength = hit.SnippetMatchLength;
    }

    public int Depth { get; }
    public string Label { get; }
    public bool IsFolder { get; }
    public bool IsSearchGroup { get; }
    public bool IsSearchHit { get; }
    public string? FolderKey { get; }
    public FileItemViewModel? File { get; }
    public DiffSide? HitSide { get; }
    public int? HitLine { get; }
    public string? HitSnippet { get; }
    public int HitSnippetMatchIndex { get; }
    public int HitSnippetMatchLength { get; }

    /// <summary>Regular file row (not a search group or hit).</summary>
    public bool IsFile => File is not null && !IsSearchGroup && !IsSearchHit;

    /// <summary>Tree folder or search-group header that toggles children.</summary>
    public bool IsExpandable => IsFolder || IsSearchGroup;

    [ObservableProperty] private bool _isExpanded;

    public Thickness IndentMargin => new(Depth * 12, 0, 0, 0);
}

/// <summary>Rebuilds visible <see cref="FileListEntry"/> rows for flat, tree, or search layout.</summary>
public static class FileListLayoutHelper
{
    public static void Rebuild(
        ObservableCollection<FileListEntry> target,
        IReadOnlyList<FileItemViewModel> files,
        FileListLayoutMode mode,
        bool flatUsesFullPath,
        IDictionary<string, bool> expandState)
    {
        target.Clear();
        if (files.Count == 0)
            return;

        if (mode == FileListLayoutMode.Flat)
        {
            foreach (var file in files)
            {
                var label = flatUsesFullPath ? file.Path.Value : file.Name;
                target.Add(new FileListEntry(0, label, file));
            }

            return;
        }

        var byPath = files.ToDictionary(f => f.Path.Value, StringComparer.Ordinal);
        var roots = FileTreeBuilder.Build(byPath.Keys);
        var flat = new List<(FileTreeNode Node, int Depth)>();
        FileTreeBuilder.Flatten(roots, key => IsExpanded(expandState, key), flat);

        foreach (var (node, depth) in flat)
        {
            if (node.IsFolder)
            {
                target.Add(new FileListEntry(
                    depth,
                    node.Label,
                    node.Key,
                    IsExpanded(expandState, node.Key)));
            }
            else if (node.FilePath is not null && byPath.TryGetValue(node.FilePath, out var file))
            {
                target.Add(new FileListEntry(depth, file.Name, file));
            }
        }
    }

    /// <summary>
    /// Flat list of matching files with nested hit rows. Expand state defaults to expanded.
    /// </summary>
    public static void RebuildSearchResults(
        ObservableCollection<FileListEntry> target,
        IReadOnlyList<(FileItemViewModel File, IReadOnlyList<ChangedLineSearch.Hit> Hits)> results,
        bool flatUsesFullPath,
        IDictionary<string, bool> expandState)
    {
        target.Clear();
        foreach (var (file, hits) in results)
        {
            if (hits.Count == 0)
                continue;

            var key = file.Path.Value;
            var expanded = IsExpanded(expandState, key);
            var label = flatUsesFullPath ? file.Path.Value : file.Name;
            target.Add(FileListEntry.ForSearchGroup(0, label, file, expanded));
            if (!expanded)
                continue;

            foreach (var hit in hits)
                target.Add(FileListEntry.ForSearchHit(1, file, hit));
        }
    }

    public static bool IsExpanded(IDictionary<string, bool> expandState, string key)
    {
        if (expandState.TryGetValue(key, out var expanded))
            return expanded;
        expandState[key] = true;
        return true;
    }

    public static void ToggleFolder(
        ObservableCollection<FileListEntry> target,
        IReadOnlyList<FileItemViewModel> files,
        IDictionary<string, bool> expandState,
        string folderKey,
        bool flatUsesFullPath)
    {
        var current = IsExpanded(expandState, folderKey);
        expandState[folderKey] = !current;
        Rebuild(target, files, FileListLayoutMode.Tree, flatUsesFullPath, expandState);
    }
}
