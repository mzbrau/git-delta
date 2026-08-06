using GitDelta.Core.Diff;
using GitDelta.Diff.Internal;

namespace GitDelta.Diff;

/// <summary>
/// Computes word-level (intra-line) highlighting for a single already-paired removed/added line.
/// Owned rather than delegated to a generic diff library: highlight quality is tunable here, which
/// matters more for review than in most applications (see Plan.md, "Extension seam").
/// </summary>
public interface IIntraLineDiffer
{
    (IReadOnlyList<CharSpan> Old, IReadOnlyList<CharSpan> New) Diff(ReadOnlySpan<char> oldLine, ReadOnlySpan<char> newLine);
}

/// <summary>
/// Word-level LCS differ. Lines are tokenised into runs of word characters, runs of whitespace, and
/// individual punctuation/symbol characters, then the longest common token subsequence is computed
/// to identify which tokens changed. Consecutive changed tokens on each side are merged into a
/// single <see cref="CharSpan"/> so highlighting is not overly fragmented.
/// </summary>
public sealed class IntraLineDiffer : IIntraLineDiffer
{
    /// <summary>Lines longer than this are not word-diffed; the whole line is reported changed instead.</summary>
    private const int MaxLineLength = 2_000;

    /// <summary>Guards the O(n*m) LCS table against pathological token counts (e.g. all-punctuation lines).</summary>
    private const long MaxTokenProduct = 250_000;

    private static readonly IReadOnlyList<CharSpan> Empty = Array.Empty<CharSpan>();

    public (IReadOnlyList<CharSpan> Old, IReadOnlyList<CharSpan> New) Diff(ReadOnlySpan<char> oldLine, ReadOnlySpan<char> newLine)
    {
        if (oldLine.SequenceEqual(newLine))
            return (Empty, Empty);

        if (oldLine.IsEmpty)
            return (Empty, new[] { new CharSpan(0, newLine.Length) });
        if (newLine.IsEmpty)
            return (new[] { new CharSpan(0, oldLine.Length) }, Empty);

        if (oldLine.Length > MaxLineLength || newLine.Length > MaxLineLength)
            return (new[] { new CharSpan(0, oldLine.Length) }, new[] { new CharSpan(0, newLine.Length) });

        var oldTokens = Tokenize(oldLine);
        var newTokens = Tokenize(newLine);

        if ((long)oldTokens.Count * newTokens.Count > MaxTokenProduct)
            return (new[] { new CharSpan(0, oldLine.Length) }, new[] { new CharSpan(0, newLine.Length) });

        var (oldChanged, newChanged) = DiffTokens(oldTokens, oldLine, newTokens, newLine);

        return (MergeChangedSpans(oldTokens, oldChanged), MergeChangedSpans(newTokens, newChanged));
    }

    private static List<(int Start, int Length)> Tokenize(ReadOnlySpan<char> line)
    {
        var tokens = new List<(int Start, int Length)>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                    i++;
                tokens.Add((start, i - start));
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                tokens.Add((start, i - start));
            }
            else
            {
                tokens.Add((i, 1));
                i++;
            }
        }

        return tokens;
    }

    private static bool TokenEquals(
        ReadOnlySpan<char> oldLine, (int Start, int Length) oldToken,
        ReadOnlySpan<char> newLine, (int Start, int Length) newToken)
    {
        if (oldToken.Length != newToken.Length)
            return false;
        return oldLine.Slice(oldToken.Start, oldToken.Length).SequenceEqual(newLine.Slice(newToken.Start, newToken.Length));
    }

    /// <summary>Classic LCS DP + backtrack, applied at token granularity rather than character granularity.</summary>
    private static (bool[] OldChanged, bool[] NewChanged) DiffTokens(
        List<(int Start, int Length)> oldTokens, ReadOnlySpan<char> oldLine,
        List<(int Start, int Length)> newTokens, ReadOnlySpan<char> newLine)
    {
        var n = oldTokens.Count;
        var m = newTokens.Count;
        var dp = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = TokenEquals(oldLine, oldTokens[i], newLine, newTokens[j])
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var oldChanged = new bool[n];
        var newChanged = new bool[m];
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (TokenEquals(oldLine, oldTokens[a], newLine, newTokens[b]))
            {
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                oldChanged[a] = true;
                a++;
            }
            else
            {
                newChanged[b] = true;
                b++;
            }
        }

        while (a < n)
        {
            oldChanged[a] = true;
            a++;
        }

        while (b < m)
        {
            newChanged[b] = true;
            b++;
        }

        return (oldChanged, newChanged);
    }

    private static IReadOnlyList<CharSpan> MergeChangedSpans(List<(int Start, int Length)> tokens, bool[] changed)
    {
        List<CharSpan>? spans = null;
        var i = 0;
        while (i < tokens.Count)
        {
            if (!changed[i])
            {
                i++;
                continue;
            }

            var start = tokens[i].Start;
            var j = i;
            while (j + 1 < tokens.Count && changed[j + 1])
                j++;

            var end = tokens[j].Start + tokens[j].Length;
            (spans ??= new List<CharSpan>()).Add(new CharSpan(start, end - start));
            i = j + 1;
        }

        return spans ?? Empty;
    }
}

