namespace CodeReviewr.Core;

/// <summary>Immutable node produced by <see cref="FileTreeBuilder"/>.</summary>
public sealed class FileTreeNode
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public bool IsFolder { get; init; }
    public string? FilePath { get; init; }
    public IReadOnlyList<FileTreeNode> Children { get; init; } = [];
}

/// <summary>
/// Builds a Bitbucket/SourceTree-style folder tree from repo-relative paths,
/// compressing single-child folder chains into combined labels.
/// </summary>
public static class FileTreeBuilder
{
    public static IReadOnlyList<FileTreeNode> Build(IEnumerable<string> paths)
    {
        var root = new MutableNode { Key = "", Label = "", IsFolder = true };
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var path = raw.Replace('\\', '/').Trim('/');
            if (path.Length == 0)
                continue;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                var key = current.Key.Length == 0 ? segment : current.Key + "/" + segment;
                if (!current.Folders.TryGetValue(segment, out var folder))
                {
                    folder = new MutableNode
                    {
                        Key = key,
                        Label = segment,
                        IsFolder = true,
                    };
                    current.Folders[segment] = folder;
                }

                current = folder;
            }

            var fileName = segments[^1];
            var fileKey = path;
            current.Files[fileName] = new MutableNode
            {
                Key = fileKey,
                Label = fileName,
                IsFolder = false,
                FilePath = path,
            };
        }

        // Never collapse the synthetic root — only compress real folder nodes.
        foreach (var folder in root.Folders.Values)
            Compress(folder);
        return ToImmutableChildren(root);
    }

    public static void Flatten(
        IReadOnlyList<FileTreeNode> roots,
        Func<string, bool> isFolderExpanded,
        IList<(FileTreeNode Node, int Depth)> into)
    {
        foreach (var root in roots)
            FlattenNode(root, 0, isFolderExpanded, into);
    }

    private static void FlattenNode(
        FileTreeNode node,
        int depth,
        Func<string, bool> isFolderExpanded,
        IList<(FileTreeNode Node, int Depth)> into)
    {
        into.Add((node, depth));
        if (!node.IsFolder)
            return;

        if (!isFolderExpanded(node.Key))
            return;

        foreach (var child in node.Children)
            FlattenNode(child, depth + 1, isFolderExpanded, into);
    }

    private static void Compress(MutableNode node)
    {
        foreach (var folder in node.Folders.Values)
            Compress(folder);

        // Collapse single-child folder chains (no sibling files at this level).
        while (node.Files.Count == 0 && node.Folders.Count == 1)
        {
            var only = node.Folders.Values.First();
            node.Label = string.IsNullOrEmpty(node.Label)
                ? only.Label
                : node.Label + "/" + only.Label;
            node.Key = only.Key;
            node.Folders = only.Folders;
            node.Files = only.Files;
        }

        foreach (var folder in node.Folders.Values)
            Compress(folder);
    }

    private static IReadOnlyList<FileTreeNode> ToImmutableChildren(MutableNode node)
    {
        var children = new List<FileTreeNode>();
        foreach (var folder in node.Folders.Values.OrderBy(f => f.Label, StringComparer.Ordinal))
        {
            children.Add(new FileTreeNode
            {
                Key = folder.Key,
                Label = folder.Label,
                IsFolder = true,
                Children = ToImmutableChildren(folder),
            });
        }

        foreach (var file in node.Files.Values.OrderBy(f => f.Label, StringComparer.Ordinal))
        {
            children.Add(new FileTreeNode
            {
                Key = file.Key,
                Label = file.Label,
                IsFolder = false,
                FilePath = file.FilePath,
            });
        }

        return children;
    }

    private sealed class MutableNode
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsFolder { get; set; }
        public string? FilePath { get; set; }
        public Dictionary<string, MutableNode> Folders { get; set; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, MutableNode> Files { get; set; } =
            new(StringComparer.Ordinal);
    }
}
