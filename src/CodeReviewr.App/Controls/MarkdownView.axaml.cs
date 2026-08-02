using Avalonia;
using Avalonia.Controls;

namespace CodeReviewr.App.Controls;

/// <summary>Minimal Markdig renderer for PR descriptions and comment bodies.</summary>
public sealed class MarkdownView : UserControl
{
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

        var document = Markdig.Markdown.Parse(Markdown, MarkdownRenderer.Pipeline);
        foreach (var block in document)
            _root.Children.Add(MarkdownRenderer.CreateBlockControl(block));
    }
}
