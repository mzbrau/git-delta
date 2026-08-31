using System.Collections.Generic;
using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.App.Controls;

/// <summary>Pure DiffViewer layout helpers (testable without an Avalonia visual tree).</summary>
public static class DiffViewerLayout
{
    /// <summary>Default continuous scale: one wheel unit ≈ 1.5 rows (no fractional vs notch cliff).</summary>
    public const double DefaultScrollSensitivity = 1.5;

    /// <summary>Exponential catch-up factor toward the scroll target each render tick.</summary>
    public const double DefaultScrollLerpAlpha = 0.45;

    /// <summary>Snap displayed scroll to target when within this many pixels.</summary>
    public const double DefaultScrollLerpSnapPx = 0.5;

    /// <summary>
    /// Horizontal scroll is needed only when max content width is already known.
    /// A null cache must not trigger an O(rows) scan on the UI/input path.
    /// </summary>
    public static bool HasCachedHorizontalScroll(double? maxCodeContentWidthCache, double viewportCodeWidth) =>
        maxCodeContentWidthCache is { } width && width - viewportCodeWidth > 0.5;

    /// <summary>
    /// Converts a wheel/trackpad delta into vertical pixels with a continuous scale
    /// (no discontinuity when |deltaY| crosses 1.0).
    /// </summary>
    public static double VerticalScrollDeltaPixels(
        double deltaY,
        double rowHeight,
        double sensitivity = DefaultScrollSensitivity)
    {
        if (rowHeight <= 0 || Math.Abs(deltaY) < 1e-9 || sensitivity <= 0)
            return 0;
        return deltaY * rowHeight * sensitivity;
    }

    /// <summary>
    /// One exponential step from <paramref name="current"/> toward <paramref name="target"/>.
    /// Snaps when within <paramref name="snapPx"/>.
    /// </summary>
    public static double StepScrollLerp(
        double current,
        double target,
        double alpha = DefaultScrollLerpAlpha,
        double snapPx = DefaultScrollLerpSnapPx)
    {
        var delta = target - current;
        if (Math.Abs(delta) <= snapPx || alpha >= 1.0)
            return target;
        if (alpha <= 0)
            return current;
        return current + delta * alpha;
    }

    /// <summary>True while a scroll gesture is still within the warm-idle window.</summary>
    public static bool IsScrollWarmPaused(long lastScrollTimestamp, long nowTimestamp, double idleMs)
    {
        if (lastScrollTimestamp <= 0 || idleMs <= 0)
            return false;
        var elapsedMs = (nowTimestamp - lastScrollTimestamp) * 1000.0 / Stopwatch.Frequency;
        return elapsedMs < idleMs;
    }

    /// <summary>JetBrains Mono is fixed-width — max advance is char-count × glyph width.</summary>
    public static int ComputeMaxDisplayChars(IReadOnlyList<DiffRow> rows, DiffViewMode viewMode)
    {
        var maxChars = 0;
        if (viewMode == DiffViewMode.SideBySide)
        {
            foreach (var row in rows)
            {
                if (row.Kind is DiffRowKind.Collapsed or DiffRowKind.Padding)
                    continue;
                if (row.Kind == DiffRowKind.HunkHeader)
                {
                    maxChars = Math.Max(maxChars, DisplayCharCount(row.LeftText));
                    continue;
                }

                if (!row.LeftText.IsEmpty)
                    maxChars = Math.Max(maxChars, DisplayCharCount(row.LeftText));
                if (!row.RightText.IsEmpty)
                    maxChars = Math.Max(maxChars, DisplayCharCount(row.RightText));
            }
        }
        else
        {
            foreach (var row in rows)
            {
                if (row.Kind is DiffRowKind.Collapsed or DiffRowKind.Padding)
                    continue;
                if (row.Kind == DiffRowKind.HunkHeader)
                {
                    maxChars = Math.Max(maxChars, DisplayCharCount(row.LeftText));
                    continue;
                }

                var text = row.Kind == DiffRowKind.Removed ? row.LeftText : row.RightText;
                if (text.IsEmpty) text = row.LeftText.IsEmpty ? row.RightText : row.LeftText;
                // Unified rows include a one-character +/-/space prefix.
                maxChars = Math.Max(maxChars, 1 + DisplayCharCount(text));
            }
        }

        return maxChars;
    }

    public static int DisplayCharCount(ReadOnlyMemory<char> text)
    {
        var span = text.Span;
        var len = span.Length;
        while (len > 0 && (span[len - 1] == '\n' || span[len - 1] == '\r'))
            len--;
        return len;
    }
}
