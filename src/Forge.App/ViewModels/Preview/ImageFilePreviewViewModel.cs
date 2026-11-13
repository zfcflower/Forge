using Avalonia;
using Avalonia.Media.Imaging;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// A decoded image preview. The decode runs off the UI thread and is gated behind an injectable decoder (default the
/// real Avalonia decoder) so headless tests can skip it; the long edge is bound to <see cref="MaxLongEdge"/> pixels by
/// scaling with <c>CreateScaledBitmap</c> and disposing the big bitmap. A decode failure is a notice, never an exception.
/// </summary>
internal sealed class ImageFilePreviewViewModel : FilePreviewViewModel
{
    /// <summary>Longest edge of the decoded image in pixels; larger images are scaled down.</summary>
    internal const int MaxLongEdge = 4096;

    private Bitmap? image;

    private ImageFilePreviewViewModel(string path, string name, long byteSize, Bitmap? image, int pixelWidth, int pixelHeight)
        : base(path, name, byteSize)
    {
        this.image = image;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    /// <summary>The decoded (and possibly scaled) bitmap; null when decoding failed or a test stubbed the decoder.</summary>
    public Bitmap? Image => image;

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool HasImage => image is not null;

    public override string Caption =>
        PreviewStrings.ImageCaptionText(Name, PixelWidth, PixelHeight, SizeText(ByteSize)).Resolve();

    public override void Release()
    {
        image?.Dispose();
        image = null;
    }

    public static ImageFilePreviewViewModel Build(string path, long byteSize, Func<byte[], Bitmap?>? decoder, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        try
        {
            ct.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(path);
            ct.ThrowIfCancellationRequested();

            Bitmap? decoded;
            try
            {
                decoded = (decoder ?? DefaultDecoder)(data);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AppLogs.File.ZLogWarning($"image decode failed: {name}: {exception.Message}");
                return Failed(path, name, byteSize, PreviewStrings.ImageDecodeFailed(name));
            }

            if (decoded is null)
            {
                return Failed(path, name, byteSize, PreviewStrings.ImageDecodeFailed(name));
            }

            var pixelSize = decoded.PixelSize;
            var longEdge = Math.Max(pixelSize.Width, pixelSize.Height);
            if (longEdge > MaxLongEdge)
            {
                var scale = (double)MaxLongEdge / longEdge;
                var target = new PixelSize(
                    Math.Max(1, (int)(pixelSize.Width * scale)),
                    Math.Max(1, (int)(pixelSize.Height * scale)));
                var scaled = decoded.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
                decoded.Dispose();
                decoded = scaled;
            }

            return new ImageFilePreviewViewModel(path, name, byteSize, decoded, decoded.PixelSize.Width, decoded.PixelSize.Height);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed(path, name, byteSize, PreviewStrings.ImageReadFailed(name, exception.Message));
        }
    }

    private static ImageFilePreviewViewModel Failed(string path, string name, long byteSize, LocText notice)
    {
        var viewModel = new ImageFilePreviewViewModel(path, name, byteSize, null, 0, 0);
        viewModel.SetNotice(notice);
        return viewModel;
    }

    /// <summary>The production decoder: Avalonia's own bitmap decoding, no image library.</summary>
    private static Bitmap? DefaultDecoder(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        return new Bitmap(input);
    }
}
