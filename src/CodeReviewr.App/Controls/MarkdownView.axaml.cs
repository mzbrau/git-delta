using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;
using AvInline = Avalonia.Controls.Documents.Inline;
using AvSpan = Avalonia.Controls.Documents.Span;

namespace CodeReviewr.App.Controls;

/// <summary>Minimal Markdig renderer for PR descriptions and comment bodies.</summary>
public sealed class MarkdownView : UserControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private readonly StackPanel _root = new() { Spacing = 6 };

    public MarkdownView()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        if (string.IsNullOrWhiteSpace(Markdown))
            return;

        var document = Markdig.Markdown.Parse(Markdown, Pipeline);
        foreach (var block in document)
            RenderBlock(block);
    }

    private void RenderBlock(Block block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                _root.Children.Add(CreateInlines(paragraph.Inline, TextWrapping.Wrap));
                break;
            case HeadingBlock heading:
                _root.Children.Add(CreateInlines(heading.Inline, TextWrapping.Wrap, fontSize: 16 - heading.Level));
                break;
            case ListBlock list:
                RenderList(list);
                break;
            case QuoteBlock quote:
                RenderQuote(quote);
                break;
            case FencedCodeBlock code:
                _root.Children.Add(new Border
                {
                    Background = Brush("ForgeSurfaceContainerHighBrush", Brushes.DimGray),
                    Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock
                    {
                        Text = code.Lines.ToString(),
                        FontFamily = new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
                        TextWrapping = TextWrapping.NoWrap,
                    },
                });
                break;
            case ThematicBreakBlock:
                _root.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4),
                    Background = Brush("ForgeOutlineVariantBrush", Brushes.Gray),
                });
                break;
            default:
                if (block is LeafBlock { Inline: not null } leaf)
                    _root.Children.Add(CreateInlines(leaf.Inline, TextWrapping.Wrap));
                break;
        }
    }

    private void RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 0, 0) };
        var index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
                continue;

            var prefix = list.IsOrdered ? $"{index++}. " : "• ";
            var itemPanel = new StackPanel { Spacing = 2 };
            itemPanel.Children.Add(new TextBlock
            {
                Text = prefix,
                FontWeight = FontWeight.SemiBold,
            });

            foreach (var child in listItem)
                RenderListItemBlock(itemPanel, child);

            panel.Children.Add(itemPanel);
        }

        _root.Children.Add(panel);
    }

    private void RenderListItemBlock(Panel panel, Block block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                panel.Children.Add(CreateInlines(paragraph.Inline, TextWrapping.Wrap));
                break;
            case FencedCodeBlock code:
                panel.Children.Add(new TextBlock
                {
                    Text = code.Lines.ToString(),
                    FontFamily = new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
                });
                break;
        }
    }

    private void RenderQuote(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var block in quote)
            if (block is ParagraphBlock paragraph)
                inner.Children.Add(CreateInlines(paragraph.Inline, TextWrapping.Wrap));

        _root.Children.Add(new Border
        {
            BorderBrush = Brush("ForgePrimaryBrush", Brushes.SteelBlue),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = inner,
        });
    }

    private TextBlock CreateInlines(MdInline? inline, TextWrapping wrapping, double fontSize = 13)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = wrapping,
            FontSize = fontSize,
            Foreground = Brush("ForgeOnSurfaceBrush", Brushes.White),
        };

        if (inline is null)
            return textBlock;

        var inlines = new ObservableCollection<AvInline>();
        AppendInlines(inline, inlines);
        textBlock.Inlines!.Clear();
        foreach (var avInline in inlines)
            textBlock.Inlines.Add(avInline);
        return textBlock;
    }

    private static void AppendInlines(MdInline node, ICollection<AvInline> target)
    {
        for (var current = node; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case EmphasisInline emphasis:
                {
                    var span = new AvSpan();
                    if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeight.Bold;
                    else
                        span.FontStyle = FontStyle.Italic;
                    AppendInlines(emphasis.FirstChild!, span.Inlines!);
                    target.Add(span);
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        Background = Brush("ForgeSurfaceContainerHighBrush", Brushes.DimGray),
                        FontFamily = new FontFamily("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono"),
                    });
                    break;
                case LinkInline link when !string.IsNullOrEmpty(link.Url):
                {
                    var run = new Run(link.FirstChild is LiteralInline lit ? lit.Content.ToString() : link.Url)
                    {
                        Foreground = Brush("ForgePrimaryBrush", Brushes.SteelBlue),
                        TextDecorations = TextDecorations.Underline,
                    };
                    // TODO: authenticated fetch for private GitHub assets (images, attachments).
                    if (link.IsImage && link.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        target.Add(new Run($"[image: {link.Url}]")
                        {
                            Foreground = Brush("ForgeOnSurfaceVariantBrush", Brushes.Gray),
                        });
                    }
                    else
                    {
                        target.Add(run);
                    }

                    break;
                }
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline container:
                    AppendInlines(container.FirstChild!, target);
                    break;
            }
        }
    }

    private static IBrush Brush(string key, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, null, out var res) == true && res is IBrush brush)
            return brush;
        return fallback;
    }
}
