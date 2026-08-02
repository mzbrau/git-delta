using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CodeReviewr.Core;
using CodeReviewr.Core.Diff;
using Markdig.Syntax;

namespace CodeReviewr.App.Controls;

/// <summary>
/// Renders markdown file content with a source-line gutter and optional PR comment lane.
/// Each top-level Markdig block is one row anchored to its source line range.
/// </summary>
public sealed class MarkdownFilePreview : UserControl
{
    private const double GutterWidth = 36;
    private const double CommentLaneWidth = 22;

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, string?>(nameof(Markdown));

    public static readonly StyledProperty<string> EmptyMessageProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, string>(
            nameof(EmptyMessage),
            "No markdown content");

    public static readonly StyledProperty<bool> CanAddLineCommentsProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, bool>(nameof(CanAddLineComments));

    public static readonly StyledProperty<ICommand?> AddLineCommentCommandProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, ICommand?>(nameof(AddLineCommentCommand));

    public static readonly StyledProperty<IReadOnlyList<IDiffAnnotation>?> AnnotationsProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, IReadOnlyList<IDiffAnnotation>?>(nameof(Annotations));

    public static readonly StyledProperty<IDiffAnnotation?> SelectedAnnotationProperty =
        AvaloniaProperty.Register<MarkdownFilePreview, IDiffAnnotation?>(
            nameof(SelectedAnnotation),
            defaultBindingMode: BindingMode.TwoWay);

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _rowsPanel;
    private readonly TextBlock _empty;
    private readonly List<BlockRow> _rows = [];
    private INotifyCollectionChanged? _annotationsNotify;
    private int _hoverRowIndex = -1;

    private sealed class BlockRow
    {
        public required int StartLine { get; init; }
        public required int EndLine { get; init; }
        public required Border Root { get; init; }
        public required Panel CommentLane { get; init; }
        public required Button AddCommentButton { get; init; }
        public required Panel AnnotationHost { get; init; }
    }

