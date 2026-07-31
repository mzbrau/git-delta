using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;

namespace CodeReviewr.App.Controls;

/// <summary>Purpose-built virtualized diff control. Fixed row height; paints O(viewport).</summary>
public sealed class DiffViewer : Control
{
    public static readonly StyledProperty<IReadOnlyList<DiffRow>?> RowsProperty =
        AvaloniaProperty.Register<DiffViewer, IReadOnlyList<DiffRow>?>(nameof(Rows));

    public static readonly StyledProperty<DiffViewMode> ViewModeProperty =
        AvaloniaProperty.Register<DiffViewer, DiffViewMode>(nameof(ViewMode), DiffViewMode.Unified);

    public static readonly StyledProperty<bool> ShowWhitespaceProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(ShowWhitespace));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DiffViewer, double>(nameof(RowHeight), 20);

    private const double GutterWidth = 40;

    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private double _scrollY;
    private double _scrollX;
    private readonly Typeface _typeface = new(
        new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"));

    private static readonly IBrush AddedFill = new SolidColorBrush(Color.FromArgb(0x26, 0x22, 0xc5, 0x5e));
    private static readonly IBrush RemovedFill = new SolidColorBrush(Color.FromArgb(0x26, 0xef, 0x44, 0x44));
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x40, 0x33, 0x48, 0x66));
    private static readonly IBrush HeaderFill = new SolidColorBrush(Color.FromArgb(0x28, 0x2d, 0x34, 0x49));
    private static readonly IBrush CollapsedFill = new SolidColorBrush(Color.FromArgb(0x14, 0x8c, 0x90, 0x9f));
    private static readonly IBrush AddedAccent = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly IBrush RemovedAccent = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly IBrush AddedText = new SolidColorBrush(Color.FromRgb(0xbb, 0xf7, 0xd0));
    private static readonly IBrush RemovedText = new SolidColorBrush(Color.FromRgb(0xfe, 0xca, 0xca));
    private static readonly IBrush ContextText = new SolidColorBrush(Color.FromRgb(0xda, 0xe2, 0xfd));
    private static readonly IBrush GutterText = new SolidColorBrush(Color.FromArgb(0x4D, 0xc2, 0xc6, 0xd6));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.FromRgb(0x8c, 0x90, 0x9f));
    private static readonly IBrush ColumnDivider = new SolidColorBrush(Color.FromRgb(0x42, 0x47, 0x54));

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
        AffectsRender<DiffViewer>(RowsProperty, ViewModeProperty, ShowWhitespaceProperty, RowHeightProperty);
        FocusableProperty.OverrideDefaultValue<DiffViewer>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            _selectionStart = _selectionEnd = -1;
            _scrollY = 0;
            InvalidateVisual();
            InvalidateMeasure();
        }
        else if (change.Property == ViewModeProperty || change.Property == RowHeightProperty)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Rows?.Count ?? 0;
        return new Size(availableSize.Width, Math.Max(rows * RowHeight, availableSize.Height));
    }

    public override void Render(DrawingContext context)
    {
        var rows = Rows;
        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x06, 0x0e, 0x20)), new Rect(bounds.Size));

        if (rows is null || rows.Count == 0)
        {
            DrawText(context, "Select a file to view its diff", 16, 16, MutedText);
            return;
        }

        var rowH = RowHeight;
        var first = Math.Max(0, (int)(_scrollY / rowH));
        var last = Math.Min(rows.Count - 1, (int)((_scrollY + bounds.Height) / rowH) + 1);
        var midX = ViewMode == DiffViewMode.SideBySide ? bounds.Width / 2 : bounds.Width;

        if (ViewMode == DiffViewMode.SideBySide)
            context.FillRectangle(ColumnDivider, new Rect(midX - 0.5, 0, 1, bounds.Height));

        for (var i = first; i <= last; i++)
        {
            var row = rows[i];
            var y = i * rowH - _scrollY;
            var selected = _selectionStart >= 0
                           && i >= Math.Min(_selectionStart, _selectionEnd)
                           && i <= Math.Max(_selectionStart, _selectionEnd);

            context.FillRectangle(RowBrush(row.Kind, selected), new Rect(0, y, bounds.Width, rowH));

            if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
            {
                var accent = row.Kind == DiffRowKind.Added ? AddedAccent : RemovedAccent;
                if (ViewMode == DiffViewMode.SideBySide)
                {
                    if (row.Kind == DiffRowKind.Removed || !row.LeftText.IsEmpty)
                        context.FillRectangle(accent, new Rect(0, y, 2, rowH));
                    if (row.Kind == DiffRowKind.Added || !row.RightText.IsEmpty)
                        context.FillRectangle(accent, new Rect(midX, y, 2, rowH));
                }
                else
                {
                    context.FillRectangle(accent, new Rect(0, y, 2, rowH));
                }
            }

            DrawGutter(context, row.OldLineNumber, 0, y);
            if (ViewMode == DiffViewMode.SideBySide)
                DrawGutter(context, row.NewLineNumber, midX, y);
            else
                DrawGutter(context, row.NewLineNumber, GutterWidth, y);

            if (row.Kind == DiffRowKind.HunkHeader)
            {
                DrawText(context, row.LeftText.ToString(), GutterWidth * 2 + 8 - _scrollX, y, MutedText);
                continue;
            }

            if (row.Kind == DiffRowKind.Collapsed)
            {
                DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines ⋯", GutterWidth * 2 + 8, y, MutedText);
                continue;
            }

            if (ViewMode == DiffViewMode.SideBySide)
            {
                if (!row.LeftText.IsEmpty)
                    DrawText(context, FormatText(row.LeftText), GutterWidth + 8 - _scrollX, y, TextBrush(row.Kind));
                if (!row.RightText.IsEmpty)
                    DrawText(context, FormatText(row.RightText), midX + GutterWidth + 8 - _scrollX, y, TextBrush(row.Kind));
            }
            else
            {
                var text = row.Kind == DiffRowKind.Removed ? row.LeftText : row.RightText;
                if (text.IsEmpty) text = row.LeftText.IsEmpty ? row.RightText : row.LeftText;
                var prefix = row.Kind switch
                {
                    DiffRowKind.Added => "+",
                    DiffRowKind.Removed => "-",
                    _ => " ",
                };
                DrawText(context, prefix + FormatText(text), GutterWidth * 2 + 8 - _scrollX, y, TextBrush(row.Kind));
            }
        }
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
        var brush = GutterText;
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

    private static IBrush RowBrush(DiffRowKind kind, bool selected) =>
        selected ? SelectionFill
        : kind switch
        {
            DiffRowKind.Added => AddedFill,
            DiffRowKind.Removed => RemovedFill,
            DiffRowKind.HunkHeader => HeaderFill,
            DiffRowKind.Collapsed => CollapsedFill,
            _ => Brushes.Transparent,
        };

    private static IBrush TextBrush(DiffRowKind kind) =>
        kind switch
        {
            DiffRowKind.Added => AddedText,
            DiffRowKind.Removed => RemovedText,
            _ => ContextText,
        };

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
        var y = e.GetPosition(this).Y + _scrollY;
        var index = (int)(y / RowHeight);
        if (Rows is null || index < 0 || index >= Rows.Count) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selectionStart >= 0)
            _selectionEnd = index;
        else
            _selectionStart = _selectionEnd = index;

        if (SelectedHunkIndex is { } hunk
            && TopLevel.GetTopLevel(this) is Window { DataContext: ViewModels.MainWindowViewModel main })
            main.WorkingCopy.SelectedHunkIndex = hunk;

        InvalidateVisual();
        e.Handled = true;
    }

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
