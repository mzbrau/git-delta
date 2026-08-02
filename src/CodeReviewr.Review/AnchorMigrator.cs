using System.Text;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.Review;

public static class AnchorMigrator
{
    public static ReviewThread Migrate(
        ReviewThread thread,
        DiffSide side,
        ContentId targetContentId,
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<string> targetLines,
        int sourceLine,
        int? sourceStartLine)
    {
        var mappedLine = LineMapper.TryMapLine(sourceLine, sourceLines, targetLines);
        if (mappedLine is null)
        {
            var contextLine = thread.OriginalLine ?? sourceLine;
            return thread with
            {
                Anchor = null,
                IsUnplaceable = true,
                IsOutdated = true,
                ContextLines = LineMapper.BuildContextLines(sourceLines, contextLine),
            };
        }

        int? mappedStart = null;
        if (sourceStartLine is not null)
            mappedStart = LineMapper.TryMapLine(sourceStartLine.Value, sourceLines, targetLines);

        var startLine = mappedStart ?? mappedLine.Value;
        var endLine = mappedLine.Value;
        if (startLine > endLine)
            (startLine, endLine) = (endLine, startLine);

        var startAnchor = new DiffAnchor(side, targetContentId, startLine);
        var endAnchor = new DiffAnchor(side, targetContentId, endLine);
        var anchor = startLine == endLine
            ? new AnnotationRange(startAnchor, startAnchor)
            : new AnnotationRange(startAnchor, endAnchor);

        return thread with
        {
            Anchor = anchor,
            IsUnplaceable = false,
            IsOutdated = true,
            Side = side,
            Line = mappedLine,
            StartLine = mappedStart,
        };
    }

    public static async Task<IReadOnlyList<string>> ReadBlobLinesAsync(
        string repositoryPath,
        ContentId contentId,
        IGitObjectReader objectReader,
        CancellationToken ct)
    {
        var bytes = await objectReader.ReadBlobAsync(repositoryPath, contentId, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return text.Replace("\r\n", "\n").Split('\n').ToList();
    }
}
