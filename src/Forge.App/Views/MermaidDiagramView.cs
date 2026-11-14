using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Forge.App.Services;
using SkiaSharp;

namespace Forge.App.Views;

/// <summary>
/// Draws one rendered Mermaid diagram (wave W11). The picture is vector: a custom draw operation replays it on the render
/// thread through Avalonia's own Skia canvas, so a 2x or 3x display, a window resize or a zoom stays crisp without rendering
/// again, and no bitmap is allocated. The control scales the diagram down to the width it is given, never up.
/// </summary>
internal sealed class MermaidDiagramView : Control
{
    private MermaidPicture? picture;
    private Size natural;

    internal MermaidDiagramView()
    {
        // Left/Top instead of the default stretch: the control is arranged at exactly the width it measured (the diagram's
        // own, or the width it was given), so the drawing never scales past 1:1 inside a wider panel.
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }

    /// <summary>The diagram shown, or null while nothing rendered yet.</summary>
    internal MermaidPicture? Picture => picture;

    internal void SetPicture(MermaidPicture? next)
    {
        if (ReferenceEquals(picture, next))
        {
            return;
        }

        picture = next;
        natural = next is null ? default : new Size(next.Size.Width, next.Size.Height);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (picture is null || !(natural.Width > 0) || !(natural.Height > 0))
        {
            return default;
        }

        var width = natural.Width;
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0 && availableSize.Width < width)
        {
            width = availableSize.Width;
        }

        return new Size(width, natural.Height * (width / natural.Width));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (picture is null || !(Bounds.Width > 0) || !(Bounds.Height > 0) || !(natural.Width > 0))
        {
            return;
        }

        context.Custom(new DiagramOperation(new Rect(Bounds.Size), picture, Bounds.Width / natural.Width));
    }

    /// <summary>One frame of the picture: a scale-to-fit transform and the Skia replay, nothing allocated besides the operation.</summary>
    private sealed class DiagramOperation : ICustomDrawOperation
    {
        private readonly MermaidPicture picture;
        private readonly double scale;

        internal DiagramOperation(Rect bounds, MermaidPicture picture, double scale)
        {
            Bounds = bounds;
            this.picture = picture;
            this.scale = scale;
        }

        public Rect Bounds { get; }

        /// <summary>The diagram is not an input target; text selection and scrolling stay with the surrounding view.</summary>
        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is DiagramOperation operation &&
            operation.Bounds == Bounds &&
            ReferenceEquals(operation.picture, picture) &&
            operation.scale.Equals(scale);

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var feature))
            {
                // A backend without Skia cannot replay the picture; the block's progress state already covers "no diagram".
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var cull = picture.Picture.CullRect;
            var opacity = Math.Clamp(lease.CurrentOpacity, 0, 1);

            var restore = canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            if (opacity < 0.999)
            {
                using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)) };
                canvas.SaveLayer(layer);
            }

            canvas.Scale((float)scale);
            canvas.Translate(-cull.Left, -cull.Top);
            picture.Picture.Playback(canvas);
            canvas.RestoreToCount(restore);
        }
    }
}
