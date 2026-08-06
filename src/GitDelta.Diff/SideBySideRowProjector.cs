using GitDelta.Core.Diff;
using GitDelta.Diff.Internal;

namespace GitDelta.Diff;

/// <summary>
/// Projects a <see cref="FileDiff"/> into side-by-side rows. Within a change block, deletions pair
/// with additions positionally; the shorter side is padded with <see cref="DiffRowKind.Padding"/>
/// rows so both columns stay vertically aligned. Pure function of its inputs, so switching from
/// <see cref="UnifiedRowProjector"/> and back needs no re-parse, no re-tokenise, and no re-diff.
/// </summary>
public static class SideBySideRowProjector
{
    /// <summary>
    /// Projects <paramref name="diff"/> into side-by-side rows.
    /// </summary>
    /// <param name="diff">The canonical, parsed diff to project.</param>
    /// <param name="collapseThreshold">
    /// When greater than zero, a run of consecutive unchanged context lines longer than
    /// <c>2 * collapseThreshold</c> keeps that many lines at each edge and replaces the middle
    /// with a single <see cref="DiffRowKind.Collapsed"/> row. Zero disables collapsing.
    /// </param>
    /// <param name="intraLineDiffer">
    /// Optional fallback used to compute word-level highlighting for a change pair whose
    /// <see cref="DiffLine.IntraLine"/> has not already been populated.
    /// </param>
    public static IReadOnlyList<DiffRow> Project(
        FileDiff diff,
        int collapseThreshold = 0,
        IIntraLineDiffer? intraLineDiffer = null,
        ISet<(int HunkIndex, int LineIndexInHunk)>? expandedCollapses = null)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var rows = new List<DiffRow>();
        if (diff.IsBinary)
            return rows;

        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            rows.Add(RowFactory.HunkHeaderRow(hunk, h));
            AppendHunkRows(hunk.Lines, h, rows, collapseThreshold, intraLineDiffer, expandedCollapses);
        }

        return rows;
    }

    private static void AppendHunkRows(
        IReadOnlyList<DiffLine> lines,
        int hunkIndex,
        List<DiffRow> rows,
        int collapseThreshold,
        IIntraLineDiffer? differ,
        ISet<(int HunkIndex, int LineIndexInHunk)>? expandedCollapses)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            switch (line.Kind)
            {
                case DiffLineKind.NoNewlineAtEof:
                    i++;
                    continue;

                case DiffLineKind.Context:
                {
                    var start = i;
                    var j = i;
                    while (j < lines.Count && lines[j].Kind == DiffLineKind.Context)
                        j++;
                    ContextRunAppender.Append(lines, start, j, hunkIndex, rows, collapseThreshold, expandedCollapses);
                    i = j;
                    continue;
                }

                case DiffLineKind.Removed:
                case DiffLineKind.Added:
                {
                    var scan = ChangeBlockScanner.Scan(lines, i);
                    AppendChangeBlock(lines, scan, hunkIndex, rows, differ);
                    i = scan.Start + scan.RemovedCount + scan.AddedCount;
                    continue;
                }

                default:
                    i++;
                    continue;
            }
        }
    }

    /// <summary>
    /// Emits one row per position in <c>[0, max(removedCount, addedCount))</c>.
    ///
    /// A position within <c>[0, min(removedCount, addedCount))</c> is a genuine positional pair
    /// (both sides real, intra-line highlighted, tagged <see cref="DiffRowKind.Removed"/> as the
    /// row's primary kind since a single <see cref="DiffRow.Kind"/> cannot describe two
    /// independently-changed sides; renderers should key per-side behaviour off
    /// <see cref="DiffRow.OldLineNumber"/>/<see cref="DiffRow.NewLineNumber"/> instead).
    ///
    /// A position beyond that in a purely one-sided block (only removed or only added lines, no
    /// pairing attempted at all — e.g. a whole function deleted with nothing added back) keeps its
    /// natural <see cref="DiffRowKind.Removed"/>/<see cref="DiffRowKind.Added"/> kind.
    ///
    /// A position beyond the shorter side within a genuinely mixed block (both removals and
    /// additions present, but counts differ) is the case Plan.md calls out explicitly: it is tagged
    /// <see cref="DiffRowKind.Padding"/>, while the longer side's real content still populates that
    /// row's own Text/LineNumber fields.
    /// </summary>
    private static void AppendChangeBlock(
        IReadOnlyList<DiffLine> lines, ChangeBlock scan, int hunkIndex, List<DiffRow> rows, IIntraLineDiffer? differ)
    {
        var pairCount = Math.Min(scan.RemovedCount, scan.AddedCount);
        var maxCount = Math.Max(scan.RemovedCount, scan.AddedCount);
        var isMixedBlock = scan.RemovedCount > 0 && scan.AddedCount > 0;

        for (var p = 0; p < maxCount; p++)
        {
            DiffLine? removed = p < scan.RemovedCount ? lines[scan.Start + p] : null;
            DiffLine? added = p < scan.AddedCount ? lines[scan.Start + scan.RemovedCount + p] : null;

            IReadOnlyList<CharSpan>? oldSpans = removed?.IntraLine;
            IReadOnlyList<CharSpan>? newSpans = added?.IntraLine;
            if (removed is { } r && added is { } a && (oldSpans is null || newSpans is null))
            {
                var resolved = IntraLinePairing.Resolve(r, a, differ);
                oldSpans = resolved.Old;
                newSpans = resolved.New;
            }

            var kind = (removed, added) switch
            {
                ({ }, { }) => DiffRowKind.Removed,
                ({ }, null) => isMixedBlock ? DiffRowKind.Padding : DiffRowKind.Removed,
                (null, { }) => isMixedBlock ? DiffRowKind.Padding : DiffRowKind.Added,
                (null, null) => DiffRowKind.Padding,
            };

            // A row can carry two real DiffLine instances (removed and added) but only one
            // LineIndexInHunk slot. Prefer the removed line's actual index in hunk.Lines when one
            // is present, falling back to the added line's actual index otherwise; this keeps the
            // value always valid for at least one side, at the cost of not being able to recover
            // the paired line's index from the row alone (callers doing partial side-by-side
            // staging on a fully-paired row must track both sides explicitly).
            var lineIndexInHunk = removed is not null ? scan.Start + p : scan.Start + scan.RemovedCount + p;

            rows.Add(new DiffRow(
                kind,
                removed?.OldLine,
                added?.NewLine,
                removed?.Text ?? ReadOnlyMemory<char>.Empty,
                added?.Text ?? ReadOnlyMemory<char>.Empty,
                removed is not null ? oldSpans : null,
                added is not null ? newSpans : null,
                hunkIndex,
                lineIndexInHunk));
        }
    }
}
