using System.Globalization;

namespace Forge.Abstractions;

/// <summary>
/// The text that stands in for a binary block anywhere text is wanted (search, summaries, retrieval, the flattened text of a
/// tool result): <c>[image: image/png, 123 KB]</c>. Never the base64 itself.
/// </summary>
public static class ContentPlaceholders
{
    /// <summary><c>[image: image/png, 123 KB]</c>: the media type and the decoded size, at least one KB.</summary>
    public static string ImagePlaceholder(ImageBlock image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return string.Concat("[image: ", image.MediaType, ", ", Kilobytes(image.Data), " KB]");
    }

    /// <summary><c>[file: report.pdf, 123 KB]</c>, or the media type when the file has no name.</summary>
    public static string FilePlaceholder(FileBlock file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var label = string.IsNullOrWhiteSpace(file.Name) ? file.MediaType : file.Name;
        return string.Concat("[file: ", label, ", ", Kilobytes(file.Data), " KB]");
    }

    /// <summary>Decoded byte count of a base64 string, from its length and padding alone (nothing is decoded).</summary>
    public static long DecodedLength(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        var length = base64.Length;
        if (length == 0)
        {
            return 0;
        }

        var padding = base64[length - 1] == '=' ? (length > 1 && base64[length - 2] == '=' ? 2 : 1) : 0;
        return Math.Max(0, (long)length / 4 * 3 + (length % 4 * 3 / 4) - padding);
    }

    private static string Kilobytes(string base64) =>
        Math.Max(1, (DecodedLength(base64) + 1023) / 1024).ToString(CultureInfo.InvariantCulture);
}
