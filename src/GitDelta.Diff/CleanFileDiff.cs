using System.Text;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.Diff;

/// <summary>
/// Builds an all-context <see cref="FileDiff"/> so the viewer can show a clean file's contents
/// without painting every line as added (unlike <see cref="UntrackedFileDiff"/>).
/// </summary>
public static class CleanFileDiff
{
    public static FileDiff Create(FilePath path, string content, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Create(path, Encoding.UTF8.GetBytes(content), scope);
    }

    public static FileDiff Create(FilePath path, byte[] bytes, DiffScope scope)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var contentId = ContentId.FromBytes(bytes);
        var isBinary = bytes.AsSpan().Contains((byte)0);

        if (isBinary)
        {
            return new FileDiff(
                scope,
                path,
                path,
                ChangeKind.Modified,
                contentId,
                contentId,
                IsBinary: true,
                Array.Empty<DiffHunk>(),
                RawPatch: string.Empty);
        }

        var content = Encoding.UTF8.GetString(bytes);
        var lines = BuildContextLines(content);
        if (lines.Count == 0)
        {
            return new FileDiff(
                scope,
                path,
                path,
                ChangeKind.Modified,
                contentId,
                contentId,
                IsBinary: false,
                Array.Empty<DiffHunk>(),
                content);
        }

        var header = $"@@ -1,{lines.Count} +1,{lines.Count} @@";
        var hunk = new DiffHunk(1, lines.Count, 1, lines.Count, header, lines);
        return new FileDiff(
            scope,
            path,
            path,
            ChangeKind.Modified,
            contentId,
            contentId,
            IsBinary: false,
            [hunk],
            content);
    }

    private static List<DiffLine> BuildContextLines(string content)
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

            lines.Add(new DiffLine(DiffLineKind.Context, lineNo, lineNo, text));
            lineNo++;

            if (nl == -1)
                break;
            pos = nl + 1;
        }

        return lines;
    }
}
