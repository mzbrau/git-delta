using Avalonia;
using CodeReviewr.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace CodeReviewr.App.ViewModels;

/// <summary>Visible row in a flat or tree file list (folder header or file).</summary>
public partial class FileListEntry : ObservableObject
{
    public FileListEntry(int depth, string label, FileItemViewModel file)
    {
        Depth = depth;
        Label = label;
        File = file;
        IsFolder = false;
        FolderKey = null;
    }

    public FileListEntry(int depth, string label, string folderKey, bool isExpanded)
    {
        Depth = depth;
        Label = label;
        FolderKey = folderKey;
        IsFolder = true;
        File = null;
        _isExpanded = isExpanded;
    }

    public int Depth { get; }
    public string Label { get; }
    public bool IsFolder { get; }
    public string? FolderKey { get; }
    public FileItemViewModel? File { get; }
    public bool IsFile => File is not null;

    [ObservableProperty] private bool _isExpanded;

    public Thickness IndentMargin => new(Depth * 12, 0, 0, 0);
}

/// <summary>Rebuilds visible <see cref="FileListEntry"/> rows for flat or tree layout.</summary>
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

        if (mode == FileListLayoutMode.AiSuggested)
        {
            RebuildAiSuggested(target, files, flatUsesFullPath, expandState);
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

    /// <summary>Folder key for the collapsible "Skip" group at the bottom of the AI-suggested layout.</summary>
    public const string AiSkipFolderKey = "__ai_skip__";

    private static void RebuildAiSuggested(
        ObservableCollection<FileListEntry> target,
        IReadOnlyList<FileItemViewModel> files,
        bool flatUsesFullPath,
        IDictionary<string, bool> expandState)
    {
        var ordered = files
            .OrderByDescending(f => f.AiPriorityStars)
            .ThenBy(f => f.Path.Value, StringComparer.Ordinal)
            .ToList();

        var normal = ordered.Where(f => !f.IsAiSkip).ToList();
        var skip = ordered.Where(f => f.IsAiSkip).ToList();

        foreach (var file in normal)
        {
            var label = flatUsesFullPath ? file.Path.Value : file.Name;
            target.Add(new FileListEntry(0, label, file));
        }

        if (skip.Count == 0)
            return;

        // Skip group defaults to collapsed (unlike tree folders, which default expanded).
        if (!expandState.ContainsKey(AiSkipFolderKey))
            expandState[AiSkipFolderKey] = false;
        var expanded = IsExpanded(expandState, AiSkipFolderKey);

        target.Add(new FileListEntry(0, $"Skip ({skip.Count})", AiSkipFolderKey, expanded));
        if (!expanded)
            return;

        foreach (var file in skip)
        {
            var label = flatUsesFullPath ? file.Path.Value : file.Name;
            target.Add(new FileListEntry(1, label, file));
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
