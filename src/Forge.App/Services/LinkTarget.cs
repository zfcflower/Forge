namespace Forge.App.Services;

/// <summary>What a link or path in a reply points at.</summary>
internal enum LinkTargetKind
{
    /// <summary>Nothing we open: an unknown scheme, an anchor, an empty string.</summary>
    None,

    /// <summary>An http or https address: the system browser.</summary>
    Web,

    /// <summary>A local file that opens in the editor: source and other text.</summary>
    EditorFile,

    /// <summary>A local file the system opens with its own application: PDF, HTML, pictures, office files and the like.</summary>
    SystemFile,
}

/// <summary>A classified link: its kind and, for a local file, the full path (relative paths resolved against the workspace).</summary>
internal readonly record struct LinkTarget(LinkTargetKind Kind, string Location)
{
    public static LinkTarget None => default;

    private static readonly HashSet<string> SystemExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".html", ".htm", ".xhtml",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".svg",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".mp3", ".mp4", ".mov", ".wav", ".avi", ".mkv",
    };

    /// <summary>
    /// Whether <paramref name="url"/> could be something to open. Purely syntactic, so a pointer move over a link can ask it
    /// without touching the disk; <see cref="Classify"/> does the file system work, once, at the click.
    /// </summary>
    public static bool LooksOpenable(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url[0] == '#')
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // "C:\x" parses as a URI whose scheme is the drive letter; a one-letter scheme is a path.
            return uri.Scheme is "http" or "https" or "file" || uri.Scheme.Length == 1;
        }

        return !url.Contains("://", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="url"/>: web addresses as they are, <c>file:</c> URIs, absolute paths and workspace-relative paths
    /// (a trailing <c>#L12</c> or <c>:12</c> line suffix is dropped) when the file exists. A path outside every known root is
    /// still fine: the user clicked a link the agent wrote about their own machine.
    /// </summary>
    public static LinkTarget Classify(string url, string? workDir)
    {
        if (string.IsNullOrWhiteSpace(url) || url[0] == '#')
        {
            return None;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)
        {
            if (uri.Scheme is "http" or "https")
            {
                return new LinkTarget(LinkTargetKind.Web, url);
            }

            return uri.Scheme == "file" ? FromPath(uri.LocalPath, null) : None;
        }

        return FromPath(url, workDir);
    }

    private static LinkTarget FromPath(string raw, string? workDir)
    {
        var path = StripSuffix(Uri.UnescapeDataString(raw.Trim()));
        if (path.Length == 0)
        {
            return None;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                if (string.IsNullOrEmpty(workDir))
                {
                    return None;
                }

                path = Path.GetFullPath(Path.Combine(workDir, path));
            }

            if (!File.Exists(path))
            {
                return None;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return None;
        }

        return new LinkTarget(
            SystemExtensions.Contains(Path.GetExtension(path)) ? LinkTargetKind.SystemFile : LinkTargetKind.EditorFile,
            path);
    }

    /// <summary>
    /// Whether an inline code span reads like a file path (<c>src/App.cs</c>, <c>README.md:12</c>): no spaces or shell
    /// punctuation and an extension of 1-8 letters or digits. Only a hint - the click resolves it against the disk.
    /// </summary>
    public static bool LooksLikeFilePath(string text)
    {
        if (text.Length is < 3 or > 260)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || "()<>*?\"=|;,`$".Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var path = StripSuffix(text);
        var dot = path.LastIndexOf('.');
        if (dot <= 0 || dot == path.Length - 1 || path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var extension = path.AsSpan(dot + 1);
        return extension.Length <= 8 && IsAlphanumeric(extension);
    }

    private static bool IsAlphanumeric(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Drops <c>#L12</c>, <c>#L12-L20</c>, <c>?query</c> and a trailing <c>:12</c> / <c>:12:3</c> that is not a drive.</summary>
    internal static string StripSuffix(string path)
    {
        var cut = path.IndexOf('#', StringComparison.Ordinal);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        for (var round = 0; round < 2; round++)
        {
            var colon = path.LastIndexOf(':');
            if (colon > 1 && colon < path.Length - 1 && path.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0)
            {
                path = path[..colon];
            }
        }

        return path;
    }
}

/// <summary>The editor's "open this file" entry, installed by the main window so replies can open files without knowing the shell.</summary>
internal static class EditorFileOpener
{
    /// <summary>Opens a full path in the editor; null in a host without one.</summary>
    internal static Action<string>? Handler { get; set; }
}

/// <summary>
/// The system's "open this file with its own application" entry, installed by the window that owns a <c>TopLevel</c> like
/// <see cref="EditorFileOpener"/>: what FORGE cannot render itself (a PDF, an audio track, a video, an office file) is handed
/// to the OS, and a leaf of the transcript has no window of its own to ask.
/// </summary>
internal static class SystemFileOpener
{
    /// <summary>Opens a full path with the OS's default application; null in a host without a window.</summary>
    internal static Action<string>? Handler { get; set; }
}
