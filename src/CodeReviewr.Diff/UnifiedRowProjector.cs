using CodeReviewr.Core.Diff;
using CodeReviewr.Diff.Internal;

namespace CodeReviewr.Diff;

/// <summary>
/// Projects a <see cref="FileDiff"/> into GitHub-style unified rows: hunk headers followed by their
/// lines in patch order, removed lines before added lines within a change block. Pure function of
/// its inputs, so it is cheap to re-run on every mode switch and safe to snapshot-test.
/// </summary>
public static class UnifiedRowProjector
{
    /// <summary>
    /// Projects <paramref name="diff"/> into unified rows.
    /// </summary>
    /// <param name="diff">The canonical, parsed diff to project.</param>
    /// <param name="collapseThreshold">
    /// When greater than zero, a run of consecutive unchanged context lines longer than this
    /// threshold is replaced by a single <see cref="DiffRowKind.Collapsed"/> row. Zero (the default)
    /// disables collapsing and shows every context line.
    /// </param>
    /// <param name="intraLineDiffer">
    /// Optional fallback used to compute word-level highlighting for a change pair whose
    /// <see cref="DiffLine.IntraLine"/> has not already been populated (e.g. via
    /// <see cref="IntraLineEnricher"/>). Passing the same enriched <paramref name="diff"/> on every
    /// call and leaving this <see langword="null"/> is what makes mode switching free of recomputation.
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
                    var runLength = j - start;

                    var expanded = expandedCollapses?.Contains((hunkIndex, start)) == true;
                    if (collapseThreshold > 0 && runLength > collapseThreshold && !expanded)
                    {
                        rows.Add(RowFactory.CollapsedRow(lines[start], hunkIndex, start, runLength));
                    }
                    else
                    {
                        for (var p = start; p < j; p++)
                            rows.Add(RowFactory.ContextRow(lines[p], hunkIndex, p));
                    }

                    i = j;
                    continue;
                }

                case DiffLineKind.Removed:
                case DiffLineKind.Added:
                {
                    var scan = ChangeBlockScanner.Scan(lines, i);
                    var pairCount = Math.Min(scan.RemovedCount, scan.AddedCount);

                    var oldSpans = new IReadOnlyList<CharSpan>?[scan.RemovedCount];
                    var newSpans = new IReadOnlyList<CharSpan>?[scan.AddedCount];
                    for (var p = 0; p < pairCount; p++)
                    {
                        var removed = lines[scan.Start + p];
                        var added = lines[scan.Start + scan.RemovedCount + p];
                        var (o, n) = IntraLinePairing.Resolve(removed, added, differ);
                        oldSpans[p] = o;
                        newSpans[p] = n;
                    }

                    for (var p = 0; p < scan.RemovedCount; p++)
                    {
                        var removed = lines[scan.Start + p];
                        rows.Add(new DiffRow(
                            DiffRowKind.Removed,
                            removed.OldLine,
                            null,
                            removed.Text,
                            ReadOnlyMemory<char>.Empty,
                            oldSpans[p] ?? removed.IntraLine,
                            null,
                            hunkIndex,
                            scan.Start + p));
                    }

                    for (var p = 0; p < scan.AddedCount; p++)
                    {
                        var added = lines[scan.Start + scan.RemovedCount + p];
                        rows.Add(new DiffRow(
                            DiffRowKind.Added,
                            null,
                            added.NewLine,
                            ReadOnlyMemory<char>.Empty,
                            added.Text,
                            null,
                            newSpans[p] ?? added.IntraLine,
                            hunkIndex,
                            scan.Start + scan.RemovedCount + p));
                    }

                    i = scan.Start + scan.RemovedCount + scan.AddedCount;
                    continue;
                }

                default:
                    i++;
                    continue;
            }
        }
    }
}
