using System.Collections.Generic;
using Avalonia.Media;
using GitDelta.Diff;

namespace GitDelta.App.Controls;

public sealed partial class DiffViewer
{
    private readonly record struct LinePaintKey(int RowIndex, byte Side, int Epoch);

    private sealed class LinePaintCache(
        string text,
        IReadOnlyList<(FormattedText Ft, double Width)> segments,
        double totalWidth)
    {
        public string Text { get; } = text;
        public IReadOnlyList<(FormattedText Ft, double Width)> Segments { get; } = segments;
        public double TotalWidth { get; } = totalWidth;
    }

    private void ClearPaintCache()
    {
        ClearLinePaintCache();
        _gutterCache.Clear();
        _prefixCache.Clear();
    }

    private void ClearLinePaintCache()
    {
        _paintEpoch++;
        _linePaintCache.Clear();
        _paintWarmCursor = -1;
    }

    private LinePaintCache GetOrCreateLinePaint(
        int rowIndex,
        byte side,
        string text,
        IBrush fallback,
        FileSyntaxTokens? tokens,
        int? oneBasedLine)
    {
        var key = new LinePaintKey(rowIndex, side, _paintEpoch);
        if (_linePaintCache.TryGetValue(key, out var cached) && cached.Text == text)
            return cached;

        var segments = new List<(FormattedText Ft, double Width)>();
        if (tokens is null || oneBasedLine is null)
        {
            var ft = CreateFormattedText(text, FontSize, fallback);
            segments.Add((ft, ft.WidthIncludingTrailingWhitespace));
        }
        else
        {
            var spans = tokens.ForLine(oneBasedLine.Value);
            if (spans.Count == 0)
            {
                var ft = CreateFormattedText(text, FontSize, fallback);
                segments.Add((ft, ft.WidthIncludingTrailingWhitespace));
            }
            else
            {
                var pos = 0;
                foreach (var span in spans)
                {
                    if (span.Length <= 0 || span.Start < 0 || span.Start >= text.Length)
                        continue;
                    var start = Math.Max(span.Start, pos);
                    var len = Math.Min(span.Length - (start - span.Start), text.Length - start);
                    if (len <= 0) continue;

                    if (pos < start)
                    {
                        var gap = text[pos..start];
                        var gapFt = CreateFormattedText(gap, FontSize, fallback);
                        segments.Add((gapFt, gapFt.WidthIncludingTrailingWhitespace));
                    }

                    var mid = text.Substring(start, len);
                    var color = SyntaxScopePalette.BrushForScope(span.Scope, ActualThemeVariant) ?? fallback;
                    var midFt = CreateFormattedText(mid, FontSize, color);
                    segments.Add((midFt, midFt.WidthIncludingTrailingWhitespace));
                    pos = start + len;
                }

                if (pos < text.Length)
                {
                    var rest = text[pos..];
                    var restFt = CreateFormattedText(rest, FontSize, fallback);
                    segments.Add((restFt, restFt.WidthIncludingTrailingWhitespace));
                }
            }
        }

        double total = 0;
        foreach (var (_, w) in segments)
            total += w;

        cached = new LinePaintCache(text, segments, total);
        _linePaintCache[key] = cached;
        return cached;
    }
}
