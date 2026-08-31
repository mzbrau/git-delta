namespace GitDelta.App.ViewModels;

/// <summary>Resolves tree folder selections to concrete file rows.</summary>
public static class FileListSelectionHelper
{
    /// <summary>
    /// True when <paramref name="path"/> is exactly <paramref name="folderKey"/>
    /// or a descendant (<c>folderKey/</c> prefix). Keys match <see cref="Core.FileTreeBuilder"/>.
    /// </summary>
    public static bool IsUnderFolder(string path, string folderKey)
    {
        if (string.IsNullOrEmpty(folderKey) || string.IsNullOrEmpty(path))
            return false;

        if (string.Equals(path, folderKey, StringComparison.Ordinal))
            return true;

        return path.Length > folderKey.Length
               && path.StartsWith(folderKey, StringComparison.Ordinal)
               && path[folderKey.Length] == '/';
    }

    /// <summary>Files in <paramref name="files"/> that live under <paramref name="folderKey"/>.</summary>
    public static IEnumerable<FileItemViewModel> FilesUnderFolder(
        IEnumerable<FileItemViewModel> files,
        string folderKey)
    {
        foreach (var file in files)
        {
            if (IsUnderFolder(file.Path.Value, folderKey))
                yield return file;
        }
    }

    /// <summary>
    /// Expands selected list entries into files, resolving folder keys against
    /// <paramref name="sourceFiles"/> (full list, not only visible rows).
    /// </summary>
    public static void CollectFromEntries(
        IEnumerable<object?> selectedItems,
        IReadOnlyList<FileItemViewModel> sourceFiles,
        List<FileItemViewModel> into)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in into)
            seen.Add(existing.Path.Value);

        foreach (var item in selectedItems)
        {
            if (item is FileListEntry { IsSearchGroup: true })
                continue;

            if (item is FileListEntry { IsFolder: true, FolderKey: { } key })
            {
                foreach (var under in FilesUnderFolder(sourceFiles, key))
                {
                    if (seen.Add(under.Path.Value))
                        into.Add(under);
                }
                continue;
            }

            if (item is FileListEntry { File: { } fileItem })
            {
                if (seen.Add(fileItem.Path.Value))
                    into.Add(fileItem);
                continue;
            }

            if (item is FileItemViewModel legacy && seen.Add(legacy.Path.Value))
                into.Add(legacy);
        }
    }
}
