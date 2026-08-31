using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.App.Controls;

public sealed partial class DiffViewer
{
    private const double TextDragThresholdPx = 4;

    private bool _textSelActive;
    private DiffSide _textSelSide = DiffSide.New;
    private DiffViewerTextSelection.Anchor _textAnchor;
    private DiffViewerTextSelection.Anchor _textFocus;
    private bool _draggingText;
    private bool _pendingCodePress;
    private Point _pressOrigin;
    private DiffViewerTextSelection.Anchor _pressAnchor;
    private DiffSide _pressSide = DiffSide.New;
    private KeyModifiers _pressModifiers;

    private bool HasTextSelection =>
        _textSelActive && !DiffViewerTextSelection.IsEmpty(_textAnchor, _textFocus);

    private void ClearTextSelection()
    {
        if (!_textSelActive && !_pendingCodePress && !_draggingText)
            return;
        _textSelActive = false;
        _draggingText = false;
        _pendingCodePress = false;
        _textAnchor = default;
        _textFocus = default;
        InvalidateVisual();
    }

    private void ClearLineSelection()
    {
        if (_selectionStart < 0 && _selectionEnd < 0)
            return;
        _selectionStart = _selectionEnd = -1;
        InvalidateVisual();
    }

    private string? GetCodeDisplayText(int rowIndex, DiffSide side)
    {
        if (Rows is null || rowIndex < 0 || rowIndex >= Rows.Count)
            return null;

        var row = Rows[rowIndex];
        if (!DiffViewerTextSelection.IsCodeRow(row.Kind))
            return null;

        var memory = DiffViewerTextSelection.GetCodeMemory(row, ViewMode, side);
        // Side-by-side empty pane: skip for copy (null), but hit-test still allows col 0.
        if (ViewMode == DiffViewMode.SideBySide && memory.IsEmpty)
            return null;

        byte sideKey = ViewMode == DiffViewMode.SideBySide
            ? (byte)(side == DiffSide.Old ? 0 : 1)
            : (byte)2;
        return GetDisplayText(rowIndex, sideKey, memory.IsEmpty ? default : memory);
    }

    private string GetCodeDisplayTextOrEmpty(int rowIndex, DiffSide side)
    {
        if (Rows is null || rowIndex < 0 || rowIndex >= Rows.Count)
            return string.Empty;

        var row = Rows[rowIndex];
        if (!DiffViewerTextSelection.IsCodeRow(row.Kind))
            return string.Empty;

        var memory = DiffViewerTextSelection.GetCodeMemory(row, ViewMode, side);
        byte sideKey = ViewMode == DiffViewMode.SideBySide
            ? (byte)(side == DiffSide.Old ? 0 : 1)
            : (byte)2;
        return GetDisplayText(rowIndex, sideKey, memory);
    }

    /// <summary>
    /// Hit-tests the code column. Returns false when the pointer is outside the code run
    /// (gutter, comment lane, divider, minimap, etc.).
    /// </summary>
    private bool TryHitTestCode(Point pos, out DiffViewerTextSelection.Anchor anchor, out DiffSide side)
    {
        anchor = default;
        side = DiffSide.New;
        if (Rows is null || Rows.Count == 0)
            return false;

        var contentLeft = MinimapWidth;
        if (pos.X < contentLeft || IsInHorizontalScrollBar(pos))
            return false;

        var contentWidth = Math.Max(0, Bounds.Width - contentLeft);
        var midX = ViewMode == DiffViewMode.SideBySide
            ? contentLeft + contentWidth / 2
            : contentLeft + contentWidth;

        var index = RowIndexAtContentY(pos.Y + _scrollY);
        if (index < 0 || index >= Rows.Count)
            return false;

        var row = Rows[index];
        double codeX;
        double prefixWidth = 0;
        DiffSide hitSide;

        if (ViewMode == DiffViewMode.SideBySide)
        {
            if (pos.X < midX)
            {
                hitSide = DiffSide.Old;
                codeX = SideBySideCodeX(contentLeft);
                if (pos.X < codeX)
                    return false;
            }
            else
            {
                hitSide = DiffSide.New;
                codeX = SideBySideCodeX(midX);
                if (pos.X < codeX)
                    return false;
            }
        }
        else
        {
            codeX = UnifiedCodeX(contentLeft);
            if (pos.X < codeX)
                return false;

            hitSide = DiffSide.New;
            if (DiffViewerTextSelection.IsCodeRow(row.Kind))
                prefixWidth = MonoCharWidth(); // one-char +/- / space prefix
        }

        side = hitSide;
        var text = DiffViewerTextSelection.IsCodeRow(row.Kind)
            ? GetCodeDisplayTextOrEmpty(index, hitSide)
            : string.Empty;
        var localX = pos.X - codeX + _scrollX - prefixWidth;
        var col = DiffViewerTextSelection.CharIndexAtX(localX, MonoCharWidth(), text.Length);
        anchor = new DiffViewerTextSelection.Anchor(index, col);
        return true;
    }

    private void BeginOrUpdateTextSelection(DiffViewerTextSelection.Anchor focus, DiffSide side)
    {
        if (!_textSelActive)
        {
            _textSelActive = true;
            _textSelSide = side;
            _textAnchor = _pressAnchor;
        }

        // Constrain to the pane where the gesture started.
        if (ViewMode == DiffViewMode.SideBySide && side != _textSelSide)
            return;

        _textFocus = focus;
        ClearLineSelection();
        InvalidateVisual();
    }

    private void DrawTextSelectionForRow(
        DrawingContext ctx,
        int rowIndex,
        DiffRow row,
        double codeX,
        double y,
        double rowH,
        DiffSide side,
        bool includeUnifiedPrefix)
    {
        if (!HasTextSelection || !DiffViewerTextSelection.IsCodeRow(row.Kind))
            return;
        if (ViewMode == DiffViewMode.SideBySide && side != _textSelSide)
            return;

        var (start, end) = DiffViewerTextSelection.Normalize(_textAnchor, _textFocus);
        var text = GetCodeDisplayTextOrEmpty(rowIndex, side);
        var range = DiffViewerTextSelection.GetLineColumnRange(
            rowIndex, start, end, text.Length, isCodeRow: true);
        if (range is null)
            return;

        var (colStart, colEnd) = range.Value;
        if (colStart == colEnd && start.Row == end.Row)
            return;

        var advance = MonoCharWidth();
        if (advance <= 0)
            return;

        var prefixOffset = includeUnifiedPrefix ? advance : 0;
        var left = codeX - _scrollX + prefixOffset + colStart * advance;
        var width = Math.Max(advance * 0.35, (colEnd - colStart) * advance);
        var brush = Brush("ForgeDiffSelectionFillBrush", Brushes.SlateBlue);
        ctx.FillRectangle(brush, new Rect(left, y, width, rowH));
    }

    private async Task CopyTextSelectionAsync()
    {
        if (!HasTextSelection || Rows is null)
            return;

        var (start, end) = DiffViewerTextSelection.Normalize(_textAnchor, _textFocus);
        var side = _textSelSide;
        var text = DiffViewerTextSelection.BuildPlainText(
            start,
            end,
            rowIndex => GetCodeDisplayText(rowIndex, side));

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private async Task CopySelectionAsync()
    {
        if (HasTextSelection)
            await CopyTextSelectionAsync();
        else
            await CopySelectionAsPatchAsync();
    }
}
