using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using LiveMarkdown.Avalonia;

namespace GitDelta.App.Controls;

/// <summary>
/// Markdown body viewer backed by LiveMarkdown.Avalonia. Keeps a simple <see cref="Markdown"/>
/// string API for XAML bindings.
/// </summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private readonly MarkdownRenderer _renderer = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private ObservableStringBuilder? _builder;

    public MarkdownView()
    {
        Content = _renderer;
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
        var markdown = Markdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            _builder?.Clear();
            _renderer.MarkdownBuilder = null;
            _builder = null;
            return;
        }

        // LiveMarkdown is Append/Clear oriented; rebuild the builder for full-string rebinds.
        var builder = new ObservableStringBuilder();
        builder.Append(markdown);
        _builder = builder;
        _renderer.MarkdownBuilder = builder;
    }
}