/// <summary>Shared helper for pairing removed/added lines within a change block, used by both row projectors.</summary>
internal static class IntraLinePairing
{
    /// <summary>
    /// Returns intra-line spans for a paired removed/added line, preferring spans already computed
    /// (e.g. by <see cref="IntraLineEnricher"/>) over recomputing them on every projection.
    /// </summary>
    public static (IReadOnlyList<CharSpan>? Old, IReadOnlyList<CharSpan>? New) Resolve(
        DiffLine removed, DiffLine added, IIntraLineDiffer? differ)
    {
        if (removed.IntraLine is not null || added.IntraLine is not null)
            return (removed.IntraLine, added.IntraLine);

        if (differ is null)
            return (null, null);

        var (oldSpans, newSpans) = differ.Diff(removed.Text.Span, added.Text.Span);
        return (oldSpans.Count > 0 ? oldSpans : null, newSpans.Count > 0 ? newSpans : null);
    }
}

/// <summary>
/// Enriches a parsed <see cref="FileDiff"/> by populating <see cref="DiffLine.IntraLine"/> for every
/// removed/added line pair, once, so that both row projectors can simply read it back and mode
/// switching never re-runs the differ (Plan.md, "Instant switching").
/// </summary>
public static class IntraLineEnricher
{
    public static FileDiff Enrich(FileDiff diff, IIntraLineDiffer differ)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(differ);

        if (diff.IsBinary || diff.Hunks.Count == 0)
            return diff;

        var changedAny = false;
        var newHunks = new List<DiffHunk>(diff.Hunks.Count);
        foreach (var hunk in diff.Hunks)
        {
            var newLines = EnrichHunkLines(hunk.Lines, differ, ref changedAny);
            newHunks.Add(ReferenceEquals(newLines, hunk.Lines) ? hunk : hunk with { Lines = newLines });
        }

        return changedAny ? diff with { Hunks = newHunks } : diff;
    }

    private static IReadOnlyList<DiffLine> EnrichHunkLines(IReadOnlyList<DiffLine> lines, IIntraLineDiffer differ, ref bool changedAny)
    {
        List<DiffLine>? result = null;
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Kind != DiffLineKind.Removed)
            {
                result?.Add(line);
                i++;
                continue;
            }

            var scan = ChangeBlockScanner.Scan(lines, i);
            var pairCount = Math.Min(scan.RemovedCount, scan.AddedCount);

            result ??= new List<DiffLine>(lines.Take(scan.Start));

            for (var p = 0; p < pairCount; p++)
            {
                var removed = lines[scan.Start + p];
                var added = lines[scan.Start + scan.RemovedCount + p];
                var (oldSpans, newSpans) = differ.Diff(removed.Text.Span, added.Text.Span);

                result.Add(oldSpans.Count > 0 ? removed with { IntraLine = oldSpans } : removed);
                result.Add(newSpans.Count > 0 ? added with { IntraLine = newSpans } : added);
                changedAny |= oldSpans.Count > 0 || newSpans.Count > 0;
            }

            for (var p = pairCount; p < scan.RemovedCount; p++)
                result.Add(lines[scan.Start + p]);
            for (var p = pairCount; p < scan.AddedCount; p++)
                result.Add(lines[scan.Start + scan.RemovedCount + p]);

            i = scan.Start + scan.RemovedCount + scan.AddedCount;
        }

        return (IReadOnlyList<DiffLine>?)result ?? lines;
    }
}
