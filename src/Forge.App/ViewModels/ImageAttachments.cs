using Avalonia;
using Avalonia.Media.Imaging;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>The limits and format checks an attached image passes before it reaches a message.</summary>
internal static class ImageAttachmentRules
{
    /// <summary>Images one message may carry.</summary>
    public const int MaxImagesPerMessage = 10;

    /// <summary>A decoded image whose long edge exceeds this is scaled down to it, keeping the aspect ratio.</summary>
    public const int MaxLongEdge = 2048;

    /// <summary>The encoded size (after any scaling) above which an image is refused.</summary>
    public const int MaxEncodedBytes = 5 * 1024 * 1024;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    /// <summary>The extensions the attach button's picker offers and a dropped file is recognised by.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>The media type from the file's leading bytes, or null when it is none of the four supported formats.</summary>
    public static string? SniffMediaType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G' &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return Png;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return Jpeg;
        }

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8' &&
            (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
        {
            return Gif;
        }

        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return Webp;
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> names a file the composer accepts by its extension.</summary>
    public static bool HasImageExtension(string? path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        foreach (var candidate in Extensions)
        {
            if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The size an image of <paramref name="width"/> x <paramref name="height"/> is scaled to: unchanged when its long edge
    /// is within <paramref name="maxLongEdge"/>, otherwise proportionally down so the long edge is exactly that.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxLongEdge = MaxLongEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var longEdge = Math.Max(width, height);
        if (longEdge <= maxLongEdge)
        {
            return (width, height);
        }

        var scale = (double)maxLongEdge / longEdge;
        return (
            width >= height ? maxLongEdge : Math.Max(1, (int)Math.Round(width * scale)),
            height >= width ? maxLongEdge : Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Whole kilobytes, rounded up and never zero, for captions.</summary>
    public static long Kilobytes(long bytes) => Math.Max(1, (bytes + 1023) / 1024);
}

/// <summary>An image ready for a message: its final media type and encoded bytes, plus a small preview when one was made.</summary>
internal sealed record PreparedImage(string MediaType, byte[] Bytes, Bitmap? Thumbnail);

/// <summary>Decoding, scaling and re-encoding behind the composer; a test swaps it for one without a renderer.</summary>
internal interface IImageCodec
{
    /// <summary>
    /// Decodes <paramref name="data"/> (already sniffed as <paramref name="mediaType"/>), scales it down when its long edge is
    /// over <see cref="ImageAttachmentRules.MaxLongEdge"/> and returns the bytes to send. Throws
    /// <see cref="InvalidDataException"/> when the data does not decode. Called off the UI thread.
    /// </summary>
    PreparedImage Prepare(byte[] data, string mediaType);

    /// <summary>A small preview of an encoded image, or null when it cannot be decoded. Called off the UI thread.</summary>
    Bitmap? Thumbnail(byte[] data);

    /// <summary>
    /// A preview whose long edge is at most <paramref name="maxLongEdge"/> pixels, or null when it cannot be decoded.
    /// The default member falls back to the codec's own default size, so existing implementors and test fakes keep
    /// compiling and behave as before. Called off the UI thread.
    /// </summary>
    Bitmap? Thumbnail(byte[] data, int maxLongEdge) => Thumbnail(data);
}

/// <summary>The production codec: Avalonia's own bitmap decoding and encoding, no image library.</summary>
internal sealed class AvaloniaImageCodec : IImageCodec
{
    private const int ThumbnailWidth = 128;
    private const int JpegQuality = 88;

    public static AvaloniaImageCodec Instance { get; } = new();

    public PreparedImage Prepare(byte[] data, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(data);
        Bitmap decoded;
        try
        {
            using var input = new MemoryStream(data, writable: false);
            decoded = new Bitmap(input);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException(exception.Message, exception);
        }

        using (decoded)
        {
            var size = decoded.PixelSize;
            var (width, height) = ImageAttachmentRules.FitWithin(size.Width, size.Height);
            if (width == size.Width && height == size.Height)
            {
                // Within bounds: the original bytes go as they are, in their own format.
                return new PreparedImage(mediaType, data, Thumbnail(data));
            }

            using var scaled = decoded.CreateScaledBitmap(new PixelSize(width, height), BitmapInterpolationMode.HighQuality);
            using var output = new MemoryStream();

            // A photo stays a JPEG (a PNG of it would be several times larger); everything else becomes a PNG, which also
            // flattens an animated GIF to its first frame.
            var keepJpeg = string.Equals(mediaType, ImageAttachmentRules.Jpeg, StringComparison.Ordinal);
            if (keepJpeg)
            {
                scaled.Save(output, new JpegBitmapEncoderOptions { Quality = JpegQuality });
            }
            else
            {
                scaled.Save(output, PngBitmapEncoderOptions.Default);
            }

            var bytes = output.ToArray();
            var encodedType = ImageAttachmentRules.SniffMediaType(bytes) ?? ImageAttachmentRules.Png;
            return new PreparedImage(encodedType, bytes, Thumbnail(bytes));
        }
    }

    public Bitmap? Thumbnail(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            return Bitmap.DecodeToWidth(input, ThumbnailWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    public Bitmap? Thumbnail(byte[] data, int maxLongEdge)
    {
        if (maxLongEdge <= 0)
        {
            // Zero means the caller wants the codec's own default size.
            return Thumbnail(data);
        }

        try
        {
            // Bound the width while decoding, so a full-size bitmap is never held: a landscape image (the usual
            // screenshot) is done in one pass. Only a portrait one keeps its height as the long edge, and only then is
            // it decoded a second time — the first attempt already produced a small bitmap, not the full-size one.
            using var byWidth = new MemoryStream(data, writable: false);
            var decoded = Bitmap.DecodeToWidth(byWidth, maxLongEdge, BitmapInterpolationMode.MediumQuality);
            if (Math.Max(decoded.PixelSize.Width, decoded.PixelSize.Height) <= maxLongEdge)
            {
                return decoded;
            }

            decoded.Dispose();
            using var byHeight = new MemoryStream(data, writable: false);
            return Bitmap.DecodeToHeight(byHeight, maxLongEdge, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// An image with a lazily decoded preview: the thumbnail is decoded off the UI thread the first time a view asks for it, so
/// replaying a long history with many images does not decode them all up front. A preview handed in up front is used as is.
/// </summary>
internal abstract class ImageThumbnailViewModel : ObservableObject
{
    private readonly int maxLongEdge;
    private Bitmap? thumbnail;
    private bool loading;

    /// <summary>
    /// <paramref name="maxLongEdge"/> bounds the long edge of the lazily decoded preview; 0 keeps the codec's own
    /// default size. Subclasses that do not care pass nothing and keep working.
    /// </summary>
    protected ImageThumbnailViewModel(ImageBlock block, long encodedBytes, Bitmap? thumbnail, int maxLongEdge = 0)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
        EncodedBytes = encodedBytes;
        this.thumbnail = thumbnail;
        this.maxLongEdge = maxLongEdge;
    }

    /// <summary>The decoder behind lazily loaded previews; a test may replace it.</summary>
    internal static IImageCodec Codec { get; set; } = AvaloniaImageCodec.Instance;

    public ImageBlock Block { get; }

    public long EncodedBytes { get; }

    /// <summary>"PNG · 123 KB", the tooltip of the thumbnail.</summary>
    public string Caption => ImageStrings.Caption(Block.MediaType, ImageAttachmentRules.Kilobytes(EncodedBytes));

    public Bitmap? Thumbnail
    {
        get
        {
            if (thumbnail is null && !loading)
            {
                loading = true;
                _ = LoadAsync();
            }

            return thumbnail;
        }
    }

    /// <summary>The preview if it is already decoded; never starts a decode.</summary>
    internal Bitmap? LoadedThumbnail => thumbnail;

    public void RefreshText() => OnPropertyChanged(nameof(Caption));

    private async Task LoadAsync()
    {
        var data = Block.Data;
        var codec = Codec;
        var maxEdge = maxLongEdge;
        var decoded = await Task.Run(() =>
        {
            try
            {
                var bytes = Convert.FromBase64String(data);
                return maxEdge > 0 ? codec.Thumbnail(bytes, maxEdge) : codec.Thumbnail(bytes);
            }
            catch (FormatException)
            {
                return null;
            }
        }).ConfigureAwait(true);
        if (decoded is null)
        {
            return;
        }

        thumbnail = decoded;
        OnPropertyChanged(nameof(Thumbnail));
    }
}

/// <summary>One image waiting in the composer: a thumbnail, its size caption and a remove button.</summary>
internal sealed class PendingImageViewModel : ImageThumbnailViewModel
{
    public PendingImageViewModel(ImageBlock block, long encodedBytes, Bitmap? thumbnail, Action<PendingImageViewModel> remove)
        : base(block, encodedBytes, thumbnail)
    {
        ArgumentNullException.ThrowIfNull(remove);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>One image of a sent user message in the transcript.</summary>
internal sealed class TranscriptImageViewModel(ImageBlock block)
    : ImageThumbnailViewModel(block, Forge.Core.Storage.MessageJson.DecodedLength(block.Data), thumbnail: null);
