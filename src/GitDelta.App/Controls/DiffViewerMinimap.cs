using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.Controls;

public sealed partial class DiffViewer
{
    /// <summary>Subsampled minimap marks — one entry per vertical pixel, rebuilt when rows/mode/height/annotations change.</summary>
    private sealed class MinimapSnapshot(
        object rowsIdentity,
        object annotationsIdentity,
        DiffViewMode mode,
        int heightPx,
        byte[] marks,
        byte[] commentMarks)
    {
        public object RowsIdentity { get; } = rowsIdentity;
        public object AnnotationsIdentity { get; } = annotationsIdentity;
        public DiffViewMode Mode { get; } = mode;
        public int HeightPx { get; } = heightPx;
        /// <summary>0=none, 1=added, 2=removed, 3=both (side-by-side).</summary>
        public byte[] Marks { get; } = marks;
        /// <summary>0=none, 1=primary thread/pending, 2=AI, 3=muted (outdated/dismissed).</summary>
        public byte[] CommentMarks { get; } = commentMarks;
    }

    private void DrawMinimap(DrawingContext context, IReadOnlyList<DiffRow> rows, Rect bounds)
    {
        var track = Brush("ForgeMinimapTrackBrush", Brushes.Transparent);
        context.FillRectangle(track, new Rect(0, 0, MinimapWidth, bounds.Height));

        var heightPx = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var snapshot = EnsureMinimapSnapshot(rows, ViewMode, heightPx);
        var added = Brush("ForgeStatusAddedBrush", Brushes.LimeGreen);
        var removed = Brush("ForgeStatusDeletedBrush", Brushes.OrangeRed);

        // Paint O(viewport height) marks from the subsampled cache — never O(rows).
        for (var y = 0; y < snapshot.Marks.Length; y++)
        {
            var mark = snapshot.Marks[y];
            if (mark == 0) continue;
            var markRect = new Rect(2, y, MinimapWidth - 4, 1);
            if (mark == 3)
            {
                var half = (MinimapWidth - 4) / 2;
                context.FillRectangle(removed, new Rect(2, y, half, 1));
                context.FillRectangle(added, new Rect(2 + half, y, MinimapWidth - 4 - half, 1));
            }
            else if (mark == 1)
                context.FillRectangle(added, markRect);
            else
                context.FillRectangle(removed, markRect);
        }

        const double commentMarkHeight = 3;
        var primaryComment = Brush("ForgePrimaryBrush", Brushes.SteelBlue);
        var aiComment = Brush("ForgeAiAccentBrush", Brushes.MediumPurple);
        var mutedComment = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
        var commentBorder = new Pen(Brushes.Black, 1);
        for (var y = 0; y < snapshot.CommentMarks.Length; y++)
        {
            var kind = snapshot.CommentMarks[y];
            if (kind == 0) continue;
            var brush = kind switch
            {
                2 => aiComment,
                3 => mutedComment,
                _ => primaryComment,
            };
            var markY = Math.Clamp(y - 1, 0, Math.Max(0, snapshot.CommentMarks.Length - commentMarkHeight));
            context.DrawRectangle(
                brush,
                commentBorder,
                new Rect(1, markY, MinimapWidth - 2, commentMarkHeight));
        }

        var contentHeight = Math.Max(1, TotalContentHeight(Math.Max(1, rows.Count)));
        if (contentHeight <= 0) return;
        var viewportHeight = ViewportHeight;
        var viewportRatio = Math.Clamp(viewportHeight / contentHeight, 0, 1);
        var viewportH = Math.Max(8, viewportRatio * bounds.Height);
        var scrollRatio = contentHeight <= viewportHeight
            ? 0
            : _scrollY / (contentHeight - viewportHeight);
        var viewportY = scrollRatio * (bounds.Height - viewportH);
        context.DrawRectangle(
            Brush("ForgeMinimapViewportBrush", Brushes.Gray),
            new Pen(Brush("ForgeOutlineBrush", Brushes.Gray), 1),
            new Rect(1, viewportY, MinimapWidth - 2, viewportH),
            1, 1);
    }

    private MinimapSnapshot EnsureMinimapSnapshot(IReadOnlyList<DiffRow> rows, DiffViewMode mode, int heightPx)
    {
        var annotations = Annotations;
        var annotationsIdentity = annotations as object ?? Array.Empty<IDiffAnnotation>();
        if (_minimapSnapshot is { } cached
            && ReferenceEquals(cached.RowsIdentity, rows)
            && cached.Mode == mode
            && cached.HeightPx == heightPx
            && ReferenceEquals(cached.AnnotationsIdentity, annotationsIdentity))
        {
            return cached;
        }

        var marks = new byte[heightPx];
        var commentMarks = new byte[heightPx];
        var total = Math.Max(1, rows.Count);
        for (var y = 0; y < heightPx; y++)
        {
            var i = Math.Min(total - 1, (int)((long)y * total / heightPx));
            var row = rows[i];
            if (mode == DiffViewMode.SideBySide)
            {
                var leftKind = DiffRowPresentation.SideBySideLeftKind(row);
                var rightKind = DiffRowPresentation.SideBySideRightKind(row);
                if (leftKind == DiffRowKind.Removed && rightKind == DiffRowKind.Added)
                    marks[y] = 3;
                else if (rightKind == DiffRowKind.Added)
                    marks[y] = 1;
                else if (leftKind == DiffRowKind.Removed)
                    marks[y] = 2;
            }
            else if (row.Kind == DiffRowKind.Added)
                marks[y] = 1;
            else if (row.Kind == DiffRowKind.Removed)
                marks[y] = 2;
        }

        if (annotations is { Count: > 0 })
        {
            var oldLineToRow = new Dictionary<int, int>();
            var newLineToRow = new Dictionary<int, int>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.OldLineNumber is { } oldLine)
                    oldLineToRow.TryAdd(oldLine, i);
                if (row.NewLineNumber is { } newLine)
                    newLineToRow.TryAdd(newLine, i);
            }

            foreach (var annotation in annotations)
            {
                var range = annotation.Range;
                var side = range.Start.Side;
                var markerLine = range.End.Line;
                var rowIndex = side == DiffSide.Old
                    ? oldLineToRow.GetValueOrDefault(markerLine, -1)
                    : newLineToRow.GetValueOrDefault(markerLine, -1);
                if (rowIndex < 0)
                    continue;

                var y = Math.Min(heightPx - 1, (int)((long)rowIndex * heightPx / total));
                byte kind = annotation switch
                {
                    AiLineAnnotation { IsDismissed: true } => 3,
                    AiLineAnnotation => 2,
                    ReviewThreadAnnotation { IsOutdated: true } => 3,
                    _ => 1,
                };
                // Prefer active AI / primary over muted when multiple map to the same pixel.
                if (commentMarks[y] == 0 || kind < commentMarks[y])
                    commentMarks[y] = kind;
            }
        }

        _minimapSnapshot = new MinimapSnapshot(rows, annotationsIdentity, mode, heightPx, marks, commentMarks);
        return _minimapSnapshot;
    }
}
