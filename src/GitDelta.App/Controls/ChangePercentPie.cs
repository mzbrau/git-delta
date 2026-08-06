using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitDelta.App.Controls;

/// <summary>Compact pie/donut showing what fraction of a file changed.</summary>
public sealed class ChangePercentPie : Control
{
    public static readonly StyledProperty<int?> PercentProperty =
        AvaloniaProperty.Register<ChangePercentPie, int?>(nameof(Percent));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChangePercentPie, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<ChangePercentPie, IBrush?>(nameof(Track));

    static ChangePercentPie()
    {
        AffectsRender<ChangePercentPie>(PercentProperty, FillProperty, TrackProperty);
        WidthProperty.OverrideDefaultValue<ChangePercentPie>(12);
        HeightProperty.OverrideDefaultValue<ChangePercentPie>(12);
    }

    public int? Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var percent = Percent;
        if (percent is null)
            return;

        var bounds = new Rect(Bounds.Size);
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0)
            return;

        var rect = new Rect(
            (bounds.Width - size) / 2,
            (bounds.Height - size) / 2,
            size,
            size);

        var track = Track ?? new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        var fill = Fill ?? Brushes.DodgerBlue;

        context.DrawEllipse(track, null, rect.Center, size / 2, size / 2);

        var clamped = Math.Clamp(percent.Value, 0, 100);
        if (clamped <= 0)
            return;

        if (clamped >= 100)
        {
            context.DrawEllipse(fill, null, rect.Center, size / 2, size / 2);
            // Donut hole
            var hole = size * 0.28;
            context.DrawEllipse(track, null, rect.Center, hole, hole);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var center = rect.Center;
            var radius = size / 2;
            var start = -Math.PI / 2;
            var sweep = 2 * Math.PI * (clamped / 100.0);
            var end = start + sweep;

            ctx.BeginFigure(center, isFilled: true);
            ctx.LineTo(Polar(center, radius, start));
            const int segments = 24;
            for (var i = 1; i <= segments; i++)
            {
                var t = start + sweep * i / segments;
                ctx.LineTo(Polar(center, radius, t));
            }

            ctx.LineTo(center);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(fill, null, geometry);

        var inner = size * 0.28;
        // Punch a hole so it reads as a donut against the control background.
        if (Avalonia.Application.Current?.TryGetResource(
                "ForgeBackgroundBrush",
                Avalonia.Application.Current.ActualThemeVariant,
                out var res) == true &&
            res is IBrush bg)
        {
            context.DrawEllipse(bg, null, rect.Center, inner, inner);
        }
        else
        {
            context.DrawEllipse(Brushes.Transparent, null, rect.Center, inner, inner);
        }
    }

    private static Point Polar(Point center, double radius, double radians) =>
        new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
}
