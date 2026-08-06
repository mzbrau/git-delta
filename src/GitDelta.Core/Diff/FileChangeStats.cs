namespace GitDelta.Core.Diff;

/// <summary>Local (non-AI) add/delete/change-percent stats for a file list row.</summary>
public readonly record struct FileChangeStats(int LinesAdded, int LinesRemoved, int? ChangePercent)
{
    public static FileChangeStats FromDiff(FileDiff diff)
    {
        var added = 0;
        var removed = 0;
        var context = 0;
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Added:
                        added++;
                        break;
                    case DiffLineKind.Removed:
                        removed++;
                        break;
                    case DiffLineKind.Context:
                        context++;
                        break;
                }
            }
        }

        return FromCounts(added, removed, context, diff.Change);
    }

    /// <summary>
    /// Builds stats from known add/delete counts. When <paramref name="totalLines"/> is null,
    /// percent is 100% for added/deleted files and unknown otherwise.
    /// </summary>
    public static FileChangeStats FromCounts(
        int added,
        int removed,
        int? totalLines,
        ChangeKind kind)
    {
        int? percent = kind switch
        {
            ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied => 100,
            ChangeKind.Deleted => 100,
            _ when totalLines is int total && total > 0 =>
                Math.Min(100, (int)Math.Round(100.0 * (added + removed) / total)),
            _ => null,
        };
        return new FileChangeStats(added, removed, percent);
    }

    /// <summary>
    /// Percent = (added + removed) / (context + added + removed), clamped to 100.
    /// Added/deleted files are always 100%.
    /// </summary>
    public static FileChangeStats FromCounts(int added, int removed, int context, ChangeKind kind)
    {
        if (kind is ChangeKind.Added or ChangeKind.Untracked or ChangeKind.Copied or ChangeKind.Deleted)
            return new FileChangeStats(added, removed, 100);

        var denom = context + added + removed;
        if (denom <= 0)
            return new FileChangeStats(added, removed, added + removed > 0 ? 100 : 0);

        var percent = Math.Min(100, (int)Math.Round(100.0 * (added + removed) / denom));
        return new FileChangeStats(added, removed, percent);
    }
}
