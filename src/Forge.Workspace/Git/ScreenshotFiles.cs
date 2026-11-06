using System.Globalization;

namespace Forge.Workspace;

/// <summary>Outcome of <see cref="ScreenshotFiles.Build"/>: either pairs or the reason the files cannot be used.</summary>
public sealed record ScreenshotFilesResult(IReadOnlyList<ScreenshotPair> Pairs, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Turns PNG files the user (or the agent) picked into <see cref="ScreenshotPair"/>s, for a case where a person took the
/// screenshots by hand and nothing is captured. Naming rule: <c>&lt;name&gt;-before.png</c> and <c>&lt;name&gt;-after.png</c> form
/// one pair; any other <c>&lt;name&gt;.png</c> is shown as an "after" image on its own. Paths are absolute or relative to the
/// worktree. Only real PNG files are accepted (the file signature is checked), at most
/// <see cref="ScreenshotCapture.MaxImageBytes"/> each; nothing is copied, the originals are read when they are published.
/// </summary>
public static class ScreenshotFiles
{
    public const int MaxFiles = ScreenshotCapture.MaxImagesPerSide * 2;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ScreenshotFilesResult Build(string worktree, IReadOnlyList<string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return Fail("No files were given.");
        }

        if (files.Count > MaxFiles)
        {
            return Fail($"{files.Count.ToString(CultureInfo.InvariantCulture)} files were given; at most {MaxFiles.ToString(CultureInfo.InvariantCulture)} are accepted.");
        }

        var byName = new SortedDictionary<string, (string? Before, string? After)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || file.Contains('\0', StringComparison.Ordinal))
            {
                return Fail("A file path is empty.");
            }

            var full = WorkspacePaths.Normalize(Path.Combine(worktree, file.Trim()));
            if (!File.Exists(full))
            {
                return Fail($"'{file}' is not an existing file (looked at {full}).");
            }

            if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"'{file}' is not a .png file; only PNG images can be published.");
            }

            var length = new FileInfo(full).Length;
            if (length > ScreenshotCapture.MaxImageBytes)
            {
                return Fail($"'{file}' is larger than {ScreenshotCapture.MaxImageBytes / (1024 * 1024)} MiB.");
            }

            if (!HasPngSignature(full))
            {
                return Fail($"'{file}' is not a PNG image (wrong file signature).");
            }

            var stem = ScreenshotCapture.SafeName(Path.GetFileName(full));
            var (name, side) = SplitSide(stem);
            byName.TryGetValue(name, out var existing);
            if (side == Side.Before)
            {
                if (existing.Before is not null)
                {
                    return Fail($"Two files map to the before image of '{name}'.");
                }

                byName[name] = (full, existing.After);
            }
            else
            {
                if (existing.After is not null)
                {
                    return Fail($"Two files map to the after image of '{name}'.");
                }

                byName[name] = (existing.Before, full);
            }
        }

        var pairs = new List<ScreenshotPair>(byName.Count);
        foreach (var (name, sides) in byName)
        {
            pairs.Add(new ScreenshotPair(name, sides.Before, sides.After));
        }

        return new ScreenshotFilesResult(pairs, null);
    }

    private static (string Name, Side Side) SplitSide(string stem)
    {
        if (stem.EndsWith("-before", StringComparison.OrdinalIgnoreCase) && stem.Length > "-before".Length)
        {
            return (stem[..^"-before".Length], Side.Before);
        }

        if (stem.EndsWith("-after", StringComparison.OrdinalIgnoreCase) && stem.Length > "-after".Length)
        {
            return (stem[..^"-after".Length], Side.After);
        }

        return (stem, Side.After);
    }

    private static bool HasPngSignature(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual(PngSignature);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ScreenshotFilesResult Fail(string error) => new([], error);

    private enum Side
    {
        Before,
        After,
    }
}
