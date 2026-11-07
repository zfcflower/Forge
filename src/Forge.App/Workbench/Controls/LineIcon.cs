using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// A line (outline) icon: strokes a geometry drawn on a 24x24 grid, scaled to the control size, with a stroke of fixed
/// on-screen width so every icon reads with the same line weight as the text next to it. The colour follows the inherited
/// <see cref="TextElement.ForegroundProperty"/>, like a <see cref="PathIcon"/>; <see cref="Fill"/> optionally fills it.
/// </summary>
public sealed class LineIcon : Control
{
    public const double Grid = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LineIcon, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.25);

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, FillProperty, StrokeThicknessProperty);
    }

    public LineIcon()
    {
        Width = 14;
        Height = 14;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The line width in device-independent pixels, independent of the icon size.</summary>
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data)
        {
            return;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var scale = size / Grid;
        var offset = new Vector((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = new Pen(Foreground, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            context.DrawGeometry(Fill, pen, data);
        }
    }
}
