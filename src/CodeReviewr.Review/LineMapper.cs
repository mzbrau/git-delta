using System.Text;

namespace CodeReviewr.Review;

/// <summary>
/// Maps line numbers between two versions of a file using longest-common-subsequence on lines.
/// </summary>
public static class LineMapper
{
    public static IReadOnlyDictionary<int, int> MapLines(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var lcs = ComputeLcs(oldLines, newLines);
        var mapping = new Dictionary<int, int>();
        foreach (var (oldIndex, newIndex) in lcs)
        {
            mapping[oldIndex + 1] = newIndex + 1;
        }

        return mapping;
    }

    public static int? TryMapLine(int oldOneBasedLine, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        if (oldOneBasedLine < 1 || oldOneBasedLine > oldLines.Count)
            return null;

        var mapping = MapLines(oldLines, newLines);
        return mapping.TryGetValue(oldOneBasedLine, out var mapped) ? mapped : null;
    }

    public static string BuildContextLines(IReadOnlyList<string> lines, int oneBasedLine, int radius = 3)
    {
        if (lines.Count == 0 || oneBasedLine < 1)
            return string.Empty;

        var start = Math.Max(1, oneBasedLine - radius);
        var end = Math.Min(lines.Count, oneBasedLine + radius);
        var sb = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            sb.Append(i == oneBasedLine ? "> " : "  ");
            sb.AppendLine(lines[i - 1]);
        }

        return sb.ToString().TrimEnd();
    }

    private static List<(int OldIndex, int NewIndex)> ComputeLcs(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines)
    {
        var m = oldLines.Count;
        var n = newLines.Count;
        var dp = new int[m + 1, n + 1];

        for (var i = 1; i <= m; i++)
        for (var j = 1; j <= n; j++)
        {
            if (oldLines[i - 1] == newLines[j - 1])
                dp[i, j] = dp[i - 1, j - 1] + 1;
            else
                dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
        }

        var pairs = new List<(int, int)>();
        var o = m;
        var p = n;
        while (o > 0 && p > 0)
        {
            if (oldLines[o - 1] == newLines[p - 1])
            {
                pairs.Add((o - 1, p - 1));
                o--;
                p--;
            }
            else if (dp[o - 1, p] >= dp[o, p - 1])
                o--;
            else
                p--;
        }

        pairs.Reverse();
        return pairs;
    }
}
