using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GitDelta.App.Diagnostics;
using GitDelta.App.ViewModels;
using GitDelta.Core;
using GitDelta.Core.Diff;
using GitDelta.Diff;

namespace GitDelta.App.Controls;

/// <summary>Purpose-built virtualized diff control. Fixed row height; paints O(viewport).</summary>
public sealed partial class DiffViewer : Control
{
    private readonly SelectableTextBlock _emptyMessage;
    private readonly StackPanel _brandOverlay;
    private readonly Image _brandLogo;
    private readonly TextBlock _brandTitle;
    private readonly TextBlock _brandCaption;
    private readonly TextBlock _brandDetail;

    public DiffViewer()
    {
        ClipToBounds = true;
        _emptyMessage = new SelectableTextBlock
        {
            Margin = new Thickness(MinimapWidth + 16, 16, 16, 16),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _brandLogo = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://GitDelta.App/Assets/logo.png"));
            _brandLogo.Source = new Bitmap(stream);
        }
        catch
        {
            // Brand hero still shows wordmark + caption without the logo.
        }

        _brandTitle = new TextBlock
        {
            Text = ProductInfo.DisplayName,
            FontSize = 26,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _brandCaption = new TextBlock
        {
            FontSize = 14.5,
            Opacity = 0.7,
            LetterSpacing = 0.4,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        _brandDetail = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
            IsVisible = false,
        };
        _brandOverlay = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Children = { _brandLogo, _brandTitle, _brandCaption, _brandDetail },
        };

        LogicalChildren.Add(_emptyMessage);
        LogicalChildren.Add(_brandOverlay);
        VisualChildren.Add(_emptyMessage);
        VisualChildren.Add(_brandOverlay);
        UpdateEmptyMessage();
    }

    public static readonly StyledProperty<IReadOnlyList<DiffRow>?> RowsProperty =
        AvaloniaProperty.Register<DiffViewer, IReadOnlyList<DiffRow>?>(nameof(Rows));

    public static readonly StyledProperty<DiffViewMode> ViewModeProperty =
        AvaloniaProperty.Register<DiffViewer, DiffViewMode>(nameof(ViewMode), DiffViewMode.Unified);

