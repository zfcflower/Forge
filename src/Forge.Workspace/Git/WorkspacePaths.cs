using System.Runtime.InteropServices;
using System.Text;

namespace Forge.Workspace;

internal static class WorkspacePaths
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full) is { Length: > 0 } trimmed ? trimmed : full;
    }

    public static string NormalizeCandidate(string path) => Normalize(path);

    /// <summary>
    /// True when both spellings name the same path. The normalized spellings are compared first; when they differ, the
    /// links of every existing component are resolved too, because git reports resolved paths while a caller may pass a
    /// path through a symlink (macOS <c>/tmp</c> and <c>/var</c> are links).
    /// </summary>
    public static bool Equals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(left), Normalize(right), comparison)
            || string.Equals(ResolveRealPath(left), ResolveRealPath(right), comparison);
    }

    public static bool IsUnder(string path, string root)
    {
        var normalizedPath = Normalize(path);
        var normalizedRoot = Normalize(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalizedPath.StartsWith(normalizedRoot, comparison))
        {
            return false;
        }

        return normalizedPath.Length == normalizedRoot.Length
            || normalizedPath[normalizedRoot.Length] is '/' or '\\';
    }

    public static string FileName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>True when the path exists and is a symlink, junction or any other reparse point.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the links of every existing component of <paramref name="path"/> and returns the resulting real
    /// path. Components that do not exist yet are appended unchanged, so a not-yet-created file still yields the
    /// directory it would really land in.
    /// </summary>
    public static string ResolveRealPath(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var segment in full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Exists(current))
            {
                break;
            }

            if (ResolveLinkTarget(current) is { } target)
            {
                current = target;
            }
        }

        return Normalize(current);
    }

    /// <summary>
    /// Verifies that <paramref name="fullPath"/> is inside <paramref name="root"/> and reaches it without passing
    /// through a symlink, junction or other reparse point below the root; a write can then never land outside the
    /// workspace. Throws <see cref="ArgumentException"/> before the caller touches the file system.
    /// </summary>
    public static void EnsurePlainPathInside(string root, string fullPath, string parameterName = "path")
    {
        var normalizedRoot = Normalize(root);
        var normalizedPath = Normalize(fullPath);
        if (!IsUnder(normalizedPath, normalizedRoot))
        {
            throw new ArgumentException($"'{fullPath}' is outside the workspace '{normalizedRoot}'.", parameterName);
        }

        var current = normalizedRoot;
        foreach (var segment in normalizedPath[normalizedRoot.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Exists(current))
            {
                break;
            }

            if (IsReparsePoint(current))
            {
                throw new ArgumentException(
                    $"'{fullPath}' passes through the link '{current}'; spec files must be plain files inside the workspace.",
                    parameterName);
            }
        }

        var realRoot = ResolveRealPath(normalizedRoot);
        var realTarget = ResolveRealPath(normalizedPath);
        if (!IsUnder(realTarget, realRoot))
        {
            throw new ArgumentException(
                $"'{fullPath}' resolves to '{realTarget}', which is outside the workspace '{realRoot}'.",
                parameterName);
        }
    }

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

/// <summary>Decodes git's C-style path quoting (<c>"a/pa\tth"</c>, octal escapes for non-ASCII bytes).</summary>
internal static class GitQuoting
{
    public static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"')
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '"')
            {
                break;
            }

            if (ch != '\\' || i + 1 >= value.Length)
            {
                AppendChar(bytes, ch);
                continue;
            }

            var escape = value[++i];
            switch (escape)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 'f': bytes.Add(0x0C); break;
                case 'n': bytes.Add(0x0A); break;
                case 'r': bytes.Add(0x0D); break;
                case 't': bytes.Add(0x09); break;
                case 'v': bytes.Add(0x0B); break;
                case '"': bytes.Add(0x22); break;
                case '\\': bytes.Add(0x5C); break;
                default:
                    if (escape is >= '0' and <= '7'
                        && i + 2 < value.Length
                        && value[i + 1] is >= '0' and <= '7'
                        && value[i + 2] is >= '0' and <= '7')
                    {
                        bytes.Add((byte)(((escape - '0') << 6) | ((value[i + 1] - '0') << 3) | (value[i + 2] - '0')));
                        i += 2;
                    }
                    else
                    {
                        AppendChar(bytes, escape);
                    }

                    break;
            }
        }

        return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
    }

    private static void AppendChar(List<byte> bytes, char value)
    {
        if (value < 0x80)
        {
            bytes.Add((byte)value);
            return;
        }

        Span<byte> buffer = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes([value], buffer);
        for (var i = 0; i < written; i++)
        {
            bytes.Add(buffer[i]);
        }
    }
}

/// <summary>Splits text into lines, dropping the CR of CRLF pairs and keeping no trailing empty line.</summary>
internal static class TextLines
{
    public static IEnumerable<string> Split(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var line = text[start..end];
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            yield return line;
            start = end + 1;
        }
    }
}
