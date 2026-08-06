using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App;

/// <summary>
/// Shared helpers for building <see cref="DiffOptions"/>, ensuring intra-line spans, and projecting
/// <see cref="DiffRow"/>s for working-copy and review surfaces.
/// </summary>
public static class DiffPresentation
{
    public const int DefaultCollapseThreshold = 8;
    public const int FullFileContextLines = 100_000;

    public static DiffOptions BuildDiffOptions(
        AppSettings settings,
        bool ignoreWhitespace,
        bool showFullFile,
        int contextLines,
        int? fullFileContextLines = null)
    {
        var fullContext = fullFileContextLines ?? FullFileContextLines;
        return settings.ToDiffOptions() with
        {
            IgnoreAllSpace = ignoreWhitespace,
            ContextLines = showFullFile ? fullContext : Math.Max(1, contextLines),
        };
    }

    /// <summary>
    /// Prefer spans already set by <see cref="IntraLineEnricher"/> (git path); only enrich
    /// untracked / unenriched diffs.
    /// </summary>
    public static FileDiff EnsureIntraLine(FileDiff diff, IIntraLineDiffer intraLine)
    {
        if (HasAnyIntraLineSpans(diff))
            return diff;
        return IntraLineEnricher.Enrich(diff, intraLine);
    }

    public static bool HasAnyIntraLineSpans(FileDiff diff)
    {
        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.IntraLine is not null)
                    return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<DiffRow> ProjectRows(
        FileDiff diff,
        DiffViewMode viewMode,
        bool showFullFile,
        IIntraLineDiffer intraLine,
        ISet<(int HunkIndex, int LineIndexInHunk)>? expanded = null,
        int collapseThreshold = DefaultCollapseThreshold)
    {
        var threshold = showFullFile ? 0 : collapseThreshold;
        return viewMode == DiffViewMode.SideBySide
            ? SideBySideRowProjector.Project(diff, threshold, intraLine, expanded)
            : UnifiedRowProjector.Project(diff, threshold, intraLine, expanded);
    }
}