    public static readonly StyledProperty<bool> ShowWhitespaceProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(ShowWhitespace));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DiffViewer, double>(nameof(FontSize), 12);

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DiffViewer, double>(nameof(RowHeight), 20);

    public static readonly StyledProperty<string> EmptyMessageProperty =
        AvaloniaProperty.Register<DiffViewer, string>(nameof(EmptyMessage), "Select a file to view its diff");

    public static readonly StyledProperty<string?> EmptyDetailProperty =
        AvaloniaProperty.Register<DiffViewer, string?>(nameof(EmptyDetail));

    public static readonly StyledProperty<bool> ShowBrandWatermarkProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(ShowBrandWatermark));

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
    private const double HScrollBarHeight = 10;
    private const double HScrollStep = 40;
    private const double AddCommentHitSize = 14;
    private const double AddCommentHoverScale = 1.2;
    private const double AnnotationDotSize = 8;

    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private int _hoverRowIndex = -1;
    private DiffSide? _hoverSide;
    private bool _hoverAddComment;
    private double _scrollY;
    private double _targetScrollY;
    private double _scrollX;
    private bool _scrollLerpPosted;
    private bool _draggingMinimap;
    private bool _draggingHScroll;
    private double? _maxCodeContentWidthCache;
    private double? _monoCharWidth;
    private bool _rowsInvalidatePosted;
    private bool _annotationsInvalidatePosted;
    private bool _syntaxInvalidatePosted;
    private int _paintEpoch;
    private int _contentEpoch;
    private int _scrollDirection = 1;
    private bool _paintWarmPosted;
    private bool _paintWarmUseRenderPriority;
    private bool _paintWarmBidirectional = true;
    private int _paintWarmBelowCursor = -1;
    private int _paintWarmAboveCursor = -1;
    private long? _pendingScrollGestureTimestamp;
    private long _lastScrollActivityTimestamp;
    private bool _scrollIdleFollowUpPosted;
    private bool _hScrollBarEnabled;
    private bool _pendingHScrollBarReveal;
    private int _lastScrollReportContentEpoch = -1;
    private int _lastScrollReportPaintEpoch = -1;
    private double _lastMaxWidthScanMs;
    private int _paintCacheMissesThisFrame;
    private bool _countPaintMisses;
    private int _maxWidthComputeGeneration;
    private readonly Dictionary<LinePaintKey, LinePaintCache> _linePaintCache = new();
    private readonly Dictionary<DisplayTextKey, string> _displayTextCache = new();
    private readonly Dictionary<int, FormattedText> _gutterCache = new();
    private readonly Dictionary<(string Prefix, IBrush Brush), FormattedText> _prefixCache = new();
    private readonly Dictionary<uint, SolidColorBrush> _intraHighlightBrushes = new();
    private Size _layoutSize;
    private INotifyCollectionChanged? _rowsNotify;
    private INotifyCollectionChanged? _annotationsNotify;
    private readonly List<HunkButtonHit> _hunkButtons = [];
    private readonly List<AnnotationHit> _annotationHits = [];
    private readonly List<AddCommentHit> _addCommentHits = [];
    private MinimapSnapshot? _minimapSnapshot;
    private RenderTargetBitmap? _minimapMarksBitmap;
    private MinimapSnapshot? _minimapMarksBitmapSource;
    private readonly Typeface _typeface = new(
        new FontFamily("avares://GitDelta.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"));

    private const int PaintWarmRowsPerTick = 12;
    private const int PaintWarmRowsPerTickWhileScrolling = 2;
    private const int PaintWarmViewportMultiplier = 2;
    private const double PaintWarmScrollIdleMs = 100;
    private const double BrandHeroLogoFraction = 0.8;
    private const double BrandHeroReservedBelow = 88;
    private const double BrandLogoIdleMax = 112;
    private const double BrandLogoDetailMax = 72;

    private readonly record struct DisplayTextKey(int RowIndex, byte Side, int ContentEpoch);

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

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
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

    public string? EmptyDetail
    {
        get => GetValue(EmptyDetailProperty);
        set => SetValue(EmptyDetailProperty, value);
    }

    public bool ShowBrandWatermark
    {
        get => GetValue(ShowBrandWatermarkProperty);
        set => SetValue(ShowBrandWatermarkProperty, value);
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
            RowsProperty, ViewModeProperty, ShowWhitespaceProperty, FontSizeProperty, RowHeightProperty,
            EmptyMessageProperty, EmptyDetailProperty, ShowBrandWatermarkProperty, CanStageLinesProperty, CanUnstageLinesProperty, CanDiscardLinesProperty,
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
        _draggingMinimap = false;
        _draggingHScroll = false;
        DisposeMinimapMarksBitmap();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ClearPaintCache();
        _gutterCache.Clear();
        _prefixCache.Clear();
        _intraHighlightBrushes.Clear();
        DisposeMinimapMarksBitmap();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            DetachRowsNotify();
            AttachRowsNotify(change.NewValue as INotifyCollectionChanged);
            _selectionStart = _selectionEnd = -1;
            _textSelActive = false;
            _draggingText = false;
            _pendingCodePress = false;
            _textAnchor = default;
            _textFocus = default;
            _hoverRowIndex = -1;
            _hoverSide = null;
            _hoverAddComment = false;
            _scrollY = 0;
            _targetScrollY = 0;
            _scrollX = 0;
            _scrollDirection = 1;
            ResetPaintWarmCursors();
            InvalidateMinimapCaches();
            ClearContentCaches();
            _paintWarmUseRenderPriority = true;
            _paintWarmBidirectional = true;
            UpdateEmptyMessage();
            InvalidateVisual();
            InvalidateMeasure();
            ScheduleMaxWidthCompute();
        }
        else if (change.Property == AnnotationsProperty)
        {
            DetachAnnotationsNotify();
            AttachAnnotationsNotify(change.NewValue as INotifyCollectionChanged);
            InvalidateMinimapCaches();
            InvalidateVisual();
        }
        else if (change.Property == EmptyMessageProperty
                 || change.Property == EmptyDetailProperty
                 || change.Property == ShowBrandWatermarkProperty)
        {
            UpdateEmptyMessage();
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == LeftSyntaxTokensProperty
                 || change.Property == RightSyntaxTokensProperty)
        {
            // Coalesce left+right token assigns in the same dispatcher frame into one
            // paint-cache clear + Render-priority warm burst.
            ScheduleSyntaxPaintInvalidate();
        }
        else if (change.Property == ShowWhitespaceProperty)
        {
            ClearContentCaches();
            ClampScroll();
            InvalidateVisual();
            ScheduleMaxWidthCompute();
        }
        else if (change.Property == FontSizeProperty)
        {
            _monoCharWidth = null;
            ClearContentCaches();
            RowHeight = Math.Max(16, Math.Round(FontSize * 20.0 / 12.0));
            ClampScroll();
            InvalidateMeasure();
            InvalidateVisual();
            ScheduleMaxWidthCompute();
        }
        else if (change.Property == ViewModeProperty || change.Property == RowHeightProperty
                 || change.Property == CanStageLinesProperty
                 || change.Property == CanUnstageLinesProperty
                 || change.Property == CanDiscardLinesProperty
                 || change.Property == SelectedAnnotationProperty
                 || change.Property == InlineInsetAfterRowIndexProperty
                 || change.Property == InlineInsetHeightProperty)
        {
            if (change.Property == ViewModeProperty)
            {
                ClearContentCaches();
                _paintWarmUseRenderPriority = true;
                _paintWarmBidirectional = true;
                ResetPaintWarmCursors();
                ScheduleMaxWidthCompute();
            }
            ClampScroll();
            InvalidateVisual();
        }
    }

    private void UpdateEmptyMessage()
    {
        var empty = Rows is null || Rows.Count == 0;
        var showBrand = empty && ShowBrandWatermark;

        _brandOverlay.IsVisible = showBrand;
        _emptyMessage.IsVisible = empty && !showBrand;

        if (showBrand)
        {
            _brandCaption.Text = EmptyMessage;
            _brandCaption.Foreground = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
            _brandTitle.Foreground = Brush("ForgePrimaryBrush", Brushes.MediumPurple);
            var detail = EmptyDetail;
            var hasDetail = !string.IsNullOrWhiteSpace(detail);
            _brandDetail.Text = detail ?? "";
            _brandDetail.IsVisible = hasDetail;
            _brandDetail.Foreground = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
            if (Application.Current?.TryGetResource("ForgeUiFont", ActualThemeVariant, out var font) == true
                && font is FontFamily uiFont)
            {
                _brandTitle.FontFamily = uiFont;
                _brandCaption.FontFamily = uiFont;
                _brandDetail.FontFamily = uiFont;
            }
        }
        else if (empty)
        {
            _emptyMessage.Text = EmptyMessage;
            _emptyMessage.Foreground = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);
            _brandDetail.IsVisible = false;
        }
        else
        {
            _brandDetail.IsVisible = false;
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
        // Coalesce Reset and Add storms into one posted invalidate so SelectionChanged /
        // ListBox sync can finish before DiffViewer teardown (max-width, warm, measure).
        if (_rowsInvalidatePosted)
            return;

        _rowsInvalidatePosted = true;
        // Loaded is after Input — avoids re-entering heavy DiffViewer work on the selection stack.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rowsInvalidatePosted = false;
            InvalidateRowsState();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void InvalidateRowsState()
    {
        InvalidateMinimapCaches();
        ResetPaintWarmCursors();
        ClearContentCaches();
        _paintWarmUseRenderPriority = true;
        _paintWarmBidirectional = true;
        UpdateEmptyMessage();
        ClampScroll();
        InvalidateVisual();
        InvalidateMeasure();
        ScheduleMaxWidthCompute();
    }

    private void ScheduleSyntaxPaintInvalidate()
    {
        if (_syntaxInvalidatePosted)
            return;

        // Clear immediately so a same-frame paint never reuses stale plain FormattedText
        // after tokens are bound; coalesce the warm burst + invalidate for left+right assigns.
        ClearLinePaintCache();
        _syntaxInvalidatePosted = true;
        _paintWarmUseRenderPriority = true;
        _paintWarmBidirectional = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syntaxInvalidatePosted = false;
            ClampScroll();
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void ResetPaintWarmCursors()
    {
        _paintWarmBelowCursor = -1;
        _paintWarmAboveCursor = -1;
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

    private void OnAnnotationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Coalesce annotation Add/Remove storms (local+AI reload) into one invalidate,
        // matching OnRowsCollectionChanged — avoids paint thrash while scrolling.
        if (e.Action != NotifyCollectionChangedAction.Reset && _annotationsInvalidatePosted)
            return;

        if (e.Action != NotifyCollectionChangedAction.Reset)
        {
            _annotationsInvalidatePosted = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _annotationsInvalidatePosted = false;
                InvalidateMinimapCaches();
                InvalidateVisual();
            }, Avalonia.Threading.DispatcherPriority.Render);
            return;
        }

        InvalidateMinimapCaches();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        if (_emptyMessage.IsVisible)
            _emptyMessage.Measure(new Size(Math.Max(0, width - MinimapWidth - 32), height));
        if (_brandOverlay.IsVisible)
        {
            var contentWidth = Math.Max(0, width - MinimapWidth);
            ApplyBrandLogoSize(contentWidth, height);
            _brandOverlay.Measure(new Size(contentWidth, height));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_emptyMessage.IsVisible)
        {
            var margin = _emptyMessage.Margin;
            var rect = new Rect(
                margin.Left,
                margin.Top,
                Math.Max(0, finalSize.Width - margin.Left - margin.Right),
                Math.Max(0, finalSize.Height - margin.Top - margin.Bottom));
            _emptyMessage.Arrange(rect);
        }
        else
        {
            _emptyMessage.Arrange(default);
        }

        if (_brandOverlay.IsVisible)
        {
            var contentLeft = MinimapWidth;
            var contentWidth = Math.Max(0, finalSize.Width - contentLeft);
            var contentHeight = finalSize.Height;
            ApplyBrandLogoSize(contentWidth, contentHeight);
            _brandOverlay.Measure(new Size(contentWidth, contentHeight));
            var desired = _brandOverlay.DesiredSize;
            var x = contentLeft + Math.Max(0, (contentWidth - desired.Width) / 2);
            var y = Math.Max(0, (contentHeight - desired.Height) / 2);
            _brandOverlay.Arrange(new Rect(x, y, desired.Width, desired.Height));
        }
        else
        {
            _brandOverlay.Arrange(default);
        }

        _layoutSize = finalSize;
        ClampScroll(notify: false);
        return finalSize;
    }

    private void ApplyBrandLogoSize(double contentWidth, double contentHeight)
    {
        var maxLogo = string.IsNullOrWhiteSpace(EmptyDetail) ? BrandLogoIdleMax : BrandLogoDetailMax;
        var reservedBelow = string.IsNullOrWhiteSpace(EmptyDetail)
            ? BrandHeroReservedBelow
            : BrandHeroReservedBelow + 120;
        var logoSize = Math.Min(contentWidth, contentHeight) * BrandHeroLogoFraction;
        logoSize = Math.Min(logoSize, Math.Max(32, contentHeight - reservedBelow));
        logoSize = Math.Max(32, Math.Min(logoSize, contentWidth));
        logoSize = Math.Min(logoSize, maxLogo);
        _brandLogo.Width = logoSize;
        _brandLogo.Height = logoSize;
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
        var renderSw = Stopwatch.StartNew();
        _hunkButtons.Clear();
        _annotationHits.Clear();
        _addCommentHits.Clear();
        _paintCacheMissesThisFrame = 0;
        _countPaintMisses = true;
        var rows = Rows;
        var bounds = Bounds;
        using var clip = context.PushClip(new Rect(bounds.Size));
        var bg = Brush("ForgeSurfaceContainerLowestBrush", Brushes.Black);
        context.FillRectangle(bg, new Rect(bounds.Size));

        if (rows is null || rows.Count == 0)
        {
            _countPaintMisses = false;
            return;
        }

        var contentLeft = MinimapWidth;
        var contentWidth = Math.Max(0, bounds.Width - contentLeft);
        var viewportHeight = ViewportHeight;
        var rowH = RowHeight;
        var first = Math.Max(0, RowIndexAtContentY(_scrollY));
        var last = Math.Min(rows.Count - 1, RowIndexAtContentY(_scrollY + viewportHeight) + 1);
        var visibleCount = last - first + 1;
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
                        DrawText(context, GetDisplayText(i, side: 0, row.LeftText), SideBySideCodeX(contentLeft) - _scrollX, y, muted);
                    else if (row.Kind == DiffRowKind.Collapsed)
                        DrawText(context, $"⋯ {row.CollapsedCount} unchanged lines — click to expand",
                            SideBySideCodeX(contentLeft), y, muted);
                    else if (!row.LeftText.IsEmpty)
                    {
                        var x = SideBySideCodeX(contentLeft);
                        var leftFormatted = GetDisplayText(i, side: 0, row.LeftText);
                        DrawTextSelectionForRow(context, i, row, x, y, rowH, DiffSide.Old, includeUnifiedPrefix: false);
                        DrawIntraLineHighlights(context, leftFormatted, row.LeftIntraLine, x - _scrollX, y, rowH, removedAccent);
                        DrawSyntaxOrPlainText(context, i, side: 0, leftFormatted, x - _scrollX, y,
                            TextBrush(leftKind, contextText), LeftSyntaxTokens, row.OldLineNumber);
                    }
                }

                using (context.PushClip(new Rect(midX, 0, bounds.Width - midX, bounds.Height)))
                {
                    DrawGutter(context, row.NewLineNumber, midX, y);
                    if (row.Kind is not DiffRowKind.HunkHeader and not DiffRowKind.Collapsed && !row.RightText.IsEmpty)
                    {
                        var x = SideBySideCodeX(midX);
                        var rightFormatted = GetDisplayText(i, side: 1, row.RightText);
                        DrawTextSelectionForRow(context, i, row, x, y, rowH, DiffSide.New, includeUnifiedPrefix: false);
                        DrawIntraLineHighlights(context, rightFormatted, row.RightIntraLine, x - _scrollX, y, rowH, addedAccent);
                        DrawSyntaxOrPlainText(context, i, side: 1, rightFormatted, x - _scrollX, y,
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
                    DrawText(context, GetDisplayText(i, side: 0, row.LeftText), UnifiedCodeX(contentLeft) - _scrollX, y, muted);
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
                var formatted = GetDisplayText(i, side: 2, text);
                var x = UnifiedCodeX(contentLeft);
                var intra = row.Kind == DiffRowKind.Removed ? row.LeftIntraLine : row.RightIntraLine;
                var accent = row.Kind == DiffRowKind.Added ? addedAccent : removedAccent;
                // Offset highlights by the +/- prefix width.
                var prefixBrush = TextBrush(row.Kind, contextText);
                var drawX = x - _scrollX;
                var prefixWidth = DrawPrefix(context, prefix, drawX, y, prefixBrush);
                DrawTextSelectionForRow(context, i, row, x, y, rowH, DiffSide.New, includeUnifiedPrefix: true);
                DrawIntraLineHighlights(context, formatted, intra, drawX + prefixWidth, y, rowH, accent);
                var tokens = row.Kind == DiffRowKind.Removed ? LeftSyntaxTokens : RightSyntaxTokens;
                var lineNo = row.Kind == DiffRowKind.Removed ? row.OldLineNumber : row.NewLineNumber;
                if (row.Kind == DiffRowKind.Context)
                {
                    tokens = RightSyntaxTokens ?? LeftSyntaxTokens;
                    lineNo = row.NewLineNumber ?? row.OldLineNumber;
                }

                DrawSyntaxOrPlainText(context, i, side: 2, formatted, drawX + prefixWidth, y,
                    TextBrush(row.Kind, contextText), tokens, lineNo);
            }

            DrawAnnotationMarkers(context, row, contentLeft, midX, y, rowH);
            DrawAddCommentAffordance(context, row, i, contentLeft, midX, y, rowH);
        }

        DrawHorizontalScrollbar(context, bounds);
        _countPaintMisses = false;
        OpenTelemetryBootstrap.RecordDiffRender(renderSw.Elapsed.TotalMilliseconds, visibleCount);
        RecordPendingScrollGesture();
        SchedulePaintWarm(first, last, rows.Count);
    }

    private void RecordPendingScrollGesture()
    {
        if (_pendingScrollGestureTimestamp is not { } started)
            return;

        var gestureToPaintMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _pendingScrollGestureTimestamp = null;

        var firstAfterBind = _contentEpoch != _lastScrollReportContentEpoch
                             || _paintEpoch != _lastScrollReportPaintEpoch;
        _lastScrollReportContentEpoch = _contentEpoch;
        _lastScrollReportPaintEpoch = _paintEpoch;

        var warmPending = _paintWarmPosted || IsPaintWarmPending();
        OpenTelemetryBootstrap.RecordDiffScroll(
            gestureToPaintMs,
            _paintCacheMissesThisFrame,
            _lastMaxWidthScanMs,
            warmPending,
            firstAfterBind,
            _scrollDirection);
    }

    private bool IsPaintWarmPending()
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0)
            return false;

        var rowH = RowHeight;
        if (rowH <= 0)
            return false;

        var first = Math.Max(0, RowIndexAtContentY(_scrollY));
        var last = Math.Min(rows.Count - 1, RowIndexAtContentY(_scrollY + ViewportHeight) + 1);
        var band = Math.Max(PaintWarmRowsPerTick, (int)(ViewportHeight / rowH) * PaintWarmViewportMultiplier);
        var belowStart = last + 1;
        var belowEnd = Math.Min(rows.Count - 1, last + band);
        var aboveEnd = first - 1;
        var aboveStart = Math.Max(0, first - band);
        var belowPending = belowStart <= belowEnd
                           && (_paintWarmBelowCursor < 0 || _paintWarmBelowCursor <= belowEnd);
        var abovePending = aboveStart <= aboveEnd
                           && (_paintWarmAboveCursor < 0 || _paintWarmAboveCursor >= aboveStart);
        return belowPending || abovePending;
    }

    private static double UnifiedCodeX(double contentLeft) =>
        contentLeft + GutterWidth * 2 + CommentLaneWidth + CodePadding;

    private static double SideBySideCodeX(double paneLeft) =>
        paneLeft + GutterWidth + CommentLaneWidth + CodePadding;

    private double LayoutWidth => _layoutSize.Width > 0 ? _layoutSize.Width : Bounds.Width;

    private double LayoutHeight => _layoutSize.Height > 0 ? _layoutSize.Height : Bounds.Height;

    private double ViewportCodeWidth()
    {
        var contentLeft = MinimapWidth;
        var contentWidth = Math.Max(0, LayoutWidth - contentLeft);
        if (ViewMode == DiffViewMode.SideBySide)
        {
            var paneWidth = contentWidth / 2;
            return Math.Max(0, paneWidth - (GutterWidth + CommentLaneWidth + CodePadding));
        }

        return Math.Max(0, LayoutWidth - UnifiedCodeX(contentLeft));
    }

    private double GetMaxCodeContentWidth()
    {
        if (_maxCodeContentWidthCache is { } cached)
            return cached;

        // Never scan O(rows) on the UI/input/paint path — ScheduleMaxWidthCompute fills this.
        return 0;
    }

    private void ScheduleMaxWidthCompute()
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0)
        {
            _maxCodeContentWidthCache = 0;
            _lastMaxWidthScanMs = 0;
            _hScrollBarEnabled = false;
            _pendingHScrollBarReveal = false;
            return;
        }

        if (_maxCodeContentWidthCache is not null)
            return;

        var generation = ++_maxWidthComputeGeneration;
        var contentEpoch = _contentEpoch;
        var mode = ViewMode;
        var rowsSnapshot = rows;

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var maxChars = DiffViewerLayout.ComputeMaxDisplayChars(rowsSnapshot, mode);
            var scanMs = sw.Elapsed.TotalMilliseconds;

            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _maxWidthComputeGeneration
                    || contentEpoch != _contentEpoch
                    || mode != ViewMode
                    || !ReferenceEquals(Rows, rowsSnapshot))
                {
                    return;
                }

                _lastMaxWidthScanMs = scanMs;
                _maxCodeContentWidthCache = maxChars * MonoCharWidth();
                if (IsPaintWarmPausedForScroll() && NeedsHorizontalScroll)
                {
                    // Defer bar chrome until scroll idle so ViewportHeight does not jump mid-gesture.
                    _pendingHScrollBarReveal = true;
                    ClampScroll(notify: false);
                    InvalidateVisual();
                }
                else
                {
                    ApplyHScrollBarVisibility();
                }
            });
        });
    }

    private double MonoCharWidth()
    {
        if (_monoCharWidth is { } cached)
            return cached;

        var ft = CreateFormattedText("M", FontSize, Brushes.Transparent);
        _monoCharWidth = ft.WidthIncludingTrailingWhitespace;
        return _monoCharWidth.Value;
    }

    private double MaxScrollX()
    {
        if (_maxCodeContentWidthCache is not { } width)
            return 0;
        return Math.Max(0, width - ViewportCodeWidth());
    }

    private bool NeedsHorizontalScroll =>
        DiffViewerLayout.HasCachedHorizontalScroll(_maxCodeContentWidthCache, ViewportCodeWidth());

    /// <summary>
    /// When max-width is not yet known or the bar is deferred during scroll, reserve nothing
    /// so InvalidateRowsState / first paint / mid-gesture viewport height stay stable.
    /// </summary>
    private double HScrollReserve =>
        _hScrollBarEnabled ? HScrollBarHeight : 0;

    private double ViewportHeight => Math.Max(0, LayoutHeight - HScrollReserve);

    private Rect HorizontalScrollTrackBounds()
    {
        var trackLeft = MinimapWidth;
        return new Rect(
            trackLeft,
            LayoutHeight - HScrollBarHeight,
            Math.Max(0, LayoutWidth - trackLeft),
            HScrollBarHeight);
    }

    private bool IsInHorizontalScrollBar(Point pos) =>
        _hScrollBarEnabled && NeedsHorizontalScroll && HorizontalScrollTrackBounds().Contains(pos);

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
        var aiBrush = Brush("ForgeAiAccentBrush", Brushes.MediumPurple);
        var aiDismissedBrush = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray);

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
                : annotation is AiLineAnnotation ai
                    ? (ai.IsDismissed ? aiDismissedBrush : aiBrush)
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
        if (InlineInsetAfterRowIndex == -1 && InlineInsetHeight == 0)
            return;

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

    private void DrawHorizontalScrollbar(DrawingContext context, Rect bounds)
    {
        // Skip until background max-width compute finishes — and until scroll-idle reveal.
        if (!_hScrollBarEnabled || _maxCodeContentWidthCache is null)
            return;

        var maxX = MaxScrollX();
        if (maxX <= 0.5)
            return;

        var track = HorizontalScrollTrackBounds();
        context.FillRectangle(
            Brush("ForgeMinimapTrackBrush", Brushes.Transparent),
            track);

        var maxContent = Math.Max(1, GetMaxCodeContentWidth());
        var viewport = ViewportCodeWidth();
        var thumbWidth = Math.Max(HScrollBarHeight, track.Width * (viewport / maxContent));
        var thumbTravel = Math.Max(0, track.Width - thumbWidth);
        var thumbX = track.X + (_scrollX / maxX) * thumbTravel;
        context.FillRectangle(
            Brush("ForgeMinimapViewportBrush", Brushes.Gray),
            new Rect(thumbX, track.Y + 1, thumbWidth, Math.Max(1, track.Height - 2)),
            2);
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
            Math.Max(9, FontSize - 1),
            Brush("ForgeOnSurfaceVariantBrush", Brushes.LightGray));
        context.DrawText(ft, new Point(
            rect.X + (rect.Width - ft.Width) / 2,
            rect.Y + (rect.Height - ft.Height) / 2));
    }

    private void ClearContentCaches()
    {
        _contentEpoch++;
        _maxWidthComputeGeneration++;
        _displayTextCache.Clear();
        _maxCodeContentWidthCache = null;
        _lastMaxWidthScanMs = 0;
        _hScrollBarEnabled = false;
        _pendingHScrollBarReveal = false;
        ClearPaintCache();
    }

    private string GetDisplayText(int rowIndex, byte side, ReadOnlyMemory<char> text)
    {
        var key = new DisplayTextKey(rowIndex, side, _contentEpoch);
        if (_displayTextCache.TryGetValue(key, out var cached))
            return cached;

        var formatted = FormatText(text);
        _displayTextCache[key] = formatted;
        return formatted;
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
        if (!_gutterCache.TryGetValue(line.Value, out var ft))
        {
            var brush = Brush("ForgeDiffGutterTextBrush", Brushes.Gray);
            ft = CreateFormattedText(line.Value.ToString(), FontSize, brush);
            _gutterCache[line.Value] = ft;
        }

        ctx.DrawText(ft, new Point(x + GutterWidth - ft.Width - 4, y + (RowHeight - ft.Height) / 2));
    }

    /// <summary>Draws <paramref name="text"/> and returns its advance width (same FormattedText).</summary>
    private double DrawText(DrawingContext ctx, string text, double x, double y, IBrush brush)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var ft = CreateFormattedText(text, FontSize, brush);
        ctx.DrawText(ft, new Point(x, y + (RowHeight - ft.Height) / 2));
        return ft.WidthIncludingTrailingWhitespace;
    }

    private double DrawPrefix(DrawingContext ctx, string prefix, double x, double y, IBrush brush)
    {
        if (!_prefixCache.TryGetValue((prefix, brush), out var ft))
        {
            ft = CreateFormattedText(prefix, FontSize, brush);
            _prefixCache[(prefix, brush)] = ft;
        }

        ctx.DrawText(ft, new Point(x, y + (RowHeight - ft.Height) / 2));
        return ft.WidthIncludingTrailingWhitespace;
    }

    private FormattedText CreateFormattedText(string text, double fontSize, IBrush brush) =>
        new(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            fontSize,
            brush);

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

        var highlight = GetIntraHighlightBrush(accent);
        var advance = MonoCharWidth();

        foreach (var span in spans)
        {
            if (span.Length <= 0 || span.Start < 0 || span.Start >= text.Length)
                continue;
            var len = Math.Min(span.Length, text.Length - span.Start);
            if (len <= 0) continue;
            var left = x + span.Start * advance;
            var width = len * advance;
            if (width <= 0) continue;
            ctx.FillRectangle(highlight, new Rect(left, y, width, rowH));
        }
    }

    private IBrush GetIntraHighlightBrush(IBrush accent)
    {
        if (accent is not ISolidColorBrush solid)
            return accent;

        var key = Color.FromArgb(0x55, solid.Color.R, solid.Color.G, solid.Color.B).ToUInt32();
        if (_intraHighlightBrushes.TryGetValue(key, out var cached))
            return cached;

        var brush = new SolidColorBrush(Color.FromUInt32(key));
        _intraHighlightBrushes[key] = brush;
        return brush;
    }

    private void DrawSyntaxOrPlainText(
        DrawingContext ctx,
        int rowIndex,
        byte side,
        string text,
        double x,
        double y,
        IBrush fallback,
        FileSyntaxTokens? tokens,
        int? oneBasedLine)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var cache = GetOrCreateLinePaint(rowIndex, side, text, fallback, tokens, oneBasedLine);
        var drawX = x;
        foreach (var (ft, width) in cache.Segments)
        {
            ctx.DrawText(ft, new Point(drawX, y + (RowHeight - ft.Height) / 2));
            drawX += width;
        }
    }

    private void SchedulePaintWarm(int firstVisible, int lastVisible, int rowCount)
    {
        if (rowCount <= 0 || _paintWarmPosted)
            return;

        _paintWarmPosted = true;
        var epoch = _paintEpoch;
        var contentEpoch = _contentEpoch;
        var direction = _scrollDirection;
        var bidirectional = _paintWarmBidirectional;
        var scrolling = IsPaintWarmPausedForScroll();
        var priority = !scrolling && _paintWarmUseRenderPriority
            ? DispatcherPriority.Render
            : DispatcherPriority.Background;
        Dispatcher.UIThread.Post(() =>
        {
            _paintWarmPosted = false;
            if (epoch != _paintEpoch || contentEpoch != _contentEpoch)
                return;

            var stillScrolling = IsPaintWarmPausedForScroll();
            if (!stillScrolling)
                _paintWarmUseRenderPriority = false;

            var budget = stillScrolling
                ? PaintWarmRowsPerTickWhileScrolling
                : PaintWarmRowsPerTick;
            WarmPaintCache(direction, bidirectional, budget);
        }, priority);
    }

    private void WarmPaintCache(int direction, bool bidirectional, int budget)
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0)
            return;

        var rowH = RowHeight;
        if (rowH <= 0)
            return;

        // Prefer live viewport — scroll may have moved since this tick was queued.
        var firstVisible = Math.Max(0, RowIndexAtContentY(_scrollY));
        var lastVisible = Math.Min(rows.Count - 1, RowIndexAtContentY(_scrollY + ViewportHeight) + 1);

        var band = Math.Max(PaintWarmRowsPerTick, (int)(ViewportHeight / rowH) * PaintWarmViewportMultiplier);
        var belowStart = lastVisible + 1;
        var belowEnd = Math.Min(rows.Count - 1, lastVisible + band);
        var aboveEnd = firstVisible - 1;
        var aboveStart = Math.Max(0, firstVisible - band);

        var muted = Brush("ForgeOnSurfaceBrush", Brushes.White);
        var preferBelow = direction >= 0;

        if (preferBelow)
        {
            budget -= WarmRange(rows, ref _paintWarmBelowCursor, belowStart, belowEnd, step: 1, budget, muted);
            if (bidirectional || direction < 0)
                budget -= WarmRange(rows, ref _paintWarmAboveCursor, aboveStart, aboveEnd, step: -1, budget, muted);
        }
        else
        {
            budget -= WarmRange(rows, ref _paintWarmAboveCursor, aboveStart, aboveEnd, step: -1, budget, muted);
            if (bidirectional || direction >= 0)
                budget -= WarmRange(rows, ref _paintWarmBelowCursor, belowStart, belowEnd, step: 1, budget, muted);
        }

        var belowPending = belowStart <= belowEnd
                           && (_paintWarmBelowCursor < 0 || _paintWarmBelowCursor <= belowEnd);
        var abovePending = aboveStart <= aboveEnd
                           && (_paintWarmAboveCursor < 0 || _paintWarmAboveCursor >= aboveStart);

        if (bidirectional && !belowPending && !abovePending)
            _paintWarmBidirectional = false;

        if ((preferBelow && belowPending) || (!preferBelow && abovePending)
            || (bidirectional && (belowPending || abovePending)))
        {
            SchedulePaintWarm(firstVisible, lastVisible, rows.Count);
        }
    }

    private void NoteScrollActivity()
    {
        _lastScrollActivityTimestamp = Stopwatch.GetTimestamp();
        ScheduleScrollIdleFollowUp();
    }

    private bool IsPaintWarmPausedForScroll() =>
        DiffViewerLayout.IsScrollWarmPaused(
            _lastScrollActivityTimestamp,
            Stopwatch.GetTimestamp(),
            PaintWarmScrollIdleMs);

    private void ScheduleScrollIdleFollowUp()
    {
        if (_scrollIdleFollowUpPosted)
            return;

        _scrollIdleFollowUpPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollIdleFollowUpPosted = false;
            if (IsPaintWarmPausedForScroll())
            {
                ScheduleScrollIdleFollowUp();
                return;
            }

            if (_pendingHScrollBarReveal)
                ApplyHScrollBarVisibility();

            var rows = Rows;
            if (rows is null || rows.Count == 0 || _paintWarmPosted)
                return;

            var first = Math.Max(0, RowIndexAtContentY(_scrollY));
            var last = Math.Min(rows.Count - 1, RowIndexAtContentY(_scrollY + ViewportHeight) + 1);
            SchedulePaintWarm(first, last, rows.Count);
        }, DispatcherPriority.Background);
    }

    private void ApplyHScrollBarVisibility()
    {
        _pendingHScrollBarReveal = false;
        var need = NeedsHorizontalScroll;
        if (need == _hScrollBarEnabled)
            return;

        _hScrollBarEnabled = need;
        ClampScroll(notify: false);
        InvalidateVisual();
    }

    private bool ReleaseOwnedPointerCapture(IPointer pointer)
    {
        var released = pointer.Captured == this;
        _draggingMinimap = false;
        _draggingHScroll = false;
        if (released)
            pointer.Capture(null);
        return released;
    }

    private int WarmRange(
        IReadOnlyList<DiffRow> rows,
        ref int cursor,
        int start,
        int end,
        int step,
        int budget,
        IBrush fallback)
    {
        if (budget <= 0 || start > end)
            return 0;

        if (step > 0)
        {
            if (cursor > end)
                return 0;
            if (cursor < start)
                cursor = start;
        }
        else
        {
            if (cursor >= 0 && cursor < start)
                return 0;
            if (cursor < 0 || cursor > end)
                cursor = end;
        }

        var warmed = 0;
        while (warmed < budget && cursor >= start && cursor <= end)
        {
            WarmRow(rows, cursor, fallback);
            warmed++;
            cursor += step;
        }

        return warmed;
    }

    private void WarmRow(IReadOnlyList<DiffRow> rows, int index, IBrush fallback)
    {
        if ((uint)index >= (uint)rows.Count)
            return;

        var row = rows[index];
        if (row.Kind is DiffRowKind.HunkHeader or DiffRowKind.Collapsed or DiffRowKind.Padding)
            return;

        if (ViewMode == DiffViewMode.SideBySide)
        {
            if (!row.LeftText.IsEmpty)
            {
                var left = GetDisplayText(index, side: 0, row.LeftText);
                var leftKind = DiffRowPresentation.SideBySideLeftKind(row);
                GetOrCreateLinePaint(index, side: 0, left, TextBrush(leftKind, fallback), LeftSyntaxTokens, row.OldLineNumber);
            }

            if (!row.RightText.IsEmpty)
            {
                var right = GetDisplayText(index, side: 1, row.RightText);
                var rightKind = DiffRowPresentation.SideBySideRightKind(row);
                GetOrCreateLinePaint(index, side: 1, right, TextBrush(rightKind, fallback), RightSyntaxTokens, row.NewLineNumber);
            }

            return;
        }

        var text = row.Kind == DiffRowKind.Removed ? row.LeftText : row.RightText;
        if (text.IsEmpty) text = row.LeftText.IsEmpty ? row.RightText : row.LeftText;
        if (text.IsEmpty)
            return;

        var formatted = GetDisplayText(index, side: 2, text);
        var tokens = row.Kind == DiffRowKind.Removed ? LeftSyntaxTokens : RightSyntaxTokens;
        var lineNo = row.Kind == DiffRowKind.Removed ? row.OldLineNumber : row.NewLineNumber;
        if (row.Kind == DiffRowKind.Context)
        {
            tokens = RightSyntaxTokens ?? LeftSyntaxTokens;
            lineNo = row.NewLineNumber ?? row.OldLineNumber;
        }

        GetOrCreateLinePaint(index, side: 2, formatted, TextBrush(row.Kind, fallback), tokens, lineNo);
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

    private void ClampScroll(bool notify = true)
    {
        var rows = Rows;
        if (rows is null)
        {
            var cleared = false;
            if (_scrollY != 0 || _targetScrollY != 0)
            {
                _scrollY = 0;
                _targetScrollY = 0;
                cleared = true;
            }
            if (_scrollX != 0)
            {
                _scrollX = 0;
                cleared = true;
            }
            if (cleared && notify)
                NotifyViewportChanged();
            return;
        }

        var maxY = Math.Max(0, TotalContentHeight(rows.Count) - ViewportHeight);
        var nextY = Math.Clamp(_scrollY, 0, maxY);
        var nextTargetY = Math.Clamp(_targetScrollY, 0, maxY);
        // Skip MaxScrollX when already at origin so InvalidateRowsState does not force a full width scan.
        var nextX = _scrollX <= 0.01 ? 0 : Math.Clamp(_scrollX, 0, MaxScrollX());
        var changed = false;
        if (Math.Abs(nextY - _scrollY) > 0.01)
        {
            _scrollY = nextY;
            changed = true;
        }
        if (Math.Abs(nextTargetY - _targetScrollY) > 0.01)
            _targetScrollY = nextTargetY;
        if (Math.Abs(nextX - _scrollX) > 0.01)
        {
            _scrollX = nextX;
            changed = true;
        }
        if (changed && notify)
            NotifyViewportChanged();
    }

    /// <summary>Accumulates wheel target and drives Render-priority catch-up toward it.</summary>
    private void SetTargetScrollY(double targetY, bool noteActivity = true)
    {
        var rows = Rows;
        var max = rows is null
            ? 0
            : Math.Max(0, TotalContentHeight(rows.Count) - ViewportHeight);
        var next = Math.Clamp(targetY, 0, max);
        if (Math.Abs(next - _targetScrollY) <= 0.01)
        {
            if (Math.Abs(next - _scrollY) > DiffViewerLayout.DefaultScrollLerpSnapPx)
                ScheduleScrollLerp();
            return;
        }

        _scrollDirection = next > _scrollY ? 1 : -1;
        _targetScrollY = next;
        if (noteActivity)
        {
            _pendingScrollGestureTimestamp ??= Stopwatch.GetTimestamp();
            NoteScrollActivity();
        }

        ScheduleScrollLerp();
    }

    /// <summary>Snaps display + target (minimap scrub / programmatic EnsureVisible).</summary>
    private void SnapScrollY(double y, bool noteActivity = true)
    {
        var rows = Rows;
        var max = rows is null
            ? 0
            : Math.Max(0, TotalContentHeight(rows.Count) - ViewportHeight);
        var next = Math.Clamp(y, 0, max);
        if (Math.Abs(next - _scrollY) <= 0.01 && Math.Abs(next - _targetScrollY) <= 0.01)
            return;

        _scrollDirection = next > _scrollY ? 1 : -1;
        _scrollY = next;
        _targetScrollY = next;
        if (noteActivity)
            NoteScrollActivity();
        NotifyViewportChanged();
        InvalidateVisual();
    }

    private void ScheduleScrollLerp()
    {
        if (_scrollLerpPosted)
            return;
        if (Math.Abs(_scrollY - _targetScrollY) <= DiffViewerLayout.DefaultScrollLerpSnapPx)
        {
            if (Math.Abs(_scrollY - _targetScrollY) > 0.01)
            {
                _scrollY = _targetScrollY;
                NotifyViewportChanged();
                InvalidateVisual();
            }
            return;
        }

        _scrollLerpPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollLerpPosted = false;
            var next = DiffViewerLayout.StepScrollLerp(_scrollY, _targetScrollY);
            if (Math.Abs(next - _scrollY) > 0.01)
            {
                _scrollY = next;
                NotifyViewportChanged();
                InvalidateVisual();
            }

            if (Math.Abs(_scrollY - _targetScrollY) > DiffViewerLayout.DefaultScrollLerpSnapPx)
                ScheduleScrollLerp();
            else if (Math.Abs(_scrollY - _targetScrollY) > 0.01)
            {
                _scrollY = _targetScrollY;
                NotifyViewportChanged();
                InvalidateVisual();
            }
        }, DispatcherPriority.Render);
    }

    private void ScrollFromMinimapY(double y)
    {
        var rows = Rows;
        if (rows is null || rows.Count == 0) return;
        var contentHeight = TotalContentHeight(rows.Count);
        var viewportHeight = ViewportHeight;
        var ratio = Math.Clamp(y / Math.Max(1, LayoutHeight), 0, 1);
        var next = Math.Clamp(ratio * Math.Max(0, contentHeight - viewportHeight), 0, Math.Max(0, contentHeight - viewportHeight));
        SnapScrollY(next);
    }

    private void ScrollFromHScrollX(double x)
    {
        var maxX = MaxScrollX();
        if (maxX <= 0) return;
        var track = HorizontalScrollTrackBounds();
        var ratio = Math.Clamp((x - track.X) / Math.Max(1, track.Width), 0, 1);
        var next = ratio * maxX;
        if (Math.Abs(next - _scrollX) > 0.01)
        {
            _scrollX = next;
            NoteScrollActivity();
            NotifyViewportChanged();
        }
        InvalidateVisual();
    }

    private void ScrollHorizontally(double delta)
    {
        var next = Math.Clamp(_scrollX + delta, 0, MaxScrollX());
        if (Math.Abs(next - _scrollX) <= 0.01) return;
        _scrollX = next;
        NotifyViewportChanged();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Scroll must not keep an old press/minimap capture — that routes the next
        // MouseDown to DiffViewer and eats the first click on file list / chrome.
        var releasedCapture = ReleaseOwnedPointerCapture(e.Pointer);

        var rows = Rows;
        if (rows is null)
        {
            if (releasedCapture)
                e.Handled = true;
            return;
        }

        var changed = false;
        if (Math.Abs(e.Delta.X) > 0.01)
        {
            var nextX = Math.Clamp(_scrollX - e.Delta.X * HScrollStep, 0, MaxScrollX());
            if (Math.Abs(nextX - _scrollX) > 0.01)
            {
                _scrollX = nextX;
                changed = true;
            }
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var nextX = Math.Clamp(_scrollX - e.Delta.Y * HScrollStep, 0, MaxScrollX());
            if (Math.Abs(nextX - _scrollX) > 0.01)
            {
                _scrollX = nextX;
                changed = true;
            }
        }
        else
        {
            var max = Math.Max(0, TotalContentHeight(rows.Count) - ViewportHeight);
            var deltaPx = DiffViewerLayout.VerticalScrollDeltaPixels(e.Delta.Y, RowHeight);
            var nextTarget = Math.Clamp(_targetScrollY - deltaPx, 0, max);
            if (Math.Abs(nextTarget - _targetScrollY) > 0.01 || Math.Abs(nextTarget - _scrollY) > 0.01)
            {
                SetTargetScrollY(nextTarget);
                changed = true;
            }
        }
        if (changed)
        {
            // Horizontal path still needs activity + invalidate; vertical uses SetTargetScrollY.
            if (Math.Abs(e.Delta.X) > 0.01 || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _pendingScrollGestureTimestamp ??= Stopwatch.GetTimestamp();
                NoteScrollActivity();
                NotifyViewportChanged();
                InvalidateVisual();
            }
        }

        if (changed || releasedCapture)
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

        if (IsInHorizontalScrollBar(pos))
        {
            _draggingHScroll = true;
            ScrollFromHScrollX(pos.X);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            ClearTextSelection();
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

        // Shift+click with an active text selection extends the character range.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && HasTextSelection
            && TryHitTestCode(pos, out var shiftAnchor, out var shiftSide)
            && DiffViewerTextSelection.IsCodeRow(Rows[shiftAnchor.Row].Kind)
            && (ViewMode != DiffViewMode.SideBySide || shiftSide == _textSelSide))
        {
            _textFocus = shiftAnchor;
            ClearLineSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Press in the code column: defer line vs text selection until move/release.
        if (point.Properties.IsLeftButtonPressed
            && TryHitTestCode(pos, out var codeAnchor, out var codeSide)
            && DiffViewerTextSelection.IsCodeRow(Rows[codeAnchor.Row].Kind))
        {
            _pendingCodePress = true;
            _draggingText = false;
            _pressOrigin = pos;
            _pressAnchor = codeAnchor;
            _pressSide = codeSide;
            _pressModifiers = e.KeyModifiers;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Gutter / non-code click: whole-line selection (staging / comments).
        ClearTextSelection();
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

        if (_draggingHScroll)
        {
            ScrollFromHScrollX(e.GetPosition(this).X);
            e.Handled = true;
            return;
        }

        if (_pendingCodePress || _draggingText)
        {
            var dragPos = e.GetPosition(this);
            var dx = dragPos.X - _pressOrigin.X;
            var dy = dragPos.Y - _pressOrigin.Y;
            if (!_draggingText
                && (dx * dx + dy * dy) >= TextDragThresholdPx * TextDragThresholdPx)
            {
                _draggingText = true;
                _pendingCodePress = false;
                BeginOrUpdateTextSelection(_pressAnchor, _pressSide);
            }

            if (_draggingText)
            {
                if (TryHitTestCode(dragPos, out var focus, out var dragSide))
                    BeginOrUpdateTextSelection(focus, dragSide);
                else
                {
                    // Outside code column horizontally but still over a row: update row, clamp col.
                    var dragIndex = RowIndexAtContentY(dragPos.Y + _scrollY);
                    if (Rows is not null && dragIndex >= 0 && dragIndex < Rows.Count)
                    {
                        var text = GetCodeDisplayTextOrEmpty(dragIndex, _textSelSide);
                        var col = dragPos.X < _pressOrigin.X ? 0 : text.Length;
                        BeginOrUpdateTextSelection(
                            new DiffViewerTextSelection.Anchor(dragIndex, col),
                            _textSelSide);
                    }
                }

                e.Handled = true;
                return;
            }
        }

        if (!CanAddLineComments || Rows is null)
            return;

        var pos = e.GetPosition(this);
        if (pos.X < MinimapWidth || IsInHorizontalScrollBar(pos))
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
        var wasDragging = _draggingMinimap || _draggingHScroll || _draggingText;
        var pendingCode = _pendingCodePress;

        if (_draggingText)
        {
            _draggingText = false;
            _pendingCodePress = false;
            if (e.Pointer.Captured == this)
                ReleaseOwnedPointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (pendingCode)
        {
            _pendingCodePress = false;
            if (e.Pointer.Captured == this)
                ReleaseOwnedPointerCapture(e.Pointer);

            // Click without drag → whole-line selection (staging / comments).
            var index = _pressAnchor.Row;
            if (Rows is not null && index >= 0 && index < Rows.Count)
            {
                ClearTextSelection();
                if (_pressModifiers.HasFlag(KeyModifiers.Shift) && _selectionStart >= 0)
                    _selectionEnd = index;
                else
                    _selectionStart = _selectionEnd = index;

                if (SelectedHunkIndex is { } hunk && GetWorkingCopy() is { } workingCopy)
                    workingCopy.SelectedHunkIndex = hunk;

                InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        if (e.Pointer.Captured == this || wasDragging)
            ReleaseOwnedPointerCapture(e.Pointer);
        if (wasDragging)
            e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _draggingMinimap = false;
        _draggingHScroll = false;
        _draggingText = false;
        _pendingCodePress = false;
        base.OnPointerCaptureLost(e);
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
            _ = CopySelectionAsync();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            ScrollHorizontally(e.Key == Key.Left ? -HScrollStep : HScrollStep);
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
            Key.PageDown => Math.Min(rows.Count - 1, idx + (int)(ViewportHeight / RowHeight)),
            Key.PageUp => Math.Max(0, idx - (int)(ViewportHeight / RowHeight)),
            Key.Home => 0,
            Key.End => rows.Count - 1,
            _ => idx,
        };
        _selectionStart = _selectionEnd = idx;
        if (_textSelActive)
        {
            _textSelActive = false;
            _textAnchor = default;
            _textFocus = default;
        }
        EnsureVisible(idx);
        InvalidateVisual();
    }

    /// <summary>Scrolls so the row for <paramref name="side"/>/<paramref name="line"/> is visible.</summary>
    public void ScrollToLine(DiffSide side, int line)
    {
        if (!TryGetRowIndex(side, line, out var idx))
            return;

        EnsureVisible(idx);
        InvalidateVisual();
    }

    private void EnsureVisible(int index)
    {
        var y = RowContentTop(index);
        var next = _targetScrollY;
        var viewportHeight = ViewportHeight;
        if (y < _targetScrollY) next = y;
        else if (y + RowHeight > _targetScrollY + viewportHeight)
            next = y + RowHeight - viewportHeight;
        if (Math.Abs(next - _targetScrollY) > 0.01 || Math.Abs(next - _scrollY) > 0.01)
            SnapScrollY(next, noteActivity: false);
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
