namespace Forge.Workspace;

/// <summary>
/// Reads the captured pictures into memory before the capture directory is removed (wave W17a), so <c>pr_screenshots</c>
/// can hand them back as image blocks. A side that is missing, unreadable or larger than <paramref name="limit"/> is simply
/// left out: the text report already names every file, and a picture is evidence, not a contract.
/// </summary>
internal static class ScreenshotImages
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static IReadOnlyList<ScreenshotImage> Read(IReadOnlyList<ScreenshotPair> pairs, int limit)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
        {
            return [];
        }

        var images = new List<ScreenshotImage>(pairs.Count * 2);
        foreach (var pair in pairs)
        {
            Add(images, pair.BeforePath, pair.BeforeFileName, limit);
            Add(images, pair.AfterPath, pair.AfterFileName, limit);
        }

        return images;
    }

    private static void Add(List<ScreenshotImage> images, string? path, string fileName, int limit)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > limit)
            {
                return;
            }

            var data = File.ReadAllBytes(path);
            // Only what the app can display: the capture pipeline writes png, and a signature check keeps a mislabelled
            // file from reaching the image block (which would fail to decode later, far from the cause).
            if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            {
                return;
            }

            images.Add(new ScreenshotImage(fileName, "image/png", data));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The text report still names the file; a missing picture is not an error worth failing the run over.
        }
    }
}