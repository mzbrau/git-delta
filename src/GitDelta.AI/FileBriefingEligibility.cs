using GitDelta.Core;
using GitDelta.Core.AI;

namespace GitDelta.AI;

/// <summary>Decides whether a changed file should get an automatic file briefing.</summary>
public static class FileBriefingEligibility
{
    public static bool IsEligible(
        int? changePercent,
        int linesAdded,
        int linesRemoved,
        int minChangePercent,
        int minLinesChanged)
    {
        var linesChanged = linesAdded + linesRemoved;
        if (linesChanged < minLinesChanged)
            return false;

        // Unknown percent: eligible when the line threshold is met.
        if (changePercent is null)
            return true;

        return changePercent.Value >= minChangePercent;
    }

    public static bool IsEligible(AiChangedFileFact file, AppSettings settings) =>
        IsEligible(
            file.ChangePercent,
            file.LinesAdded ?? 0,
            file.LinesRemoved ?? 0,
            settings.AiFileBriefingMinChangePercent,
            settings.AiFileBriefingMinLinesChanged);
}
