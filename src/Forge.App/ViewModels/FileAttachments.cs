using System.Text;
using System.Text.Unicode;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;
using Forge.Core.Storage;

namespace Forge.App.ViewModels;

/// <summary>What an attachment is, which decides its limit, its capability flag and its wire shape.</summary>
internal enum AttachmentKind
{
    Image,
    Pdf,
    Audio,
    Video,

    /// <summary>A text or source file, inlined as a <see cref="TextBlock"/> (<see cref="TextAttachment"/>); always allowed.</summary>
    Text,
}

/// <summary>Why an attachment was not added.</summary>
internal enum AttachmentRejection
{
    None,

    /// <summary>Not a format the composer knows (or an unreadable file).</summary>
    Unsupported,
    TooLarge,
    TooMany,

    /// <summary>The current model's catalogue entry lists its inputs and this kind is not among them.</summary>
    ModelRejects,

    /// <summary>The current provider's wire format has no shape for this file.</summary>
    WireUnsupported,

    /// <summary>A text file that is binary or not valid UTF-8.</summary>
    NotText,
}

/// <summary>A classified file: its kind and the media type it is sent as.</summary>
internal readonly record struct AttachmentType(AttachmentKind Kind, string MediaType);

/// <summary>
/// The one classifier behind pick, paste and drop: the extension says what a file claims to be (so an oversized file is
/// refused before it is read), the leading bytes say what it is. Size limits per kind are the documented constants below;
/// they sit under what the providers accept inline (Anthropic and OpenAI take PDFs up to 32 MB per request, inline audio is
/// limited to around 20-25 MB) and keep a session's stored history bounded.
/// </summary>
internal static class FileAttachmentRules
{
    /// <summary>Non-image files one message may carry (images have their own limit).</summary>
    public const int MaxFilesPerMessage = 10;

    public const long MaxPdfBytes = 32L * 1024 * 1024;

    public const long MaxAudioBytes = 20L * 1024 * 1024;

    public const long MaxVideoBytes = 20L * 1024 * 1024;

    /// <summary>A text file is inlined into the prompt, so it is kept far smaller: about 64k tokens at most.</summary>
    public const long MaxTextBytes = 256L * 1024;

    /// <summary>The leading bytes checked for NUL when deciding whether a file is text.</summary>
    private const int BinaryProbeBytes = 8192;

    public static readonly string[] PdfExtensions = [".pdf"];

    public static readonly string[] AudioExtensions = [".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".weba"];

    public static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".webm", ".mkv", ".ogv", ".avi", ".mpeg", ".mpg"];

    /// <summary>Text and source files the picker offers; any other file whose bytes are UTF-8 text is accepted as text too.</summary>
    public static readonly string[] TextExtensions =
    [
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".env", ".properties", ".html", ".htm", ".css", ".scss", ".less", ".svg", ".sql", ".graphql", ".proto",
        ".cs", ".csx", ".fs", ".vb", ".axaml", ".xaml", ".csproj", ".props", ".targets", ".sln", ".slnx", ".razor", ".cshtml",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".m", ".mm", ".swift", ".java", ".kt", ".kts", ".scala", ".groovy",
        ".gradle", ".go", ".rs", ".py", ".pyi", ".rb", ".php", ".pl", ".lua", ".r", ".jl", ".dart", ".js", ".mjs", ".cjs",
        ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        ".dockerfile", ".tf", ".hcl", ".nix", ".zig", ".hs", ".ex", ".exs", ".erl", ".clj", ".elm", ".ml", ".mli", ".tex",
        ".diff", ".patch", ".gitignore", ".editorconfig",
    ];

    /// <summary>The kind a file claims by its extension, or null when the extension says nothing (or there is none).</summary>
    public static AttachmentKind? KindFromExtension(string? path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        if (ImageAttachmentRules.HasImageExtension(path))
        {
            return AttachmentKind.Image;
        }

        return Contains(PdfExtensions, extension) ? AttachmentKind.Pdf
            : Contains(AudioExtensions, extension) ? AttachmentKind.Audio
            : Contains(VideoExtensions, extension) ? AttachmentKind.Video
            : Contains(TextExtensions, extension) ? AttachmentKind.Text
            : null;
    }

