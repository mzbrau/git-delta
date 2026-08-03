using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdInline = Markdig.Syntax.Inlines.Inline;
using AvInline = Avalonia.Controls.Documents.Inline;
using AvSpan = Avalonia.Controls.Documents.Span;

namespace CodeReviewr.App.Controls;

/// <summary>Shared Markdig → Avalonia control rendering for PR bodies and file preview.</summary>
internal static class MarkdownRenderer
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static Control CreateBlockControl(Block block) =>
        block switch
        {
            ParagraphBlock paragraph => CreateInlines(paragraph.Inline, TextWrapping.Wrap),
            HeadingBlock heading => CreateInlines(heading.Inline, TextWrapping.Wrap, fontSize: 16 - heading.Level),
            ListBlock list => CreateList(list),
            QuoteBlock quote => CreateQuote(quote),
            FencedCodeBlock code => CreateCode(code),
            ThematicBreakBlock => new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4),
                Background = Brush("ForgeOutlineVariantBrush", Brushes.Gray),
            },
            LeafBlock { Inline: not null } leaf => CreateInlines(leaf.Inline, TextWrapping.Wrap),
            _ => new SelectableTextBlock(),
        };

    private static Control CreateList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 0, 0) };
        var index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
                continue;

            var prefix = list.IsOrdered ? $"{index++}. " : "• ";
            var marker = new SelectableTextBlock
            {
                Text = prefix,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var content = new StackPanel { Spacing = 2 };
            foreach (var child in listItem)
                AddListItemBlock(content, child);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static void AddListItemBlock(Panel panel, Block block)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
                panel.Children.Add(CreateInlines(paragraph.Inline, TextWrapping.Wrap));
                break;
            case FencedCodeBlock code:
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = code.Lines.ToString(),
                    FontFamily = MonoFont(),
                });
                break;
        }
    }

    private static Control CreateQuote(QuoteBlock quote)
    {
        var inner = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var block in quote)
            if (block is ParagraphBlock paragraph)
                inner.Children.Add(CreateInlines(paragraph.Inline, TextWrapping.Wrap));

        return new Border
        {
            BorderBrush = Brush("ForgePrimaryBrush", Brushes.SteelBlue),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = inner,
        };
    }

    private static Control CreateCode(FencedCodeBlock code) =>
        new Border
        {
            Background = Brush("ForgeSurfaceContainerHighBrush", Brushes.DimGray),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Child = new SelectableTextBlock
            {
                Text = code.Lines.ToString(),
                FontFamily = MonoFont(),
                TextWrapping = TextWrapping.NoWrap,
            },
        };

    private static SelectableTextBlock CreateInlines(MdInline? inline, TextWrapping wrapping, double fontSize = 13)
    {
        var textBlock = new SelectableTextBlock
        {
            TextWrapping = wrapping,
            FontSize = fontSize,
            LineHeight = fontSize * 1.45,
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
                        FontFamily = MonoFont(),
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

    private static FontFamily MonoFont() =>
        new("avares://CodeReviewr.App/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono");

    private static IBrush Brush(string key, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, null, out var res) == true && res is IBrush brush)
            return brush;
        return fallback;
    }
}
