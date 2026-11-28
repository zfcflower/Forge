using Forge.Abstractions;
using Forge.App.Logging;
using Forge.Core.Storage;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// An attachment of a sent message as the file something else opens: the message carries the bytes inline, while a preview
/// tab and an OS application both open <em>files</em>, so the bytes are written under the temp folder — under the attachment's
/// own name, because the extension is what tells the preview and the OS what they are looking at — and the same attachment
/// always lands on the same path. Everything runs on a pool thread: decoding a multi-megabyte base64 payload is not a UI job.
/// </summary>
internal static class AttachmentFiles
{
    /// <summary>Where the copies go; nothing else lives here.</summary>
    internal static string Folder { get; } = TempFiles.Folder("forge-attachments");

    /// <summary>A picture as a PNG/JPEG/GIF/WebP file, or null when the bytes are not one or cannot be written.</summary>
    internal static string? Materialize(ImageBlock image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Write($"image|{image.MediaType}|{image.Data}", "image" + ExtensionOf(image.MediaType, ".png"), () => Decode(image.Data));
    }

    /// <summary>A PDF, audio or video file under its own name, or null when it cannot be written.</summary>
    internal static string? Materialize(FileBlock file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = FileNameOf(file.Name, file.MediaType);
        return Write($"file|{file.MediaType}|{file.Data}", name, () => Decode(file.Data));
    }

    /// <summary>The text of an inlined text or source file, written back under its own name, or null when it cannot be written.</summary>
    internal static string? Materialize(TextBlock text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TextAttachment.TryExtract(text.Text, out var name, out var content))
        {
            return null;
        }

        var fileName = FileNameOf(name, "text/plain");
        return Write($"text|{name}|{content}", fileName, () => System.Text.Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// The file's path: written once per content, so a second click reuses it, and never at all when the target already exists.
    /// A failure to decode or write is traced and answers null — a click that cannot open is not an error to raise.
    /// </summary>
    private static string? Write(string key, string fileName, Func<byte[]> read)
    {
        try
        {
            var path = TempFiles.PathFor(Folder, key, fileName);
            if (TempFiles.Exists(path))
            {
                return path;
            }

            var bytes = read();
            if (bytes.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(Folder);
            TempFiles.Sweep(Folder);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLogs.Session.ZLogWarning($"attachment could not be written: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    private static byte[] Decode(string base64) => Convert.FromBase64String(base64);

    /// <summary>
    /// The name the copy takes: the attachment's own, cleaned to a bare file name and bounded, with an extension it can be
    /// recognised by — the original one when it has one, the media type's otherwise.
    /// </summary>
    private static string FileNameOf(string? name, string mediaType)
    {
        var bare = string.IsNullOrWhiteSpace(name) ? string.Empty : Path.GetFileName(name.Trim());
        var cleaned = new System.Text.StringBuilder(bare.Length);
        foreach (var character in bare)
        {
            cleaned.Append(Array.IndexOf(InvalidFileNameChars, character) >= 0 ? '_' : character);
        }

        var fileName = cleaned.ToString().Trim().TrimEnd('.');
        if (fileName.Length > 96)
        {
            var extension = Path.GetExtension(fileName);
            fileName = fileName[..Math.Max(1, 96 - extension.Length)] + extension;
        }

        if (Path.GetExtension(fileName).Length > 1)
        {
            return fileName;
        }

        return (fileName.Length == 0 ? "attachment" : fileName) + ExtensionOf(mediaType, ".bin");
    }

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>The extension a media type is known by; <paramref name="fallback"/> for one nothing here knows.</summary>
    private static string ExtensionOf(string mediaType, string fallback) =>
        mediaType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "text/plain" => ".txt",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            _ => fallback,
        };
}
