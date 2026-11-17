using Avalonia;
using Avalonia.Media.Imaging;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Services;
using ZLogger;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// A decoded image preview. The decode runs off the UI thread and is gated behind an injectable decoder (default the
/// real Avalonia decoder) so a decoder-less host can skip it; the long edge is bound to <see cref="MaxLongEdge"/> pixels — the
/// default decoder decodes straight to that bound, and a decoder that returns a larger bitmap is scaled down with
/// <c>CreateScaledBitmap</c> and its big bitmap disposed. A decode failure is a notice, never an exception.
/// </summary>
internal sealed class ImageFilePreviewViewModel : FilePreviewViewModel
{
    /// <summary>Longest edge of the decoded image in pixels; larger images are scaled down.</summary>
    internal const int MaxLongEdge = 4096;

    private Bitmap? image;
    private ChatPicture? picture;

    private ImageFilePreviewViewModel(string path, string name, long byteSize, Bitmap? image, int pixelWidth, int pixelHeight)
        : base(path, name, byteSize)
    {
        this.image = image;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    /// <summary>The decoded (and possibly scaled) bitmap; null when decoding failed or the decoder was stubbed.</summary>
    public Bitmap? Image => image;

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    /// <summary>The vector picture of an <c>.svg</c> file; null for a bitmap. Replayed at the zoomed scale, never rasterized.</summary>
    public MermaidPicture? Vector => picture?.Vector;

    public bool HasImage => image is not null || picture is not null;

    public override string Caption =>
        PreviewStrings.ImageCaptionText(Name, PixelWidth, PixelHeight, SizeText(ByteSize)).Resolve();

    protected override void ReleaseCore()
    {
        image?.Dispose();
        image = null;
        picture?.Release();
        picture = null;
    }

    public static ImageFilePreviewViewModel Build(string path, long byteSize, Func<byte[], Bitmap?>? decoder, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (FilePreviewClassifier.IsSvgExtension(path))
            {
                return BuildSvg(path, name, byteSize);
            }

            var data = File.ReadAllBytes(path);
            ct.ThrowIfCancellationRequested();
            return Build(data, name, byteSize, decoder, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed(path, name, byteSize, PreviewStrings.ImageReadFailed(name, exception.Message));
        }
    }

    /// <summary>
    /// The same decode path for bytes that did not come from a file: the old side of an image diff is a git blob, and the base
    /// version of a file exists nowhere on disk. The caller decides the name (and whether the bytes are within the preview
    /// limit) and gets the same bounded decode, scaling and failure notice as the file overload.
    /// </summary>
    public static ImageFilePreviewViewModel Build(byte[] data, string name, long byteSize, Func<byte[], Bitmap?>? decoder, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Bitmap? decoded;
        try
        {
            decoded = (decoder ?? DefaultDecoder)(data);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.File.ZLogWarning($"image decode failed: {name}: {exception.Message}");
            return Failed(path ?? name, name, byteSize, PreviewStrings.ImageDecodeFailed(name));
        }

        if (decoded is null)
        {
            return Failed(path ?? name, name, byteSize, PreviewStrings.ImageDecodeFailed(name));
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

        return new ImageFilePreviewViewModel(path ?? name, name, byteSize, decoded, decoded.PixelSize.Width, decoded.PixelSize.Height);
    }

    /// <summary>An SVG file as a vector picture, through the same self-contained check the transcript's SVGs get.</summary>
    private static ImageFilePreviewViewModel BuildSvg(string path, string name, long byteSize)
    {
        var loaded = ChatPictures.LoadSvg(File.ReadAllText(path), name, path);
        if (loaded is null)
        {
            return Failed(path, name, byteSize, PreviewStrings.ImageDecodeFailed(name));
        }

        var size = loaded.Size;
        return new ImageFilePreviewViewModel(path, name, byteSize, null, (int)size.Width, (int)size.Height) { picture = loaded };
    }

    private static ImageFilePreviewViewModel Failed(string path, string name, long byteSize, LocText notice)
    {
        var viewModel = new ImageFilePreviewViewModel(path, name, byteSize, null, 0, 0);
        viewModel.SetNotice(notice);
        return viewModel;
    }

    /// <summary>
    /// The production decoder: Avalonia's own bitmap decoding, no image library, bounded to <see cref="MaxLongEdge"/> while
    /// decoding (the header says how large the image is), so a huge image never exists as a full-size bitmap. Shared by the
    /// notebook preview's embedded pictures.
    /// </summary>
    internal static Bitmap? DefaultDecoder(byte[] data) =>
        ImageHeader.DecodeBounded(data, MaxLongEdge, BitmapInterpolationMode.HighQuality);
}
