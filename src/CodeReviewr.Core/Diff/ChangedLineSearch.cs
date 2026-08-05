namespace CodeReviewr.Core.Diff;

/// <summary>Searches added/removed lines in a <see cref="FileDiff"/> for a substring.</summary>
public static class ChangedLineSearch
{
    public const int DefaultSnippetRadius = 40;

    public readonly record struct Hit(
        DiffSide Side,
        int LineNumber,
        string LineText,
        string Snippet,
        int SnippetMatchIndex,
        int SnippetMatchLength);

    public readonly record struct FormattedSnippet(string Text, int MatchIndex, int MatchLength);

    /// <summary>
    /// Finds case-insensitive substring matches on <see cref="DiffLineKind.Added"/> and
    /// <see cref="DiffLineKind.Removed"/> lines only (context lines are ignored).
    /// </summary>
    public static IReadOnlyList<Hit> FindHits(FileDiff diff, string query, int snippetRadius = DefaultSnippetRadius)
    {
        if (diff.IsBinary || string.IsNullOrWhiteSpace(query))
            return [];

        var needle = query.Trim();
        if (needle.Length == 0)
            return [];

        var hits = new List<Hit>();
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind is not (DiffLineKind.Added or DiffLineKind.Removed))
                    continue;

                var text = line.Text.ToString();
                var index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;

                DiffSide side;
                int lineNumber;
                if (line.Kind == DiffLineKind.Removed)
                {
                    if (line.OldLine is not { } oldLine)
                        continue;
                    side = DiffSide.Old;
                    lineNumber = oldLine;
                }
                else
                {
                    if (line.NewLine is not { } newLine)
                        continue;
                    side = DiffSide.New;
                    lineNumber = newLine;
                }

                var snippet = FormatSnippetParts(text, index, needle.Length, snippetRadius);
                hits.Add(new Hit(
                    side,
                    lineNumber,
                    text,
                    snippet.Text,
                    snippet.MatchIndex,
                    snippet.MatchLength));
            }
        }

        return hits;
    }

    /// <summary>
    /// Builds a display snippet with up to <paramref name="radius"/> characters before and after
    /// the match, truncating the rest with ellipses. Leading whitespace is stripped so indented
    /// lines start at the first non-whitespace character in the preview.
    /// </summary>
    public static string FormatSnippet(string lineText, int matchIndex, int matchLength, int radius = DefaultSnippetRadius) =>
        FormatSnippetParts(lineText, matchIndex, matchLength, radius).Text;

    /// <summary>
    /// Same as <see cref="FormatSnippet"/> but also returns the match offset/length within the
    /// returned snippet text (accounting for trim, windowing, and ellipses).
    /// </summary>
    public static FormattedSnippet FormatSnippetParts(
        string lineText,
        int matchIndex,
        int matchLength,
        int radius = DefaultSnippetRadius)
    {
        if (string.IsNullOrEmpty(lineText))
            return new FormattedSnippet("", 0, 0);

        var trimOffset = 0;
        while (trimOffset < lineText.Length && char.IsWhiteSpace(lineText[trimOffset]))
            trimOffset++;

        if (trimOffset > 0)
        {
            lineText = lineText[trimOffset..];
            matchIndex -= trimOffset;
        }

        if (lineText.Length == 0)
            return new FormattedSnippet("", 0, 0);

        if (matchIndex < 0 || matchIndex >= lineText.Length)
        {
            var truncated = Truncate(lineText, radius * 2 + Math.Max(matchLength, 0));
            return new FormattedSnippet(truncated, 0, 0);
        }

        var length = Math.Clamp(matchLength, 0, lineText.Length - matchIndex);
        var start = Math.Max(0, matchIndex - radius);
        var end = Math.Min(lineText.Length, matchIndex + length + radius);

        var prefix = start > 0 ? "…" : "";
        var suffix = end < lineText.Length ? "…" : "";
        var text = prefix + lineText[start..end] + suffix;
        var snippetMatchIndex = prefix.Length + (matchIndex - start);
        return new FormattedSnippet(text, snippetMatchIndex, length);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "…";
    }
}
