namespace Forge.App.ViewModels.Preview;

/// <summary>What the file panel renders for a path, decided before the file content is read.</summary>
internal enum FilePreviewKind
{
    /// <summary>Plain text: the native editor document.</summary>
    Text,

    /// <summary>A Git LFS pointer file (its bytes say so, whatever the extension).</summary>
    Lfs,

    /// <summary>An image (PNG, JPEG, GIF, WebP) decoded into a bitmap.</summary>
    Image,

    /// <summary>A CSV/TSV file parsed into a read-only table.</summary>
    Table,

    /// <summary>An <c>.ipynb</c> notebook parsed into cells and outputs.</summary>
    Notebook,

    /// <summary>A PDF: no internal rendering, handed to the OS.</summary>
    Pdf,

}

/// <summary>
/// Pure classification, no IO: the kind of preview a path gets. The caller reads the first bytes itself (enough for the
/// Git LFS pointer marker) and passes them here, so this stays fully unit-testable and never touches the disk.
/// </summary>
internal static class FilePreviewClassifier
{
    /// <summary>Bytes the caller should peek at: the LFS pointer marker fits well inside.</summary>
    public const int PeekBytes = 512;

    private static ReadOnlySpan<byte> LfsPointerPrefix => "version https://git-lfs.github.com/spec/v1"u8;

    public static FilePreviewKind Classify(string path, ReadOnlySpan<byte> firstBytes)
    {
        // The LFS check comes first: an LFS-tracked image is a text pointer file, not an image.
        if (firstBytes.StartsWith(LfsPointerPrefix))
        {
            return FilePreviewKind.Lfs;
        }

        return ExtensionOf(path) switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => FilePreviewKind.Image,
            ".csv" or ".tsv" => FilePreviewKind.Table,
            ".ipynb" => FilePreviewKind.Notebook,
            ".pdf" => FilePreviewKind.Pdf,
            _ => FilePreviewKind.Text,
        };
    }

    /// <summary>The field separator a table preview parses: tab for TSV (no quoting), comma otherwise.</summary>
    public static char TableSeparator(string path) =>
        string.Equals(ExtensionOf(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';

    private static string ExtensionOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();
}
