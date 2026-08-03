using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using CodeReviewr.App.ViewModels;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using CodeReviewr.Diff;

namespace CodeReviewr.App.Controls;

/// <summary>Purpose-built virtualized diff control. Fixed row height; paints O(viewport).</summary>
public sealed class DiffViewer : Control
{
    public DiffViewer()
    {
        ClipToBounds = true;
    }

    public static readonly StyledProperty<IReadOnlyList<DiffRow>?> RowsProperty =
        AvaloniaProperty.Register<DiffViewer, IReadOnlyList<DiffRow>?>(nameof(Rows));

    public static readonly StyledProperty<DiffViewMode> ViewModeProperty =
        AvaloniaProperty.Register<DiffViewer, DiffViewMode>(nameof(ViewMode), DiffViewMode.Unified);

    public static readonly StyledProperty<bool> ShowWhitespaceProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(ShowWhitespace));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DiffViewer, double>(nameof(RowHeight), 20);

    public static readonly StyledProperty<string> EmptyMessageProperty =
        AvaloniaProperty.Register<DiffViewer, string>(nameof(EmptyMessage), "Select a file to view its diff");

    public static readonly StyledProperty<bool> CanStageLinesProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CanStageLines));

    public static readonly StyledProperty<bool> CanUnstageLinesProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CanUnstageLines));

    public static readonly StyledProperty<bool> CanDiscardLinesProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CanDiscardLines));

    public static readonly StyledProperty<FileSyntaxTokens?> LeftSyntaxTokensProperty =
        AvaloniaProperty.Register<DiffViewer, FileSyntaxTokens?>(nameof(LeftSyntaxTokens));

    public static readonly StyledProperty<FileSyntaxTokens?> RightSyntaxTokensProperty =
        AvaloniaProperty.Register<DiffViewer, FileSyntaxTokens?>(nameof(RightSyntaxTokens));

    public static readonly StyledProperty<IReadOnlyList<IDiffAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<DiffViewer, IReadOnlyList<IDiffAnnotation>?>(nameof(Annotations));

    public static readonly StyledProperty<IDiffAnnotation?> SelectedAnnotationProperty =
        AvaloniaProperty.Register<DiffViewer, IDiffAnnotation?>(
            nameof(SelectedAnnotation),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CanAddLineCommentsProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CanAddLineComments));

    public static readonly StyledProperty<ICommand?> AddLineCommentCommandProperty =
        AvaloniaProperty.Register<DiffViewer, ICommand?>(nameof(AddLineCommentCommand));

    public static readonly StyledProperty<int> InlineInsetAfterRowIndexProperty =
        AvaloniaProperty.Register<DiffViewer, int>(nameof(InlineInsetAfterRowIndex), -1);

    public static readonly StyledProperty<double> InlineInsetHeightProperty =
        AvaloniaProperty.Register<DiffViewer, double>(nameof(InlineInsetHeight));

    private const double GutterWidth = 30;
    private const double CommentLaneWidth = 18;
    private const double CodePadding = 8;
    private const double HunkButtonWidth = 64;
    private const double HunkButtonGap = 6;
    private const double MinimapWidth = 12;
    private const double AddCommentHitSize = 14;
    private const double AddCommentHoverScale = 1.2;
    private const double AnnotationDotSize = 8;

    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private int _hoverRowIndex = -1;
    private DiffSide? _hoverSide;
    private bool _hoverAddComment;
    private double _scrollY;
    private double _scrollX;
    private bool _draggingMinimap;
    private INotifyCollectionChanged? _rowsNotify;
    private INotifyCollectionChanged? _annotationsNotify;
    private readonly List<HunkButtonHit> _hunkButtons = [];
    private readonly List<AnnotationHit> _annotationHits = [];
    private readonly List<AddCommentHit> _addCommentHits = [];
    private MinimapSnapshot? _minimapSnapshot;
    private readonly Typeface _typeface = new(
        new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"));

    /// <summary>Subsampled minimap marks — one entry per vertical pixel, rebuilt when rows/mode/height change.</summary>
    private sealed class MinimapSnapshot(
        object rowsIdentity,
        DiffViewMode mode,
        int heightPx,
        byte[] marks)
    {
        public object RowsIdentity { get; } = rowsIdentity;
        public DiffViewMode Mode { get; } = mode;
        public int HeightPx { get; } = heightPx;
        /// <summary>0=none, 1=added, 2=removed, 3=both (side-by-side).</summary>
        public byte[] Marks { get; } = marks;
    }

    private enum HunkButtonAction { Stage, Unstage, Discard }

    private readonly record struct HunkButtonHit(Rect Bounds, int HunkIndex, HunkButtonAction Action);
    private readonly record struct AnnotationHit(Rect Bounds, IDiffAnnotation Annotation);
    private readonly record struct AddCommentHit(Rect Bounds, DiffSide Side, int Line, int? StartLine);

    public IReadOnlyList<DiffRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public DiffViewMode ViewMode
    {
        get => GetValue(ViewModeProperty);
        set => SetValue(ViewModeProperty, value);
    }

    public bool ShowWhitespace
    {
        get => GetValue(ShowWhitespaceProperty);
        set => SetValue(ShowWhitespaceProperty, value);
    }

    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public string EmptyMessage
    {
        get => GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }

    public bool CanStageLines
    {
        get => GetValue(CanStageLinesProperty);
        set => SetValue(CanStageLinesProperty, value);
    }

    public bool CanUnstageLines
    {
        get => GetValue(CanUnstageLinesProperty);
        set => SetValue(CanUnstageLinesProperty, value);
    }

    public bool CanDiscardLines
    {
        get => GetValue(CanDiscardLinesProperty);
        set => SetValue(CanDiscardLinesProperty, value);
    }

    public FileSyntaxTokens? LeftSyntaxTokens
    {
        get => GetValue(LeftSyntaxTokensProperty);
        set => SetValue(LeftSyntaxTokensProperty, value);
    }

    public FileSyntaxTokens? RightSyntaxTokens
    {
        get => GetValue(RightSyntaxTokensProperty);
        set => SetValue(RightSyntaxTokensProperty, value);
    }

    public IReadOnlyList<IDiffAnnotation>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    public IDiffAnnotation? SelectedAnnotation
    {
        get => GetValue(SelectedAnnotationProperty);
        set => SetValue(SelectedAnnotationProperty, value);
    }

    public bool CanAddLineComments
    {
        get => GetValue(CanAddLineCommentsProperty);
        set => SetValue(CanAddLineCommentsProperty, value);
    }

    public ICommand? AddLineCommentCommand
    {
        get => GetValue(AddLineCommentCommandProperty);
        set => SetValue(AddLineCommentCommandProperty, value);
    }

    /// <summary>
    /// Row index after which vertical space is reserved for an inline comment card.
    /// When <see cref="InlineInsetHeight"/> &gt; 0, <c>-1</c> reserves space above the first row (file comment).
    /// </summary>
    public int InlineInsetAfterRowIndex
    {
        get => GetValue(InlineInsetAfterRowIndexProperty);
        set => SetValue(InlineInsetAfterRowIndexProperty, value);
    }

    /// <summary>Height in device-independent pixels reserved after <see cref="InlineInsetAfterRowIndex"/> (or above row 0 when that index is -1).</summary>
    public double InlineInsetHeight
    {
        get => GetValue(InlineInsetHeightProperty);
        set => SetValue(InlineInsetHeightProperty, value);
    }

    /// <summary>Raised when the viewport scroll offset changes.</summary>
    public event Action? ViewportChanged;

    public int? SelectedHunkIndex
    {
        get
        {
            if (Rows is null || _selectionStart < 0) return null;
            var idx = Math.Clamp(_selectionEnd, 0, Rows.Count - 1);
            return Rows[idx].HunkIndex;
        }
    }

    static DiffViewer()
    {
        AffectsRender<DiffViewer>(
            RowsProperty, ViewModeProperty, ShowWhitespaceProperty, RowHeightProperty,
            EmptyMessageProperty, CanStageLinesProperty, CanUnstageLinesProperty, CanDiscardLinesProperty,
            LeftSyntaxTokensProperty, RightSyntaxTokensProperty, AnnotationsProperty, SelectedAnnotationProperty,
            CanAddLineCommentsProperty, InlineInsetAfterRowIndexProperty, InlineInsetHeightProperty);
        FocusableProperty.OverrideDefaultValue<DiffViewer>(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            DetachRowsNotify();
            AttachRowsNotify(change.NewValue as INotifyCollectionChanged);
            _selectionStart = _selectionEnd = -1;
            _hoverRowIndex = -1;
            _hoverSide = null;
            _hoverAddComment = false;
            _scrollY = 0;
            _minimapSnapshot = null;
            InvalidateVisual();
            InvalidateMeasure();
        }
        else if (change.Property == AnnotationsProperty)
        {
            DetachAnnotationsNotify();
            AttachAnnotationsNotify(change.NewValue as INotifyCollectionChanged);
            InvalidateVisual();
        }
        else if (change.Property == ViewModeProperty || change.Property == RowHeightProperty
                 || change.Property == EmptyMessageProperty
                 || change.Property == CanStageLinesProperty
                 || change.Property == CanUnstageLinesProperty
                 || change.Property == CanDiscardLinesProperty
                 || change.Property == SelectedAnnotationProperty
                 || change.Property == InlineInsetAfterRowIndexProperty
                 || change.Property == InlineInsetHeightProperty)
        {
            ClampScroll();
            InvalidateVisual();
        }
    }

    private double EffectiveInsetHeight =>
        InlineInsetHeight > 0 ? InlineInsetHeight : 0;

    private double TotalContentHeight(int rowCount) =>
        rowCount * RowHeight + EffectiveInsetHeight;

    private double RowContentTop(int index) =>
        index * RowHeight + (index > InlineInsetAfterRowIndex ? EffectiveInsetHeight : 0);

    private int RowIndexAtContentY(double contentY)
    {
        var rowH = RowHeight;
        if (rowH <= 0)
            return 0;

        var insetAfter = InlineInsetAfterRowIndex;
        var insetH = EffectiveInsetHeight;
        if (insetH <= 0)
            return (int)(contentY / rowH);

        // File-level inset: gap occupies content [0, insetH) above the first row.
        if (insetAfter < 0)
        {
            if (contentY < insetH)
                return 0;
            return (int)((contentY - insetH) / rowH);
        }

        var gapStart = (insetAfter + 1) * rowH;
        if (contentY < gapStart)
            return Math.Max(0, (int)(contentY / rowH));
        if (contentY < gapStart + insetH)
            return insetAfter;
        return insetAfter + 1 + (int)((contentY - gapStart - insetH) / rowH);
    }

    private void NotifyViewportChanged() => ViewportChanged?.Invoke();

    private void AttachRowsNotify(INotifyCollectionChanged? notify)
    {
        _rowsNotify = notify;
        if (_rowsNotify is not null)
            _rowsNotify.CollectionChanged += OnRowsCollectionChanged;
    }

    private void DetachRowsNotify()
    {
        if (_rowsNotify is not null)
            _rowsNotify.CollectionChanged -= OnRowsCollectionChanged;
        _rowsNotify = null;
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _minimapSnapshot = null;
        ClampScroll();
        InvalidateVisual();
        InvalidateMeasure();
    }

    private void AttachAnnotationsNotify(INotifyCollectionChanged? notify)
    {
        _annotationsNotify = notify;
        if (_annotationsNotify is not null)
            _annotationsNotify.CollectionChanged += OnAnnotationsCollectionChanged;
    }

    private void DetachAnnotationsNotify()
    {
        if (_annotationsNotify is not null)
            _annotationsNotify.CollectionChanged -= OnAnnotationsCollectionChanged;
        _annotationsNotify = null;
    }

    private void OnAnnotationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(width, height);
    }

    public IReadOnlyList<LineSelection> GetSelectedLineSelections()
    {
        var result = new List<LineSelection>();
        if (Rows is null || _selectionStart < 0) return result;
        var a = Math.Min(_selectionStart, _selectionEnd);
        var b = Math.Max(_selectionStart, _selectionEnd);
        for (var i = a; i <= b && i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
                result.Add(new LineSelection(row.HunkIndex, row.LineIndexInHunk));
        }

        return result;
    }

    public override void Render(DrawingContext context)
    {
        _hunkButtons.Clear();
        _annotationHits.Clear();
        _addCommentHits.Clear();
        var rows = Rows;
        var bounds = Bounds;
        using var clip = context.PushClip(new Rect(bounds.Size));
        var bg = Brush("ForgeSurfaceContainerLowestBrush", Brushes.Black);
        context.FillRectangle(bg, new Rect(bounds.Size));

        if (rows is null || rows.Count == 0)
        {
            DrawText(context, EmptyMessage, MinimapWidth + 16, 16, Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray));
            return;
        }

        var contentLeft = MinimapWidth;
        var contentWidth = Math.Max(0, bounds.Width - contentLeft);
        var rowH = RowHeight;
        var first = Math.Max(0, RowIndexAtContentY(_scrollY));
        var last = Math.Min(rows.Count - 1, RowIndexAtContentY(_scrollY + bounds.Height) + 1);
        var midX = ViewMode == DiffViewMode.SideBySide
            ? contentLeft + contentWidth / 2
            : contentLeft + contentWidth;

        DrawMinimap(context, rows, bounds);

        if (ViewMode == DiffViewMode.SideBySide)
            context.FillRectangle(
                Brush("ForgeOutlineVariantBrush", Brushes.Gray),
                new Rect(midX - 0.5, 0, 1, bounds.Height));

        var muted = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
        var contextText = Brush("ForgeOnSurfaceBrush", Brushes.White);
        var addedAccent = Brush("ForgeStatusAddedBrush", Brushes.LimeGreen);
        var removedAccent = Brush("ForgeStatusDeletedBrush", Brushes.OrangeRed);

        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var y = RowContentTop(i) - _scrollY;
            var selected = _selectionStart >= 0
                           && i >= Math.Min(_selectionStart, _selectionEnd)
                           && i <= Math.Max(_selectionStart, _selectionEnd);

            if (ViewMode == DiffViewMode.SideBySide)
            {
                var leftKind = DiffRowPresentation.SideBySideLeftKind(row);
                var rightKind = DiffRowPresentation.SideBySideRightKind(row);
                using (context.PushClip(new Rect(contentLeft, y, midX - contentLeft, rowH)))
                    context.FillRectangle(RowBrush(leftKind, selected), new Rect(contentLeft, y, midX - contentLeft, rowH));
                using (context.PushClip(new Rect(midX, y, bounds.Width - midX, rowH)))
                    context.FillRectangle(RowBrush(rightKind, selected), new Rect(midX, y, bounds.Width - midX, rowH));

                if (leftKind == DiffRowKind.Removed)
                    context.FillRectangle(removedAccent, new Rect(contentLeft, y, 2, rowH));
                if (rightKind == DiffRowKind.Added)
                    context.FillRectangle(addedAccent, new Rect(midX, y, 2, rowH));
            }
            else
            {
                context.FillRectangle(RowBrush(row.Kind, selected), new Rect(contentLeft, y, contentWidth, rowH));
                if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
                {
                    var accent = row.Kind == DiffRowKind.Added ? addedAccent : removedAccent;
                    context.FillRectangle(accent, new Rect(contentLeft, y, 2, rowH));
                }
            }

            if (ViewMode == DiffViewMode.SideBySide)
            {
                var leftKind = DiffRowPresentation.SideBySideLeftKind(row);
                var rightKind = DiffRowPresentation.SideBySideRightKind(row);
                using (context.PushClip(new Rect(contentLeft, 0, midX - contentLeft, bounds.Height)))
                {
                    DrawGutter(context, row.OldLineNumber, contentLeft, y);
                    if (row.Kind == DiffRowKind.HunkHeader)
                        DrawText(context, row.LeftText.ToString(), SideBySideCodeX(contentLeft) - _scrollX, y, muted);
                    else if (row.Kind == DiffRowKind.Collapsed)
                        DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines — click to expand",
                            SideBySideCodeX(contentLeft), y, muted);
                    else if (!row.LeftText.IsEmpty)
                    {
                        var x = SideBySideCodeX(contentLeft) - _scrollX;
                        DrawIntraLineHighlights(context, FormatText(row.LeftText), row.LeftIntraLine, x, y, rowH, removedAccent);
                        DrawSyntaxOrPlainText(context, FormatText(row.LeftText), x, y,
                            TextBrush(leftKind, contextText), LeftSyntaxTokens, row.OldLineNumber);
                    }
                }

                using (context.PushClip(new Rect(midX, 0, bounds.Width - midX, bounds.Height)))
                {
                    DrawGutter(context, row.NewLineNumber, midX, y);
                    if (row.Kind is not DiffRowKind.HunkHeader and not DiffRowKind.Collapsed && !row.RightText.IsEmpty)
                    {
                        var x = SideBySideCodeX(midX) - _scrollX;
                        DrawIntraLineHighlights(context, FormatText(row.RightText), row.RightIntraLine, x, y, rowH, addedAccent);
                        DrawSyntaxOrPlainText(context, FormatText(row.RightText), x, y,
                            TextBrush(rightKind, contextText), RightSyntaxTokens, row.NewLineNumber);
                    }
                }
            }
            else
            {
                DrawGutter(context, row.OldLineNumber, contentLeft, y);
                DrawGutter(context, row.NewLineNumber, contentLeft + GutterWidth, y);

                if (row.Kind == DiffRowKind.HunkHeader)
                {
                    DrawText(context, row.LeftText.ToString(), UnifiedCodeX(contentLeft) - _scrollX, y, muted);
                    DrawUnifiedHunkButtons(context, row.HunkIndex, y, rowH, bounds.Width);
                    continue;
                }

                if (row.Kind == DiffRowKind.Collapsed)
                {
                    DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines — click to expand",
                        UnifiedCodeX(contentLeft), y, muted);
                    continue;
                }

                var text = row.Kind == DiffRowKind.Removed ? row.LeftText : row.RightText;
                if (text.IsEmpty) text = row.LeftText.IsEmpty ? row.RightText : row.LeftText;
                var prefix = row.Kind switch
                {
                    DiffRowKind.Added => "+",
                    DiffRowKind.Removed => "-",
                    _ => " ",
                };
                var formatted = FormatText(text);
                var x = UnifiedCodeX(contentLeft) - _scrollX;
                var intra = row.Kind == DiffRowKind.Removed ? row.LeftIntraLine : row.RightIntraLine;
                var accent = row.Kind == DiffRowKind.Added ? addedAccent : removedAccent;
                // Offset highlights by the +/- prefix width.
                var prefixWidth = MeasureWidth(prefix);
                DrawIntraLineHighlights(context, formatted, intra, x + prefixWidth, y, rowH, accent);
                DrawText(context, prefix, x, y, TextBrush(row.Kind, contextText));
                var tokens = row.Kind == DiffRowKind.Removed ? LeftSyntaxTokens : RightSyntaxTokens;
                var lineNo = row.Kind == DiffRowKind.Removed ? row.OldLineNumber : row.NewLineNumber;
                if (row.Kind == DiffRowKind.Context)
                {
                    tokens = RightSyntaxTokens ?? LeftSyntaxTokens;
                    lineNo = row.NewLineNumber ?? row.OldLineNumber;
                }

                DrawSyntaxOrPlainText(context, formatted, x + prefixWidth, y,
                    TextBrush(row.Kind, contextText), tokens, lineNo);
            }

            DrawAnnotationMarkers(context, row, contentLeft, midX, y, rowH);
            DrawAddCommentAffordance(context, row, i, contentLeft, midX, y, rowH);
        }
    }

    private static double UnifiedCodeX(double contentLeft) =>
        contentLeft + GutterWidth * 2 + CommentLaneWidth + CodePadding;

    private static double SideBySideCodeX(double paneLeft) =>
        paneLeft + GutterWidth + CommentLaneWidth + CodePadding;

    private double CommentLaneX(DiffSide side, double contentLeft, double midX) =>
        ViewMode == DiffViewMode.SideBySide
            ? (side == DiffSide.Old ? contentLeft : midX) + GutterWidth
            : contentLeft + GutterWidth * 2;

    private void DrawAnnotationMarkers(
        DrawingContext context,
        DiffRow row,
        double contentLeft,
        double midX,
        double y,
        double rowH)
    {
        var annotations = Annotations;
        if (annotations is null || annotations.Count == 0)
            return;

        var markerBrush = Brush("ForgePrimaryBrush", Brushes.SteelBlue);
        var outdatedBrush = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
        var selectedBrush = Brush("ForgeSecondaryBrush", Brushes.Orange);

        foreach (var annotation in annotations)
        {
            var range = annotation.Range;
            var side = range.Start.Side;
            // Show a single marker on the range end line (not every line in a multi-line span).
            var markerLine = range.End.Line;
            int? rowLine = side == DiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
            if (rowLine != markerLine)
                continue;

            var laneX = CommentLaneX(side, contentLeft, midX);
            var isOutdated = annotation is ReviewThreadAnnotation { IsOutdated: true };
            var fill = SelectedAnnotation == annotation
                ? selectedBrush
                : isOutdated ? outdatedBrush : markerBrush;

            var dot = new Rect(
                laneX + 2,
                y + (rowH - AnnotationDotSize) / 2,
                AnnotationDotSize,
                AnnotationDotSize);
            context.FillRectangle(fill, dot, (float)(AnnotationDotSize / 2));
            _annotationHits.Add(new AnnotationHit(dot, annotation));
        }
    }

    private void DrawAddCommentAffordance(
        DrawingContext context,
        DiffRow row,
        int rowIndex,
        double contentLeft,
        double midX,
        double y,
        double rowH)
    {
        if (!CanAddLineComments || rowIndex != _hoverRowIndex || _hoverSide is not { } side)
            return;
        if (row.Kind is DiffRowKind.HunkHeader or DiffRowKind.Collapsed or DiffRowKind.Padding)
            return;

        int? line = side == DiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
        if (line is null)
            return;

        var laneX = CommentLaneX(side, contentLeft, midX);
        var hasMarker = RowHasAnnotationMarker(row, side);
        // Keep + clear of the marker: markers sit at lane left; + sits to their right (into padding).
        var baseX = hasMarker
            ? laneX + AnnotationDotSize + 4
            : laneX + (CommentLaneWidth - AddCommentHitSize) / 2;

        var size = _hoverAddComment ? AddCommentHitSize * AddCommentHoverScale : AddCommentHitSize;
        var hit = new Rect(
            baseX - (size - AddCommentHitSize) / 2,
            y + (rowH - size) / 2,
            size,
            size);
        var fill = _hoverAddComment
            ? Brush("ForgePrimaryBrush", Brushes.SteelBlue)
            : Brush("ForgeSurfaceContainerHighestBrush", Brushes.DimGray);
        var glyphBrush = _hoverAddComment
            ? Brush("ForgeOnPrimaryBrush", Brushes.White)
            : Brush("ForgePrimaryBrush", Brushes.SteelBlue);
        context.FillRectangle(fill, hit, 3);
        var ft = new FormattedText(
            "+",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _hoverAddComment ? 13 : 12,
            glyphBrush);
        context.DrawText(ft, new Point(
            hit.X + (hit.Width - ft.Width) / 2,
            hit.Y + (hit.Height - ft.Height) / 2));

        // Hit-test uses the base (non-scaled) rect so the affordance doesn't jitter.
        var baseHit = new Rect(
            baseX,
            y + (rowH - AddCommentHitSize) / 2,
            AddCommentHitSize,
            AddCommentHitSize);
        var (startLine, endLine) = ResolveCommentLineRange(side, line.Value);
        _addCommentHits.Add(new AddCommentHit(baseHit, side, endLine, startLine));
    }

    private bool RowHasAnnotationMarker(DiffRow row, DiffSide side)
    {
        var annotations = Annotations;
        if (annotations is null || annotations.Count == 0)
            return false;

        int? rowLine = side == DiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
        if (rowLine is null)
            return false;

        foreach (var annotation in annotations)
        {
            var range = annotation.Range;
            if (range.Start.Side != side)
                continue;
            if (range.End.Line == rowLine.Value)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the control-relative rectangle just below the row for <paramref name="side"/>/<paramref name="line"/>,
    /// used to position the inline comment composer and expanded thread card.
    /// </summary>
    public bool TryGetLineAnchorRect(DiffSide side, int line, out Rect rect)
    {
        rect = default;
        if (!TryGetRowIndex(side, line, out var index))
            return false;

        var contentLeft = MinimapWidth;
        var contentWidth = Math.Max(0, Bounds.Width - contentLeft);
        var midX = ViewMode == DiffViewMode.SideBySide
            ? contentLeft + contentWidth / 2
            : contentLeft + contentWidth;
        double x;
        if (ViewMode == DiffViewMode.SideBySide)
        {
            var paneLeft = side == DiffSide.Old ? contentLeft : midX;
            x = SideBySideCodeX(paneLeft);
        }
        else
        {
            x = UnifiedCodeX(contentLeft);
        }

        var y = RowContentTop(index) - _scrollY + RowHeight + 2;
        var width = Math.Max(240, Bounds.Width - x - 16);
        rect = new Rect(x, y, width, 0);
        return true;
    }

    /// <summary>Viewport rect for a file-level comment card sitting in the top inset gap.</summary>
    public bool TryGetFileCommentAnchorRect(out Rect rect)
    {
        rect = default;
        var contentLeft = MinimapWidth;
        var x = ViewMode == DiffViewMode.SideBySide
            ? SideBySideCodeX(contentLeft)
            : UnifiedCodeX(contentLeft);
        var y = -_scrollY + 2;
        var width = Math.Max(240, Bounds.Width - x - 16);
        rect = new Rect(x, y, width, 0);
        return true;
    }

    public bool TryGetRowIndex(DiffSide side, int line, out int index)
    {
        index = -1;
        if (Rows is null || Rows.Count == 0)
            return false;

        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            int? rowLine = side == DiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
            if (rowLine != line)
                continue;
            index = i;
            return true;
        }

        return false;
    }

    public void ClearInlineInset()
    {
        InlineInsetAfterRowIndex = -1;
        InlineInsetHeight = 0;
    }

    private (int? StartLine, int Line) ResolveCommentLineRange(DiffSide side, int clickedLine)
    {
        if (Rows is null || _selectionStart < 0)
            return (null, clickedLine);

        var from = Math.Min(_selectionStart, _selectionEnd);
        var to = Math.Max(_selectionStart, _selectionEnd);
        if (from == to)
            return (null, clickedLine);

        int? first = null;
        int? last = null;
        for (var i = from; i <= to; i++)
        {
            if (i < 0 || i >= Rows.Count) continue;
            var row = Rows[i];
            var line = side == DiffSide.Old ? row.OldLineNumber : row.NewLineNumber;
            if (line is null) continue;
            first ??= line;
            last = line;
        }

        if (first is null || last is null || first == last)
            return (null, clickedLine);

        return (Math.Min(first.Value, last.Value), Math.Max(first.Value, last.Value));
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

        var contentHeight = Math.Max(1, TotalContentHeight(Math.Max(1, rows.Count)));
        if (contentHeight <= 0) return;
        var viewportRatio = Math.Clamp(bounds.Height / contentHeight, 0, 1);
        var viewportH = Math.Max(8, viewportRatio * bounds.Height);
        var scrollRatio = contentHeight <= bounds.Height
            ? 0
            : _scrollY / (contentHeight - bounds.Height);
        var viewportY = scrollRatio * (bounds.Height - viewportH);
        context.DrawRectangle(
            Brush("ForgeMinimapViewportBrush", Brushes.Gray),
            new Pen(Brush("ForgeOutlineBrush", Brushes.Gray), 1),
            new Rect(1, viewportY, MinimapWidth - 2, viewportH),
            1, 1);
    }

    private MinimapSnapshot EnsureMinimapSnapshot(IReadOnlyList<DiffRow> rows, DiffViewMode mode, int heightPx)
    {
        if (_minimapSnapshot is { } cached
            && ReferenceEquals(cached.RowsIdentity, rows)
            && cached.Mode == mode
            && cached.HeightPx == heightPx)
        {
            return cached;
        }

        var marks = new byte[heightPx];
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

        _minimapSnapshot = new MinimapSnapshot(rows, mode, heightPx, marks);
        return _minimapSnapshot;
    }

    private void DrawUnifiedHunkButtons(DrawingContext context, int hunkIndex, double y, double rowH, double width)
    {
        var x = width - 8;
        if (CanUnstageLines)
        {
            x -= HunkButtonWidth;
            var rect = new Rect(x, y + 2, HunkButtonWidth, rowH - 4);
            DrawHunkButton(context, rect, "Unstage");
            _hunkButtons.Add(new HunkButtonHit(rect, hunkIndex, HunkButtonAction.Unstage));
            x -= HunkButtonGap;
        }

        if (CanStageLines)
        {
            x -= HunkButtonWidth;
            var rect = new Rect(x, y + 2, HunkButtonWidth, rowH - 4);
            DrawHunkButton(context, rect, "Stage");
            _hunkButtons.Add(new HunkButtonHit(rect, hunkIndex, HunkButtonAction.Stage));
            x -= HunkButtonGap;
        }

        if (CanDiscardLines)
        {
            x -= HunkButtonWidth;
            var rect = new Rect(x, y + 2, HunkButtonWidth, rowH - 4);
            DrawHunkButton(context, rect, "Discard");
            _hunkButtons.Add(new HunkButtonHit(rect, hunkIndex, HunkButtonAction.Discard));
        }
    }

    private void DrawHunkButton(DrawingContext context, Rect rect, string label)
    {
        context.FillRectangle(Brush("ForgeSurfaceContainerHighestBrush", Brushes.DimGray), rect, 3);
        var ft = new FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            11,
            Brush("ForgeOnSurfaceVariantBrush", Brushes.LightGray));
        context.DrawText(ft, new Point(
            rect.X + (rect.Width - ft.Width) / 2,
            rect.Y + (rect.Height - ft.Height) / 2));
    }

    private string FormatText(ReadOnlyMemory<char> text)
    {
        var s = text.ToString();
        if (!ShowWhitespace) return s.TrimEnd('\n', '\r');
        return s.Replace(' ', '·').Replace('\t', '→').TrimEnd('\n', '\r');
    }

    private void DrawGutter(DrawingContext ctx, int? line, double x, double y)
    {
        if (line is null) return;
        var brush = Brush("ForgeDiffGutterTextBrush", Brushes.Gray);
        var ft = new FormattedText(
            line.Value.ToString(),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            12,
            brush);
        ctx.DrawText(ft, new Point(x + GutterWidth - ft.Width - 4, y + (RowHeight - ft.Height) / 2));
    }

    private void DrawText(DrawingContext ctx, string text, double x, double y, IBrush brush)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            12,
            brush);
        ctx.DrawText(ft, new Point(x, y + (RowHeight - ft.Height) / 2));
    }

    private double MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            12,
            Brushes.Transparent);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private void DrawIntraLineHighlights(
        DrawingContext ctx,
        string text,
        IReadOnlyList<CharSpan>? spans,
        double x,
        double y,
        double rowH,
        IBrush accent)
    {
        if (spans is null || spans.Count == 0 || string.IsNullOrEmpty(text))
            return;

        // Semi-transparent accent under changed substrings.
        var highlight = accent is ISolidColorBrush solid
            ? (IBrush)new SolidColorBrush(Color.FromArgb(0x55, solid.Color.R, solid.Color.G, solid.Color.B))
            : accent;

        foreach (var span in spans)
        {
            if (span.Length <= 0 || span.Start < 0 || span.Start >= text.Length)
                continue;
            var len = Math.Min(span.Length, text.Length - span.Start);
            if (len <= 0) continue;
            var before = text[..span.Start];
            var mid = text.Substring(span.Start, len);
            var left = x + MeasureWidth(before);
            var width = MeasureWidth(mid);
            if (width <= 0) continue;
            ctx.FillRectangle(highlight, new Rect(left, y, width, rowH));
        }
    }

    private void DrawSyntaxOrPlainText(
        DrawingContext ctx,
        string text,
        double x,
        double y,
        IBrush fallback,
        FileSyntaxTokens? tokens,
        int? oneBasedLine)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (tokens is null || oneBasedLine is null)
        {
            DrawText(ctx, text, x, y, fallback);
            return;
        }

        var spans = tokens.ForLine(oneBasedLine.Value);
        if (spans.Count == 0)
        {
            DrawText(ctx, text, x, y, fallback);
            return;
        }

        // Paint each character once: gaps use fallback, tokens use scope color.
        var drawX = x;
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
                DrawText(ctx, gap, drawX, y, fallback);
                drawX += MeasureWidth(gap);
            }

            var mid = text.Substring(start, len);
            var color = SyntaxScopePalette.BrushForScope(span.Scope, ActualThemeVariant) ?? fallback;
            DrawText(ctx, mid, drawX, y, color);
            drawX += MeasureWidth(mid);
            pos = start + len;
        }

        if (pos < text.Length)
            DrawText(ctx, text[pos..], drawX, y, fallback);
    }

    private IBrush RowBrush(DiffRowKind kind, bool selected) =>
        selected ? Brush("ForgeDiffSelectionFillBrush", Brushes.SlateBlue)
        : kind switch
        {
            DiffRowKind.Added => Brush("ForgeDiffAddedFillBrush", Brushes.DarkGreen),
            DiffRowKind.Removed => Brush("ForgeDiffRemovedFillBrush", Brushes.DarkRed),
            DiffRowKind.HunkHeader => Brush("ForgeDiffHeaderFillBrush", Brushes.DimGray),
            DiffRowKind.Collapsed => Brush("ForgeDiffCollapsedFillBrush", Brushes.Transparent),
            _ => Brushes.Transparent,
        };

    private IBrush TextBrush(DiffRowKind kind, IBrush fallback) =>
        kind switch
        {
            DiffRowKind.Added => Brush("ForgeDiffAddedTextBrush", fallback),
            DiffRowKind.Removed => Brush("ForgeDiffRemovedTextBrush", fallback),
            _ => fallback,
        };

    private IBrush Brush(string key, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var res) == true
            && res is IBrush brush)
            return brush;
        return fallback;
    }

    private void ClampScroll()
    {
        var rows = Rows;
        if (rows is null)
        {
            if (_scrollY != 0)
            {
                _scrollY = 0;
                NotifyViewportChanged();
            }
            return;
        }

        var max = Math.Max(0, TotalContentHeight(rows.Count) - Bounds.Height);
        var next = Math.Clamp(_scrollY, 0, max);
        if (Math.Abs(next - _scrollY) > 0.01)
        {
            _scrollY = next;
            NotifyViewportChanged();
        }
    }

    private void ScrollFromMinimapY(double y)
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0) return;
        var contentHeight = TotalContentHeight(rows.Count);
        var ratio = Math.Clamp(y / Math.Max(1, Bounds.Height), 0, 1);
        var next = Math.Clamp(ratio * Math.Max(0, contentHeight - Bounds.Height), 0, Math.Max(0, contentHeight - Bounds.Height));
        if (Math.Abs(next - _scrollY) > 0.01)
        {
            _scrollY = next;
            NotifyViewportChanged();
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var rows = Rows;
        if (rows is null) return;
        var changed = false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var nextX = Math.Max(0, _scrollX - e.Delta.Y * 40);
            if (Math.Abs(nextX - _scrollX) > 0.01)
            {
                _scrollX = nextX;
                changed = true;
            }
        }
        else
        {
            var max = Math.Max(0, TotalContentHeight(rows.Count) - Bounds.Height);
            var nextY = Math.Clamp(_scrollY - e.Delta.Y * RowHeight * 3, 0, max);
            if (Math.Abs(nextY - _scrollY) > 0.01)
            {
                _scrollY = nextY;
                changed = true;
            }
        }
        if (changed)
            NotifyViewportChanged();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;

        if (pos.X < MinimapWidth && Rows is { Count: > 0 })
        {
            _draggingMinimap = true;
            ScrollFromMinimapY(pos.Y);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            EnsureSelectionAt(pos);
            ShowLineContextMenu();
            e.Handled = true;
            return;
        }

        foreach (var hit in _hunkButtons)
        {
            if (!hit.Bounds.Contains(pos)) continue;
            if (GetWorkingCopy() is { } wc)
            {
                switch (hit.Action)
                {
                    case HunkButtonAction.Stage:
                        _ = wc.StageHunkAtAsync(hit.HunkIndex);
                        break;
                    case HunkButtonAction.Unstage:
                        _ = wc.UnstageHunkAtAsync(hit.HunkIndex);
                        break;
                    case HunkButtonAction.Discard:
                        _ = wc.DiscardHunkAtAsync(hit.HunkIndex);
                        break;
                }
            }
            e.Handled = true;
            return;
        }

        foreach (var hit in _annotationHits)
        {
            if (!hit.Bounds.Contains(pos)) continue;
            // Toggle: click the same marker again to collapse the inline thread.
            SelectedAnnotation = ReferenceEquals(SelectedAnnotation, hit.Annotation)
                ? null
                : hit.Annotation;
            e.Handled = true;
            return;
        }

        foreach (var hit in _addCommentHits)
        {
            if (!hit.Bounds.Contains(pos)) continue;
            var request = new LineCommentRequest(hit.Side, hit.Line, hit.StartLine);
            if (AddLineCommentCommand?.CanExecute(request) == true)
                AddLineCommentCommand.Execute(request);
            e.Handled = true;
            return;
        }

        var index = RowIndexAtContentY(pos.Y + _scrollY);
        if (Rows is null || index < 0 || index >= Rows.Count) return;

        if (Rows[index].Kind == DiffRowKind.Collapsed)
        {
            GetWorkingCopy()?.ExpandCollapsedSection(Rows[index].HunkIndex, Rows[index].LineIndexInHunk);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionStart >= 0)
            _selectionEnd = index;
        else
            _selectionStart = _selectionEnd = index;

        if (SelectedHunkIndex is { } hunk && GetWorkingCopy() is { } workingCopy)
            workingCopy.SelectedHunkIndex = hunk;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_draggingMinimap)
        {
            ScrollFromMinimapY(e.GetPosition(this).Y);
            e.Handled = true;
            return;
        }

        if (!CanAddLineComments || Rows is null)
            return;

        var pos = e.GetPosition(this);
        if (pos.X < MinimapWidth)
        {
            ClearHoverAffordance();
            return;
        }

        var contentLeft = MinimapWidth;
        var contentWidth = Math.Max(0, Bounds.Width - contentLeft);
        var midX = ViewMode == DiffViewMode.SideBySide
            ? contentLeft + contentWidth / 2
            : contentLeft + contentWidth;
        var index = RowIndexAtContentY(pos.Y + _scrollY);
        if (index < 0 || index >= Rows.Count)
        {
            ClearHoverAffordance();
            return;
        }

        // Pointer is in the reserved inset gap — no row affordance.
        var rowTop = RowContentTop(index) - _scrollY;
        if (pos.Y > rowTop + RowHeight &&
            InlineInsetAfterRowIndex == index &&
            EffectiveInsetHeight > 0)
        {
            ClearHoverAffordance();
            return;
        }

        var row = Rows[index];
        DiffSide? side = null;
        if (ViewMode == DiffViewMode.SideBySide)
        {
            side = pos.X < midX ? DiffSide.Old : DiffSide.New;
            if ((side == DiffSide.Old && row.OldLineNumber is null) ||
                (side == DiffSide.New && row.NewLineNumber is null))
                side = null;
        }
        else
        {
            if (pos.X < contentLeft + GutterWidth && row.OldLineNumber is not null)
                side = DiffSide.Old;
            else if (pos.X < contentLeft + GutterWidth * 2 && row.NewLineNumber is not null)
                side = DiffSide.New;
            else
            {
                side = row.Kind switch
                {
                    DiffRowKind.Removed => DiffSide.Old,
                    DiffRowKind.Added => DiffSide.New,
                    _ when row.NewLineNumber is not null => DiffSide.New,
                    _ when row.OldLineNumber is not null => DiffSide.Old,
                    _ => null,
                };
            }
        }

        var overAdd = false;
        if (side is not null)
        {
            foreach (var hit in _addCommentHits)
            {
                if (!hit.Bounds.Contains(pos)) continue;
                overAdd = true;
                break;
            }

            // Hits rebuild on paint; estimate the base + rect while hovering the row.
            if (!overAdd && index == _hoverRowIndex && _hoverSide == side)
            {
                var laneX = CommentLaneX(side.Value, contentLeft, midX);
                var hasMarker = RowHasAnnotationMarker(row, side.Value);
                var baseX = hasMarker
                    ? laneX + AnnotationDotSize + 4
                    : laneX + (CommentLaneWidth - AddCommentHitSize) / 2;
                var y = RowContentTop(index) - _scrollY;
                var estimate = new Rect(
                    baseX,
                    y + (RowHeight - AddCommentHitSize) / 2,
                    AddCommentHitSize,
                    AddCommentHitSize);
                overAdd = estimate.Contains(pos);
            }
        }

        if (_hoverRowIndex == index && _hoverSide == side && _hoverAddComment == overAdd)
            return;

        _hoverRowIndex = side is null ? -1 : index;
        _hoverSide = side;
        _hoverAddComment = overAdd;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHoverAffordance();
    }

    private void ClearHoverAffordance()
    {
        if (_hoverRowIndex < 0 && _hoverSide is null && !_hoverAddComment)
            return;
        _hoverRowIndex = -1;
        _hoverSide = null;
        _hoverAddComment = false;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_draggingMinimap) return;
        _draggingMinimap = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EnsureSelectionAt(Point pos)
    {
        var index = RowIndexAtContentY(pos.Y + _scrollY);
        if (Rows is null || index < 0 || index >= Rows.Count) return;
        if (_selectionStart < 0
            || index < Math.Min(_selectionStart, _selectionEnd)
            || index > Math.Max(_selectionStart, _selectionEnd))
        {
            _selectionStart = _selectionEnd = index;
            InvalidateVisual();
        }
    }

    private void ShowLineContextMenu()
    {
        var lines = GetSelectedLineSelections();
        var menu = new ContextMenu();

        var stageItem = new MenuItem
        {
            Header = lines.Count > 0 ? $"Stage {lines.Count} selected line(s)" : "Stage selected lines",
            IsEnabled = CanStageLines && lines.Count > 0,
        };
        stageItem.Click += (_, _) =>
        {
            if (GetWorkingCopy() is { } wc)
                _ = wc.StageSelectedLinesCommand.ExecuteAsync(lines);
        };

        var unstageItem = new MenuItem
        {
            Header = lines.Count > 0 ? $"Unstage {lines.Count} selected line(s)" : "Unstage selected lines",
            IsEnabled = CanUnstageLines && lines.Count > 0,
        };
        unstageItem.Click += (_, _) =>
        {
            if (GetWorkingCopy() is { } wc)
                _ = wc.UnstageSelectedLinesCommand.ExecuteAsync(lines);
        };

        var discardItem = new MenuItem
        {
            Header = lines.Count > 0 ? $"Discard {lines.Count} selected line(s)" : "Discard selected lines",
            IsEnabled = CanDiscardLines && lines.Count > 0,
        };
        discardItem.Click += (_, _) =>
        {
            if (GetWorkingCopy() is { } wc)
                _ = wc.DiscardSelectedLinesCommand.ExecuteAsync(lines);
        };

        menu.Items.Add(stageItem);
        menu.Items.Add(unstageItem);
        menu.Items.Add(discardItem);
        menu.Open(this);
    }

    private WorkingCopyViewModel? GetWorkingCopy() =>
        TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel main }
            ? main.WorkingCopy
            : null;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var copy = (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                   || (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        if (copy)
        {
            _ = CopySelectionAsPatchAsync();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up or Key.PageDown or Key.PageUp or Key.Home or Key.End)
        {
            Navigate(e.Key);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void Navigate(Key key)
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0) return;
        var idx = Math.Max(_selectionEnd, 0);
        idx = key switch
        {
            Key.Down => Math.Min(rows.Count - 1, idx + 1),
            Key.Up => Math.Max(0, idx - 1),
            Key.PageDown => Math.Min(rows.Count - 1, idx + (int)(Bounds.Height / RowHeight)),
            Key.PageUp => Math.Max(0, idx - (int)(Bounds.Height / RowHeight)),
            Key.Home => 0,
            Key.End => rows.Count - 1,
            _ => idx,
        };
        _selectionStart = _selectionEnd = idx;
        EnsureVisible(idx);
        InvalidateVisual();
    }

    private void EnsureVisible(int index)
    {
        var y = RowContentTop(index);
        var next = _scrollY;
        if (y < _scrollY) next = y;
        else if (y + RowHeight > _scrollY + Bounds.Height)
            next = y + RowHeight - Bounds.Height;
        if (Math.Abs(next - _scrollY) > 0.01)
        {
            _scrollY = next;
            NotifyViewportChanged();
        }
    }

    public async Task CopySelectionAsPatchAsync()
    {
        if (Rows is null || _selectionStart < 0) return;
        var a = Math.Min(_selectionStart, _selectionEnd);
        var b = Math.Max(_selectionStart, _selectionEnd);
        var lines = new StringBuilder();
        for (var i = a; i <= b && i < Rows.Count; i++)
        {
            var r = Rows[i];
            var text = r.RightText.IsEmpty ? r.LeftText : r.RightText;
            var prefix = r.Kind switch
            {
                DiffRowKind.Added => "+",
                DiffRowKind.Removed => "-",
                _ => " ",
            };
            lines.Append(prefix).Append(text).Append('\n');
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(lines.ToString()!);
    }
}
