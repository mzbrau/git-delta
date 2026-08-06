using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitDelta.Core.Diagnostics;

namespace GitDelta.App.Controls;

/// <summary>
/// Text block that collapses the middle of long strings (prefix…suffix) when width is limited.
/// </summary>
public sealed class MiddleEllipsisTextBlock : Control
{
    // Rare slow-measure span only; avoid flooding meters during 600-row remasures.
    private const double SlowMeasureMs = 8;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MiddleEllipsisTextBlock, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<MiddleEllipsisTextBlock>();

    private string _display = "";

    static MiddleEllipsisTextBlock()
    {
        AffectsRender<MiddleEllipsisTextBlock>(
            TextProperty, ForegroundProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty);
        AffectsMeasure<MiddleEllipsisTextBlock>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty ||
            change.Property == FontFamilyProperty ||
            change.Property == FontSizeProperty ||
            change.Property == FontWeightProperty ||
            change.Property == BoundsProperty)
        {
            UpdateDisplay(Bounds.Width);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var text = Text ?? "";
            if (string.IsNullOrEmpty(text))
                return new Size(0, FontSize * 1.2);

            var full = MeasureWidth(text);
            var height = FontSize * 1.35;
            if (double.IsInfinity(availableSize.Width) || availableSize.Width >= full)
            {
                _display = text;
                return new Size(full, height);
            }

            UpdateDisplay(availableSize.Width);
            return new Size(Math.Min(availableSize.Width, full), height);
        }
        finally
        {
            var elapsed = sw.Elapsed.TotalMilliseconds;
            if (elapsed >= SlowMeasureMs)
            {
                using var activity = GitDeltaActivity.Source.StartActivity("ui.ellipsis.slow");
                activity?.SetTag("ellipsis.text_length", (Text ?? "").Length);
                activity?.SetTag("ellipsis.measure_ms", elapsed);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateDisplay(finalSize.Width);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (string.IsNullOrEmpty(_display))
            return;

        var foreground = Foreground ?? Brushes.Black;
        var ft = CreateFormatted(_display, foreground);
        var y = Math.Max(0, (Bounds.Height - ft.Height) / 2);
        context.DrawText(ft, new Point(0, y));
    }

    private void UpdateDisplay(double width)
    {
        var text = Text ?? "";
        if (string.IsNullOrEmpty(text))
        {
            _display = "";
            return;
        }

        if (double.IsNaN(width) || width <= 0 || double.IsInfinity(width))
        {
            _display = text;
            return;
        }

        if (MeasureWidth(text) <= width)
        {
            _display = text;
            return;
        }

        const string ellipsis = "…";
        var ellipsisWidth = MeasureWidth(ellipsis);
        if (ellipsisWidth >= width)
        {
            _display = ellipsis;
            return;
        }

        var budget = width - ellipsisWidth;
        var left = 0;
        var right = 0;
        // Grow prefix and suffix evenly until the next character would overflow.
        while (left + right < text.Length)
        {
            var takeLeft = left < text.Length - right;
            if (takeLeft)
            {
                var next = MeasureWidth(text[..(left + 1)] + text[^(right)..]);
                if (next > budget)
                    break;
                left++;
            }

            if (left + right >= text.Length)
                break;

            var nextRight = MeasureWidth(text[..left] + text[^(right + 1)..]);
            if (nextRight > budget)
                break;
            right++;
        }

        if (left == 0 && right == 0)
        {
            _display = ellipsis;
            return;
        }

        _display = text[..left] + ellipsis + text[^right..];
        InvalidateVisual();
    }

    private double MeasureWidth(string text) => CreateFormatted(text, Brushes.Black).Width;

    private FormattedText CreateFormatted(string text, IBrush foreground) =>
        new(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize,
            foreground);
}
