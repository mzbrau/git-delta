using GitDelta.Core.Diff;

namespace GitDelta.Diff.Internal;

/// <summary>Row construction shared verbatim between <see cref="UnifiedRowProjector"/> and <see cref="SideBySideRowProjector"/>.</summary>
internal static class RowFactory
{
    public static DiffRow HunkHeaderRow(DiffHunk hunk, int hunkIndex) =>
        new(
            DiffRowKind.HunkHeader,
            hunk.OldStart,
            hunk.NewStart,
            hunk.Header.AsMemory(),
            ReadOnlyMemory<char>.Empty,
            null,
            null,
            hunkIndex,
            -1);

    public static DiffRow ContextRow(DiffLine line, int hunkIndex, int lineIndexInHunk) =>
        new(
            DiffRowKind.Context,
            line.OldLine,
            line.NewLine,
            line.Text,
            line.Text,
            line.IntraLine,
            line.IntraLine,
            hunkIndex,
            lineIndexInHunk);

    public static DiffRow CollapsedRow(DiffLine anchor, int hunkIndex, int lineIndexInHunk, int collapsedCount) =>
        new(
            DiffRowKind.Collapsed,
            anchor.OldLine,
            anchor.NewLine,
            ReadOnlyMemory<char>.Empty,
            ReadOnlyMemory<char>.Empty,
            null,
            null,
            hunkIndex,
            lineIndexInHunk,
            IsCollapsedAnchor: true,
            CollapsedCount: collapsedCount);
}
