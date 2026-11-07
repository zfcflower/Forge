namespace Forge.IO;

/// <summary>A requested path resolved outside the workspace root, or a path the platform refuses to parse.</summary>
public sealed class WorkspacePathException(string message) : Exception(message)
{
}

/// <summary>
/// Path identity for everything that is bounded by a workspace: one spelling of a path, one answer to "is this inside
/// the workspace", and a resolution that follows the links of every component that already exists so a junction or a
/// symlinked directory cannot be used to step outside the root.
/// <para>Resolution is not a sandbox: a caller that can create links and race the check can still escape (TOCTOU), and
/// the OS permissions of the process still apply. It stops the accidental and the obvious escape, nothing more.</para>
/// </summary>
public static class WorkspacePath
{
    /// <summary>How two spellings of the same path are compared on this platform.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>How two spellings of the same path are compared on this platform, as a comparer value.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Absolute path with a trailing separator removed; no link is resolved.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full) is { Length: > 0 } trimmed ? trimmed : full;
    }

    /// <summary>A workspace-relative path in the form the walker and the matchers use: forward slashes, no <c>./</c>.</summary>
    public static string NormalizeRelative(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var value = relativePath.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> itself or below it, without resolving links.</summary>
    public static bool IsUnder(string path, string root)
    {
        var normalizedPath = Normalize(path);
        var normalizedRoot = Normalize(root);
        if (!normalizedPath.StartsWith(normalizedRoot, Comparison))
        {
            return false;
        }

        return normalizedPath.Length == normalizedRoot.Length
            || normalizedPath[normalizedRoot.Length] is '/' or '\\';
    }

    /// <summary>
    /// Resolves the links of every existing component of <paramref name="path"/> and returns the resulting real path.
    /// Components that do not exist yet are appended unchanged, so a not-yet-created file still yields the directory it
    /// would really land in.
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            if (!Exists(candidate))
            {
                // Nothing below a missing component can be a link, so the rest is appended verbatim: a path that does not
                // exist yet still resolves to where it would really land, not to its deepest existing ancestor.
                for (; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                }

                break;
            }

            current = candidate;
            if (ResolveLinkTarget(candidate) is { } target)
            {
                current = target;
            }
        }

        return Normalize(current);
    }

    /// <summary>
    /// True when both spellings name the same path. The normalized spellings are compared first; when they differ, the
    /// links of every existing component are resolved too (macOS <c>/tmp</c> and <c>/var</c> are links).
    /// </summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), Comparison)
        || string.Equals(ResolveRealPath(left), ResolveRealPath(right), Comparison);

    /// <summary>
    /// Resolves <paramref name="requested"/> (absolute, or relative to <paramref name="root"/>) to a real path that is
    /// inside or equal to <paramref name="root"/>.
    /// </summary>
    /// <exception cref="WorkspacePathException">The path resolved outside the root, or the path cannot be parsed.</exception>
    public static string Resolve(string root, string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);

        string realRoot;
        string real;
        try
        {
            realRoot = ResolveRealPath(root);
            var combined = Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested);
            real = ResolveRealPath(combined);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspacePathException($"'{requested}' is not a usable path: {exception.Message}");
        }

        if (!string.Equals(real, realRoot, Comparison) && !IsUnder(real, realRoot))
        {
            throw new WorkspacePathException(
                $"'{requested}' resolves to '{real}', which is outside the workspace '{realRoot}'.");
        }

        return real;
    }

    /// <summary>The workspace-relative form of <paramref name="path"/>, with forward slashes; empty for the root itself.</summary>
    public static string RelativeTo(string root, string path)
    {
        var normalizedRoot = Normalize(root);
        var normalized = Normalize(path);
        if (string.Equals(normalized, normalizedRoot, Comparison))
        {
            return string.Empty;
        }

        if (!IsUnder(normalized, normalizedRoot))
        {
            throw new WorkspacePathException($"'{path}' is outside the workspace '{root}'.");
        }

        return NormalizeRelative(normalized[(normalizedRoot.Length + 1)..]);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return null;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target ? Normalize(target.FullName) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
