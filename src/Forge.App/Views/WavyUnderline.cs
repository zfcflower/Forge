using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Views;

/// <summary>
/// A hand-drawn wavy rule, used to mark a line as the detail behind the one above it. Avalonia's <c>TextDecoration</c> can
/// only draw straight strokes (solid or dashed), so the wave is one <see cref="StreamGeometry"/> of the visible width.
/// </summary>
internal sealed class WavyUnderline : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<WavyUnderline, IBrush?>(nameof(Stroke));

    /// <summary>Half a period, in DIP: the wave crests about every 6 px.</summary>
    private const double WaveLength = 6;

    private const double Amplitude = 1.1;

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 1 || Stroke is not { } brush)
        {
            return;
        }

        var mid = (Bounds.Height - 1) / 2;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(0, mid), isFilled: false);
            for (var x = 0d; x < width; x += 0.75)
            {
                path.LineTo(new Point(x, mid + (Math.Sin(x / WaveLength * Math.PI * 2) * Amplitude)));
            }
        }

        context.DrawGeometry(null, new Pen(brush, 1), geometry);
    }
}
