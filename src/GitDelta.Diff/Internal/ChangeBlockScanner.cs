using GitDelta.Core.Diff;

namespace GitDelta.Diff.Internal;

/// <summary>A contiguous run of removed lines immediately followed by a contiguous run of added lines.</summary>
internal readonly record struct ChangeBlock(int Start, int RemovedCount, int AddedCount);

/// <summary>
/// Scans a "change block" starting at <paramref name="start"/>. Git always emits removed lines
/// before added lines within a change, so this handles pure deletions (<c>AddedCount == 0</c>),
/// pure additions (<c>RemovedCount == 0</c>, when <paramref name="start"/> already points at an
/// added line), and modifications uniformly.
/// </summary>
internal static class ChangeBlockScanner
{
    public static ChangeBlock Scan(IReadOnlyList<DiffLine> lines, int start)
    {
        var j = start;
        while (j < lines.Count && lines[j].Kind == DiffLineKind.Removed)
            j++;
        var removedCount = j - start;

        var k = j;
        while (k < lines.Count && lines[k].Kind == DiffLineKind.Added)
            k++;
        var addedCount = k - j;

        return new ChangeBlock(start, removedCount, addedCount);
    }
}
