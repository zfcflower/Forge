using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Forge.App.Services;
using SkiaSharp;

namespace Forge.App.Views;

/// <summary>
/// Shows one picture (bitmap or vector) that can be zoomed with the wheel or the buttons and dragged with the pointer. A new
/// picture starts "fit": as large as the view allows without upscaling past 4x, centred. Once zoomed the picture is kept inside
/// the view (it can be dragged only while it is larger than the view; a smaller one stays centred). Vector pictures are replayed
/// at the zoomed scale, so a zoomed diagram stays crisp.
/// </summary>
internal sealed class ZoomPanView : Control
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 32;

    private Bitmap? bitmap;
    private MermaidPicture? vector;
    private Size natural;
    private double scale = 1;
    private Point offset;
    private bool fitted = true;
    private bool dragging;
    private Point dragStart;
    private Point dragOrigin;

    internal ZoomPanView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>The zoom relative to the picture's natural size (1 = actual size).</summary>
    internal double Zoom => scale;

    internal event Action? ZoomChanged;

    internal void SetPicture(ChatPicture? picture)
    {
        bitmap = picture?.Bitmap;
        vector = picture?.Vector;
        natural = picture?.Size ?? default;
        Fit();
    }

    internal void Fit()
    {
        fitted = true;
        Layout();
        InvalidateVisual();
        ZoomChanged?.Invoke();
    }

    internal void Actual() => ZoomTo(1, new Point(Bounds.Width / 2, Bounds.Height / 2));

    internal void ZoomBy(double factor) => ZoomTo(scale * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    private void ZoomTo(double next, Point anchor)
    {
        if (!(natural.Width > 0))
        {
            return;
        }

        next = Math.Clamp(next, MinZoom, MaxZoom);
        // Keep the picture point under the anchor where it is.
        var picturePoint = new Point((anchor.X - offset.X) / scale, (anchor.Y - offset.Y) / scale);
        scale = next;
        offset = new Point(anchor.X - picturePoint.X * scale, anchor.Y - picturePoint.Y * scale);
        fitted = false;
        Clamp();
        InvalidateVisual();
        ZoomChanged?.Invoke();
    }

    private void Layout()
    {
        if (!(natural.Width > 0) || !(Bounds.Width > 0) || !(Bounds.Height > 0))
        {
            return;
        }

        scale = Math.Min(4, Math.Min(Bounds.Width / natural.Width, Bounds.Height / natural.Height));
        offset = new Point((Bounds.Width - natural.Width * scale) / 2, (Bounds.Height - natural.Height * scale) / 2);
    }

    private void Clamp()
    {
        var width = natural.Width * scale;
        var height = natural.Height * scale;
        offset = new Point(
            width <= Bounds.Width ? (Bounds.Width - width) / 2 : Math.Clamp(offset.X, Bounds.Width - width, 0),
            height <= Bounds.Height ? (Bounds.Height - height) / 2 : Math.Clamp(offset.Y, Bounds.Height - height, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (fitted)
        {
            Layout();
            ZoomChanged?.Invoke();
        }
        else
        {
            Clamp();
        }

        return size;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomTo(scale * Math.Pow(1.0015, e.Delta.Y * 120 * (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 2 : 1)), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        dragging = true;
        dragStart = e.GetPosition(this);
        dragOrigin = offset;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging)
        {
            return;
        }

        var point = e.GetPosition(this);
        offset = new Point(dragOrigin.X + point.X - dragStart.X, dragOrigin.Y + point.Y - dragStart.Y);
        fitted = false;
        Clamp();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging)
        {
            dragging = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnDoubleTapped(Avalonia.Input.TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (fitted)
        {
            ZoomTo(1, e.GetPosition(this));
        }
        else
        {
            Fit();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!(natural.Width > 0))
        {
            return;
        }

        var target = new Rect(offset.X, offset.Y, natural.Width * scale, natural.Height * scale);
        if (bitmap is not null)
        {
            using (context.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality,
            }))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            }
        }
        else if (vector is not null)
        {
            if (vector.TryRetain())
            {
                context.Custom(new VectorOperation(new Rect(Bounds.Size), vector, scale, offset));
            }
        }
    }

    private sealed class VectorOperation : ICustomDrawOperation
    {
        private readonly MermaidPicture picture;
        private readonly double scale;
        private readonly Point offset;

        internal VectorOperation(Rect bounds, MermaidPicture picture, double scale, Point offset)
        {
            Bounds = bounds;
            this.picture = picture;
            this.scale = scale;
            this.offset = offset;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is VectorOperation operation &&
            operation.Bounds == Bounds &&
            ReferenceEquals(operation.picture, picture) &&
            operation.scale.Equals(scale) &&
            operation.offset == offset;

        public void Dispose() => picture.Release();

        public void Render(ImmediateDrawingContext context)
        {
            if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var feature))
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var cull = picture.Picture.CullRect;
            var restore = canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            canvas.Translate((float)offset.X, (float)offset.Y);
            canvas.Scale((float)scale);
            canvas.Translate(-cull.Left, -cull.Top);
            picture.Picture.Playback(canvas);
            canvas.RestoreToCount(restore);
        }
    }
}
