using System.Text;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Diff;

/// <summary>
/// Builds an all-added <see cref="FileDiff"/> for an untracked worktree file so the viewer can
/// show the full contents without calling <c>git diff</c> (which returns empty for untracked paths).
/// </summary>
public static class UntrackedFileDiff
{
    public static FileDiff Create(FilePath path, string content, DiffTarget target = DiffTarget.IndexToWorktree) =>
        Create(path, content, target.AsWorkingCopy());

    public static FileDiff Create(FilePath path, byte[] bytes, DiffTarget target = DiffTarget.IndexToWorktree) =>
        Create(path, bytes, target.AsWorkingCopy());

    public static FileDiff Create(FilePath path, string content, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Create(path, Encoding.UTF8.GetBytes(content), scope);
    }

    public static FileDiff Create(FilePath path, byte[] bytes, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var newContent = ContentId.FromBytes(bytes);
        var isBinary = bytes.AsSpan().Contains((byte)0);

        if (isBinary)
        {
            return new FileDiff(
                scope,
                path,
                path,
                ChangeKind.Untracked,
                ContentId.Empty,
                newContent,
                IsBinary: true,
                Array.Empty<DiffHunk>(),
                RawPatch: string.Empty);
        }

        var content = Encoding.UTF8.GetString(bytes);
        var lines = BuildAddedLines(content);
        if (lines.Count == 0)
        {
            return new FileDiff(
                scope,
                path,
                path,
                ChangeKind.Untracked,
                ContentId.Empty,
                newContent,
                IsBinary: false,
                Array.Empty<DiffHunk>(),
                content);
        }

        var header = $"@@ -0,0 +1,{lines.Count} @@";
        var hunk = new DiffHunk(0, 0, 1, lines.Count, header, lines);
        return new FileDiff(
            scope,
            path,
            path,
            ChangeKind.Untracked,
            ContentId.Empty,
            newContent,
            IsBinary: false,
            [hunk],
            content);
    }

    private static List<DiffLine> BuildAddedLines(string content)
    {
        var memory = content.AsMemory();
        var lines = new List<DiffLine>();
        var pos = 0;
        var lineNo = 1;

        while (pos < content.Length)
        {
            var nl = content.IndexOf('\n', pos);
            var end = nl == -1 ? content.Length : nl;
            var len = end - pos;
            var text = len > 0 ? memory.Slice(pos, len) : ReadOnlyMemory<char>.Empty;
            if (text.Length > 0 && text.Span[^1] == '\r')
                text = text[..^1];

            lines.Add(new DiffLine(DiffLineKind.Added, null, lineNo, text));
            lineNo++;

            if (nl == -1)
                break;
            pos = nl + 1;
        }

        return lines;
    }
}
