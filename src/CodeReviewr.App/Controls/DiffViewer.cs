using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
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

    private const double GutterWidth = 40;
    private const double HunkButtonWidth = 64;
    private const double HunkButtonGap = 6;
    private const double MinimapWidth = 12;

    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private double _scrollY;
    private double _scrollX;
    private bool _draggingMinimap;
    private INotifyCollectionChanged? _rowsNotify;
    private readonly List<HunkButtonHit> _hunkButtons = [];
    private readonly Typeface _typeface = new(
        new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"));

    private enum HunkButtonAction { Stage, Unstage, Discard }

    private readonly record struct HunkButtonHit(Rect Bounds, int HunkIndex, HunkButtonAction Action);

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
            EmptyMessageProperty, CanStageLinesProperty, CanUnstageLinesProperty, CanDiscardLinesProperty);
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
            _scrollY = 0;
            InvalidateVisual();
            InvalidateMeasure();
        }
        else if (change.Property == ViewModeProperty || change.Property == RowHeightProperty
                 || change.Property == EmptyMessageProperty
                 || change.Property == CanStageLinesProperty
                 || change.Property == CanUnstageLinesProperty
                 || change.Property == CanDiscardLinesProperty)
        {
            InvalidateVisual();
        }
    }

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
        ClampScroll();
        InvalidateVisual();
        InvalidateMeasure();
    }

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
        var first = Math.Max(0, (int)(_scrollY / rowH));
        var last = Math.Min(rows.Count - 1, (int)((_scrollY + bounds.Height) / rowH) + 1);
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
            var y = i * rowH - _scrollY;
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
                        DrawText(context, row.LeftText.ToString(), contentLeft + GutterWidth + 8 - _scrollX, y, muted);
                    else if (row.Kind == DiffRowKind.Collapsed)
                        DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines — click to expand",
                            contentLeft + GutterWidth + 8, y, muted);
                    else if (!row.LeftText.IsEmpty)
                        DrawText(context, FormatText(row.LeftText), contentLeft + GutterWidth + 8 - _scrollX, y, TextBrush(leftKind, contextText));
                }

                using (context.PushClip(new Rect(midX, 0, bounds.Width - midX, bounds.Height)))
                {
                    DrawGutter(context, row.NewLineNumber, midX, y);
                    if (row.Kind is not DiffRowKind.HunkHeader and not DiffRowKind.Collapsed && !row.RightText.IsEmpty)
                        DrawText(context, FormatText(row.RightText), midX + GutterWidth + 8 - _scrollX, y, TextBrush(rightKind, contextText));
                }
            }
            else
            {
                DrawGutter(context, row.OldLineNumber, contentLeft, y);
                DrawGutter(context, row.NewLineNumber, contentLeft + GutterWidth, y);

                if (row.Kind == DiffRowKind.HunkHeader)
                {
                    DrawText(context, row.LeftText.ToString(), contentLeft + GutterWidth * 2 + 8 - _scrollX, y, muted);
                    DrawUnifiedHunkButtons(context, row.HunkIndex, y, rowH, bounds.Width);
                    continue;
                }

                if (row.Kind == DiffRowKind.Collapsed)
                {
                    DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines — click to expand",
                        contentLeft + GutterWidth * 2 + 8, y, muted);
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
                DrawText(context, prefix + FormatText(text), contentLeft + GutterWidth * 2 + 8 - _scrollX, y, TextBrush(row.Kind, contextText));
            }
        }
    }

    private void DrawMinimap(DrawingContext context, IReadOnlyList<DiffRow> rows, Rect bounds)
    {
        var track = Brush("ForgeMinimapTrackBrush", Brushes.Transparent);
        context.FillRectangle(track, new Rect(0, 0, MinimapWidth, bounds.Height));

        var total = Math.Max(1, rows.Count);
        var added = Brush("ForgeStatusAddedBrush", Brushes.LimeGreen);
        var removed = Brush("ForgeStatusDeletedBrush", Brushes.OrangeRed);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var y = i / (double)total * bounds.Height;
            var h = Math.Max(2, bounds.Height / total);
            var markRect = new Rect(2, y, MinimapWidth - 4, h);

            if (ViewMode == DiffViewMode.SideBySide)
            {
                var leftKind = DiffRowPresentation.SideBySideLeftKind(row);
                var rightKind = DiffRowPresentation.SideBySideRightKind(row);
                if (leftKind == DiffRowKind.Removed && rightKind == DiffRowKind.Added)
                {
                    var half = (MinimapWidth - 4) / 2;
                    context.FillRectangle(removed, new Rect(2, y, half, h));
                    context.FillRectangle(added, new Rect(2 + half, y, MinimapWidth - 4 - half, h));
                }
                else if (rightKind == DiffRowKind.Added)
                    context.FillRectangle(added, markRect);
                else if (leftKind == DiffRowKind.Removed)
                    context.FillRectangle(removed, markRect);
            }
            else if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
            {
                context.FillRectangle(
                    row.Kind == DiffRowKind.Added ? added : removed,
                    markRect);
            }
        }

        var contentHeight = total * RowHeight;
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
        ctx.DrawText(ft, new Point(x + GutterWidth - ft.Width - 8, y + (RowHeight - ft.Height) / 2));
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
        if (rows is null) { _scrollY = 0; return; }
        var max = Math.Max(0, rows.Count * RowHeight - Bounds.Height);
        _scrollY = Math.Clamp(_scrollY, 0, max);
    }

    private void ScrollFromMinimapY(double y)
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0) return;
        var contentHeight = rows.Count * RowHeight;
        var ratio = Math.Clamp(y / Math.Max(1, Bounds.Height), 0, 1);
        _scrollY = Math.Clamp(ratio * Math.Max(0, contentHeight - Bounds.Height), 0, Math.Max(0, contentHeight - Bounds.Height));
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var rows = Rows;
        if (rows is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            _scrollX = Math.Max(0, _scrollX - e.Delta.Y * 40);
        else
        {
            var max = Math.Max(0, rows.Count * RowHeight - Bounds.Height);
            _scrollY = Math.Clamp(_scrollY - e.Delta.Y * RowHeight * 3, 0, max);
        }
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

        var y = pos.Y + _scrollY;
        var index = (int)(y / RowHeight);
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
        if (!_draggingMinimap) return;
        ScrollFromMinimapY(e.GetPosition(this).Y);
        e.Handled = true;
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
        var index = (int)((pos.Y + _scrollY) / RowHeight);
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
        var y = index * RowHeight;
        if (y < _scrollY) _scrollY = y;
        else if (y + RowHeight > _scrollY + Bounds.Height)
            _scrollY = y + RowHeight - Bounds.Height;
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