    /// <summary>The largest file of <paramref name="kind"/> accepted (images: before scaling, see <see cref="ImageAttachmentRules"/>).</summary>
    public static long MaxBytes(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => ImageAttachmentRules.MaxEncodedBytes * 16L,
        AttachmentKind.Pdf => MaxPdfBytes,
        AttachmentKind.Audio => MaxAudioBytes,
        AttachmentKind.Video => MaxVideoBytes,
        _ => MaxTextBytes,
    };

    /// <summary>The bound on reading a file before its bytes are classified: its claimed kind's limit, else the largest one.</summary>
    public static long ReadLimit(string? path) =>
        KindFromExtension(path) is { } kind ? MaxBytes(kind) : MaxPdfBytes;

    /// <summary>The capability flag a kind needs; <see cref="ModelInputs.Text"/> for text, which is always allowed.</summary>
    public static ModelInputs Flag(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => ModelInputs.Image,
        AttachmentKind.Pdf => ModelInputs.Pdf,
        AttachmentKind.Audio => ModelInputs.Audio,
        AttachmentKind.Video => ModelInputs.Video,
        _ => ModelInputs.Text,
    };

    /// <summary>
    /// Classifies a file from its bytes, with <paramref name="name"/> breaking ties the bytes cannot (an Ogg or WebM
    /// container, a bare MPEG audio frame). Images, PDF, audio and video are recognised by their signatures; anything else
    /// is text when it is valid UTF-8 without NUL bytes. Null when it is none of these.
    /// </summary>
    public static AttachmentType? Classify(ReadOnlySpan<byte> data, string? name)
    {
        if (ImageAttachmentRules.SniffMediaType(data) is { } image)
        {
            return new(AttachmentKind.Image, image);
        }

        if (SniffBinary(data, Path.GetExtension(name)) is { } binary)
        {
            return binary;
        }

        return LooksLikeText(data) ? new AttachmentType(AttachmentKind.Text, "text/plain") : null;
    }

    /// <summary>
    /// Decodes a text file strictly: UTF-8 (a leading BOM is dropped), no NUL bytes. False for binary data or invalid UTF-8,
    /// which the composer refuses rather than sending mojibake.
    /// </summary>
    public static bool TryDecodeText(ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;
        if (data.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        if (data.StartsWith(Utf8Bom))
        {
            data = data[Utf8Bom.Length..];
        }

        if (!Utf8.IsValid(data))
        {
            return false;
        }

        text = Encoding.UTF8.GetString(data);
        return true;
    }

    /// <summary>The short label of a file chip: "PDF", "MP3", "TXT"…, from the name's extension or the media subtype.</summary>
    public static string Label(string? name, string mediaType)
    {
        var extension = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(extension) && extension.Length <= 9)
        {
            return extension[1..].ToUpperInvariant();
        }

        var slash = mediaType.IndexOf('/');
        return (slash < 0 ? mediaType : mediaType[(slash + 1)..]).ToUpperInvariant();
    }

