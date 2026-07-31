using CodeReviewr.Core.Diff;

namespace CodeReviewr.Diff;

/// <summary>
/// Presentation helpers for <see cref="DiffRow"/>. Side-by-side projectors collapse mixed
/// change blocks into a single <see cref="DiffRow.Kind"/>; renderers must paint each pane
/// from line-number presence instead.
/// </summary>
public static class DiffRowPresentation
{
    public static bool IsChangeRow(DiffRowKind kind) =>
        kind is DiffRowKind.Added or DiffRowKind.Removed or DiffRowKind.Padding;

    /// <summary>Paint kind for the left (old) pane in side-by-side view.</summary>
    public static DiffRowKind SideBySideLeftKind(DiffRow row) =>
        IsChangeRow(row.Kind) && row.OldLineNumber.HasValue
            ? DiffRowKind.Removed
            : DiffRowKind.Context;

    /// <summary>Paint kind for the right (new) pane in side-by-side view.</summary>
    public static DiffRowKind SideBySideRightKind(DiffRow row) =>
        IsChangeRow(row.Kind) && row.NewLineNumber.HasValue
            ? DiffRowKind.Added
            : DiffRowKind.Context;
}
