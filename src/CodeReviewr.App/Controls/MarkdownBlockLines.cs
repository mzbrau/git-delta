using Markdig.Syntax;

namespace CodeReviewr.App.Controls;

/// <summary>Maps Markdig blocks to 1-based source line ranges in the original markdown.</summary>
public static class MarkdownBlockLines
{
    /// <summary>
    /// Returns the inclusive 1-based source line range for <paramref name="block"/>.
    /// Uses <see cref="Block.Line"/> (0-based) and newline counts within <see cref="Block.Span"/>.
    /// </summary>
    public static (int StartLine, int EndLine) GetRange(Block block, string markdown)
    {
        ArgumentNullException.ThrowIfNull(block);

        var startLine = block.Line + 1;
        if (string.IsNullOrEmpty(markdown) || block.Span.Length <= 0)
            return (startLine, startLine);

        var start = Math.Clamp(block.Span.Start, 0, markdown.Length);
        var endExclusive = Math.Clamp(block.Span.Start + block.Span.Length, start, markdown.Length);
        if (endExclusive <= start)
            return (startLine, startLine);

        // Trailing newline in the span belongs to the block terminator, not an extra content line.
        var last = endExclusive - 1;
        if (markdown[last] is '\n' or '\r')
        {
            endExclusive = last;
            if (endExclusive > start && markdown[endExclusive - 1] == '\r' && markdown[last] == '\n')
                endExclusive--;
        }

        if (endExclusive <= start)
            return (startLine, startLine);

        var endLine = startLine;
        for (var i = start; i < endExclusive; i++)
        {
            if (markdown[i] == '\n')
                endLine++;
        }

        return (startLine, Math.Max(startLine, endLine));
    }
}
