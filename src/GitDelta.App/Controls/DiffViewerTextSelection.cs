using System.Text;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.App.Controls;

/// <summary>Pure text-selection helpers for <see cref="DiffViewer"/> (no Avalonia types).</summary>
public static class DiffViewerTextSelection
{
    public readonly record struct Anchor(int Row, int Col);

    public static bool IsCodeRow(DiffRowKind kind) =>
        kind is DiffRowKind.Context or DiffRowKind.Added or DiffRowKind.Removed;

    /// <summary>Maps a horizontal offset within the code run to a character index in <c>[0, textLength]</c>.</summary>
    public static int CharIndexAtX(double localX, double monoCharWidth, int textLength)
    {
        if (textLength <= 0 || monoCharWidth <= 0)
            return 0;
        if (localX <= 0)
            return 0;
        var idx = (int)Math.Floor(localX / monoCharWidth + 0.5);
        return Math.Clamp(idx, 0, textLength);
    }

    /// <summary>Orders anchor/focus so start is before end in document order.</summary>
    public static (Anchor Start, Anchor End) Normalize(Anchor anchor, Anchor focus)
    {
        if (anchor.Row < focus.Row)
            return (anchor, focus);
        if (anchor.Row > focus.Row)
            return (focus, anchor);
        return anchor.Col <= focus.Col ? (anchor, focus) : (focus, anchor);
    }

    public static bool IsEmpty(Anchor anchor, Anchor focus) =>
        anchor.Row == focus.Row && anchor.Col == focus.Col;

    /// <summary>
    /// Code text for a row in the given view mode / side (raw; caller applies display formatting).
    /// Empty memory when the pane has no text.
    /// </summary>
    public static ReadOnlyMemory<char> GetCodeMemory(DiffRow row, DiffViewMode mode, DiffSide side)
    {
        if (!IsCodeRow(row.Kind))
            return default;

        if (mode == DiffViewMode.SideBySide)
            return side == DiffSide.Old ? row.LeftText : row.RightText;

        var text = row.Kind == DiffRowKind.Removed ? row.LeftText : row.RightText;
        if (text.IsEmpty)
            text = row.LeftText.IsEmpty ? row.RightText : row.LeftText;
        return text;
    }

    /// <summary>
    /// Column range to paint for <paramref name="rowIndex"/> within a normalized selection,
    /// or null when the row is outside the range / not a code row.
    /// </summary>
    public static (int ColStart, int ColEnd)? GetLineColumnRange(
        int rowIndex,
        Anchor start,
        Anchor end,
        int textLength,
        bool isCodeRow)
    {
        if (!isCodeRow || rowIndex < start.Row || rowIndex > end.Row)
            return null;

        var from = rowIndex == start.Row ? start.Col : 0;
        var to = rowIndex == end.Row ? end.Col : textLength;
        from = Math.Clamp(from, 0, textLength);
        to = Math.Clamp(to, 0, textLength);
        if (from > to)
            (from, to) = (to, from);
        return (from, to);
    }

    /// <summary>
    /// Builds plain text for a normalized selection. <paramref name="getDisplayText"/> returns
    /// the visible line string, or null to skip the row (non-code / wrong side).
    /// </summary>
    public static string BuildPlainText(
        Anchor start,
        Anchor end,
        Func<int, string?> getDisplayText)
    {
        var sb = new StringBuilder();
        for (var i = start.Row; i <= end.Row; i++)
        {
            var text = getDisplayText(i);
            if (text is null)
                continue;

            var from = i == start.Row ? start.Col : 0;
            var to = i == end.Row ? end.Col : text.Length;
            from = Math.Clamp(from, 0, text.Length);
            to = Math.Clamp(to, 0, text.Length);
            if (from > to)
                (from, to) = (to, from);

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(text, from, to - from);
        }

        return sb.ToString();
    }
}
