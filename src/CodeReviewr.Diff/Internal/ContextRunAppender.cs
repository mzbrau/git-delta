using CodeReviewr.Core.Diff;

namespace CodeReviewr.Diff.Internal;

/// <summary>
/// Appends a run of context lines, optionally collapsing the middle while keeping
/// <paramref name="collapseThreshold"/> lines visible at each edge (GitHub-style).
/// </summary>
internal static class ContextRunAppender
{
    public static void Append(
        IReadOnlyList<DiffLine> lines,
        int start,
        int end,
        int hunkIndex,
        List<DiffRow> rows,
        int collapseThreshold,
        ISet<(int HunkIndex, int LineIndexInHunk)>? expandedCollapses)
    {
        var runLength = end - start;
        var edge = collapseThreshold;

        if (collapseThreshold > 0 && runLength > edge * 2)
        {
            var middleStart = start + edge;
            var middleCount = runLength - edge * 2;
            var expanded = expandedCollapses?.Contains((hunkIndex, middleStart)) == true
                || expandedCollapses?.Contains((hunkIndex, start)) == true;

            if (!expanded)
            {
                for (var p = start; p < middleStart; p++)
                    rows.Add(RowFactory.ContextRow(lines[p], hunkIndex, p));

                rows.Add(RowFactory.CollapsedRow(lines[middleStart], hunkIndex, middleStart, middleCount));

                for (var p = middleStart + middleCount; p < end; p++)
                    rows.Add(RowFactory.ContextRow(lines[p], hunkIndex, p));
                return;
            }
        }

        for (var p = start; p < end; p++)
            rows.Add(RowFactory.ContextRow(lines[p], hunkIndex, p));
    }
}