    public MarkdownFilePreview()
    {
        _rowsPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 8, 12, 16),
        };
        _empty = new TextBlock
        {
            Text = EmptyMessage,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        var host = new Panel();
        host.Children.Add(_empty);
        host.Children.Add(_rowsPanel);
        _scroll = new ScrollViewer
        {
            Content = host,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Content = _scroll;
        ClipToBounds = true;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string EmptyMessage
    {
        get => GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
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

    static MarkdownFilePreview()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownFilePreview>((v, _) => v.Rebuild());
        EmptyMessageProperty.Changed.AddClassHandler<MarkdownFilePreview>((v, e) =>
        {
            if (e.NewValue is string text)
                v._empty.Text = text;
        });
        CanAddLineCommentsProperty.Changed.AddClassHandler<MarkdownFilePreview>((v, _) => v.RefreshCommentUi());
        AnnotationsProperty.Changed.AddClassHandler<MarkdownFilePreview>((v, e) =>
        {
            v.DetachAnnotationsNotify();
            v.AttachAnnotationsNotify(e.NewValue as INotifyCollectionChanged);
            v.RefreshAnnotations();
        });
        SelectedAnnotationProperty.Changed.AddClassHandler<MarkdownFilePreview>((v, _) => v.RefreshAnnotations());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachAnnotationsNotify();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Returns a control-relative anchor rect below the block containing <paramref name="line"/>
    /// on the new side. Old-side requests always fail.
    /// </summary>
    public bool TryGetLineAnchorRect(DiffSide side, int line, out Rect rect)
    {
        rect = default;
        if (side != DiffSide.New || _rows.Count == 0)
            return false;

        foreach (var row in _rows)
        {
            if (line < row.StartLine || line > row.EndLine)
                continue;

            var local = row.Root.TranslatePoint(
                new Point(GutterWidth + CommentLaneWidth, row.Root.Bounds.Height + 2),
                this);
            if (local is null)
                return false;

            var width = Math.Max(240, Bounds.Width - local.Value.X - 16);
            rect = new Rect(local.Value.X, local.Value.Y, width, 0);
            return true;
        }

        return false;
    }

    private void Rebuild()
    {
        _rowsPanel.Children.Clear();
        _rows.Clear();
        _hoverRowIndex = -1;

        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            _empty.Text = EmptyMessage;
            _empty.IsVisible = true;
            _rowsPanel.IsVisible = false;
            return;
        }

        _empty.IsVisible = false;
        _rowsPanel.IsVisible = true;

        var document = Markdig.Markdown.Parse(markdown, MarkdownRenderer.Pipeline);
        var index = 0;
        foreach (Block block in document)
        {
            var (startLine, endLine) = MarkdownBlockLines.GetRange(block, markdown);
            var row = BuildRow(block, startLine, endLine, index);
            _rows.Add(row);
            _rowsPanel.Children.Add(row.Root);
            index++;
        }

        if (_rows.Count == 0)
        {
            _empty.Text = EmptyMessage;
            _empty.IsVisible = true;
            _rowsPanel.IsVisible = false;
        }

        RefreshCommentUi();
        RefreshAnnotations();
    }

    private BlockRow BuildRow(Block block, int startLine, int endLine, int index)
    {
        var gutterBrush = ThemeBrush("ForgeDiffGutterTextBrush", Brushes.Gray);
        var gutter = new TextBlock
        {
            Text = startLine == endLine ? startLine.ToString() : $"{startLine}",
            Width = GutterWidth,
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 4, 6, 0),
            Foreground = gutterBrush,
            FontFamily = new FontFamily(
                "avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
        };
        if (startLine != endLine)
            ToolTip.SetTip(gutter, $"L{startLine}–L{endLine}");

        var annotationHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var addButton = new Button
        {
            Content = "+",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Tag = index,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        addButton.Classes.Add("Ghost");
        addButton.Click += OnAddCommentClick;

        var commentLane = new Panel
        {
            Width = CommentLaneWidth,
            Margin = new Thickness(0, 0, 4, 0),
        };
        commentLane.Children.Add(annotationHost);
        commentLane.Children.Add(addButton);
        addButton.HorizontalAlignment = HorizontalAlignment.Center;
        addButton.VerticalAlignment = VerticalAlignment.Top;

        var content = MarkdownRenderer.CreateBlockControl(block);
        content.HorizontalAlignment = HorizontalAlignment.Stretch;

        var rowGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{GutterWidth},{CommentLaneWidth},*"),
        };
        Grid.SetColumn(gutter, 0);
        Grid.SetColumn(commentLane, 1);
        Grid.SetColumn(content, 2);
        rowGrid.Children.Add(gutter);
        rowGrid.Children.Add(commentLane);
        rowGrid.Children.Add(content);

        var root = new Border
        {
            Child = rowGrid,
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Tag = index,
        };
        root.PointerEntered += OnRowPointerEntered;
        root.PointerExited += OnRowPointerExited;

        return new BlockRow
        {
            StartLine = startLine,
            EndLine = endLine,
            Root = root,
            CommentLane = commentLane,
            AddCommentButton = addButton,
            AnnotationHost = annotationHost,
        };
    }

    private void OnRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border { Tag: int index })
            return;
        _hoverRowIndex = index;
        RefreshCommentUi();
    }

    private void OnRowPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Border { Tag: int index })
            return;
        if (_hoverRowIndex == index)
            _hoverRowIndex = -1;
        RefreshCommentUi();
    }

    private void OnAddCommentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index } || index < 0 || index >= _rows.Count)
            return;
        if (!CanAddLineComments || AddLineCommentCommand is null)
            return;

        var row = _rows[index];
        var startLine = row.StartLine == row.EndLine ? (int?)null : row.StartLine;
        var request = new LineCommentRequest(DiffSide.New, row.EndLine, startLine);
        if (AddLineCommentCommand.CanExecute(request))
            AddLineCommentCommand.Execute(request);
    }

    private void RefreshCommentUi()
    {
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].AddCommentButton.IsVisible = CanAddLineComments && _hoverRowIndex == i;
    }

    private void RefreshAnnotations()
    {
        var annotations = Annotations;
        var selected = SelectedAnnotation;
        var markerBrush = ThemeBrush("ForgePrimaryBrush", Brushes.SteelBlue);
        var selectedBrush = ThemeBrush("ForgeSecondaryBrush", Brushes.Orange);
        var outdatedBrush = ThemeBrush("ForgeOnSurfaceVariantBrush", Brushes.Gray);

        foreach (var row in _rows)
        {
            row.AnnotationHost.Children.Clear();
            if (annotations is null)
                continue;

            foreach (var annotation in annotations)
            {
                var range = annotation.Range;
                if (range.End.Side != DiffSide.New)
                    continue;
                var line = range.End.Line;
                if (line < row.StartLine || line > row.EndLine)
                    continue;

                var isOutdated = annotation is ReviewThreadAnnotation { IsOutdated: true };
                var fill = selected == annotation
                    ? selectedBrush
                    : isOutdated ? outdatedBrush : markerBrush;

                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = fill,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Tag = annotation,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                ToolTip.SetTip(dot, $"Comment on L{line}");
                dot.PointerPressed += OnAnnotationPressed;
                row.AnnotationHost.Children.Add(dot);
            }
        }
    }

    private void OnAnnotationPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: IDiffAnnotation annotation })
            return;
        SelectedAnnotation = annotation;
        e.Handled = true;
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
        RefreshAnnotations();

    private static IBrush ThemeBrush(string key, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, null, out var res) == true && res is IBrush brush)
            return brush;
        return fallback;
    }
}