    /// <summary>The kind of a stored <see cref="FileBlock"/>, from its media type (unknown types count as video, the strictest).</summary>
    public static AttachmentKind KindOf(FileBlock file) => ProviderAttachments.KindOf(file.MediaType) switch
    {
        ModelInputs.Image => AttachmentKind.Image,
        ModelInputs.Pdf => AttachmentKind.Pdf,
        ModelInputs.Audio => AttachmentKind.Audio,
        ModelInputs.Text => AttachmentKind.Text,
        _ => AttachmentKind.Video,
    };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        // The probe rejects the common binary file cheaply before the full validation pass.
        var probe = data.Length > BinaryProbeBytes ? data[..BinaryProbeBytes] : data;
        return probe.IndexOf((byte)0) < 0 && data.IndexOf((byte)0) < 0 && Utf8.IsValid(data);
    }

    private static AttachmentType? SniffBinary(ReadOnlySpan<byte> data, string? extension)
    {
        if (data.StartsWith("%PDF-"u8))
        {
            return new(AttachmentKind.Pdf, ProviderAttachments.Pdf);
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8))
        {
            var form = data.Slice(8, 4);
            if (form.SequenceEqual("WAVE"u8))
            {
                return new(AttachmentKind.Audio, "audio/wav");
            }

            if (form.SequenceEqual("AVI "u8))
            {
                return new(AttachmentKind.Video, "video/x-msvideo");
            }
        }

        if (data.StartsWith("ID3"u8))
        {
            return new(AttachmentKind.Audio, "audio/mpeg");
        }

        // A bare MPEG layer III frame (no ID3 tag) is only two bytes of signature, so the name has to agree.
        if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x06) == 0x02 &&
            Is(extension, ".mp3"))
        {
            return new(AttachmentKind.Audio, "audio/mpeg");
        }

        if (data.StartsWith("fLaC"u8))
        {
            return new(AttachmentKind.Audio, "audio/flac");
        }

        if (data.StartsWith("OggS"u8))
        {
            return Is(extension, ".ogv") ? new(AttachmentKind.Video, "video/ogg") : new(AttachmentKind.Audio, "audio/ogg");
        }

        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return Is(extension, ".weba") ? new(AttachmentKind.Audio, "audio/webm")
                : Is(extension, ".mkv") ? new(AttachmentKind.Video, "video/x-matroska")
                : new(AttachmentKind.Video, "video/webm");
        }

        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = data.Slice(8, 4);
            if (brand.SequenceEqual("M4A "u8) || brand.SequenceEqual("M4B "u8) || Is(extension, ".m4a"))
            {
                return new(AttachmentKind.Audio, "audio/mp4");
            }

            return brand.SequenceEqual("qt  "u8)
                ? new(AttachmentKind.Video, "video/quicktime")
                : new(AttachmentKind.Video, "video/mp4");
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && (data[3] == 0xBA || data[3] == 0xB3))
        {
            return new(AttachmentKind.Video, "video/mpeg");
        }

        return null;
    }

    private static bool Is(string? extension, string expected) =>
        string.Equals(extension, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string[] extensions, string extension)
    {
        foreach (var candidate in extensions)
        {
            if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A non-image attachment shown as a chip (icon, name, size): a <see cref="FileBlock"/> (PDF, audio, video) or a text file
/// inlined as a <see cref="TextBlock"/>.
/// </summary>
internal abstract class FileChipViewModel : ObservableObject
{
    protected FileChipViewModel(ContentBlock block, string name, AttachmentKind kind, string mediaType, long bytes)
    {
        ArgumentNullException.ThrowIfNull(block);
        Block = block;
        Name = name;
        Kind = kind;
        MediaType = mediaType;
        Bytes = bytes;
    }

    /// <summary>The block the message carries.</summary>
    public ContentBlock Block { get; }

    public string Name { get; }

    public AttachmentKind Kind { get; }

    public string MediaType { get; }

    /// <summary>The file's size: decoded bytes of a binary file, UTF-8 bytes of a text file's content.</summary>
    public long Bytes { get; }

    /// <summary>"PDF · 120 KB", under the name.</summary>
    public string Caption => AttachmentStrings.Caption(FileAttachmentRules.Label(Name, MediaType), Bytes);

    public void RefreshText() => OnPropertyChanged(nameof(Caption));

    /// <summary>
    /// The chip for an attachment block of a message: a <see cref="FileBlock"/>, or a text block in the
    /// <see cref="TextAttachment"/> shape; null for anything else (typed text, images).
    /// </summary>
    public static (string Name, AttachmentKind Kind, string MediaType, long Bytes)? Describe(ContentBlock block)
    {
        switch (block)
        {
            case FileBlock file:
                return (
                    string.IsNullOrWhiteSpace(file.Name) ? file.MediaType : file.Name,
                    FileAttachmentRules.KindOf(file),
                    file.MediaType,
                    MessageJson.DecodedLength(file.Data));
            case TextBlock text when TextAttachment.TryParse(text.Text, out var name, out var bytes):
                return (name, AttachmentKind.Text, "text/plain", bytes);
            default:
                return null;
        }
    }
}

/// <summary>A file waiting in the composer, with a remove button.</summary>
internal sealed class PendingFileViewModel : FileChipViewModel
{
    public PendingFileViewModel(
        ContentBlock block, string name, AttachmentKind kind, string mediaType, long bytes, Action<PendingFileViewModel> remove)
        : base(block, name, kind, mediaType, bytes)
    {
        ArgumentNullException.ThrowIfNull(remove);
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>One non-image attachment of a sent user message in the transcript.</summary>
internal sealed class TranscriptFileViewModel(ContentBlock block, string name, AttachmentKind kind, string mediaType, long bytes)
    : FileChipViewModel(block, name, kind, mediaType, bytes);
