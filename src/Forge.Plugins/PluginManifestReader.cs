using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Plugins;

public enum PluginManifestError
{
    None = 0,
    PluginDirectoryNotFound,
    ManifestNotFound,
    ManifestUnreadable,
    ManifestTooLarge,
    InvalidJson,
    MissingId,
    InvalidId,
    MissingName,
    MissingVersion,
    InvalidVersion,
    MissingEntryAssembly,
    EntryAssemblyOutsidePluginDirectory,
    EntryAssemblyNotFound,
    MissingEntryType,
    InvalidEntryType,
}

public sealed record PluginManifestResult(PluginManifest? Manifest, PluginManifestError Error, string? Message)
{
    public bool Success => Manifest is not null;

    internal static PluginManifestResult Ok(PluginManifest manifest) => new(manifest, PluginManifestError.None, null);

    internal static PluginManifestResult Fail(PluginManifestError error, string message) => new(null, error, message);
}

/// <summary>Reads and validates <c>&lt;plugin&gt;/plugin.json</c>; every failure is a typed result, never an exception.</summary>
public static class PluginManifestReader
{
    public const string FileName = "plugin.json";

    public const int MaxManifestBytes = 64 * 1024;

    public const int MaxIdLength = 64;

    private const int MaxTypeNameLength = 512;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static PluginManifestResult Read(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var directory = Path.GetFullPath(pluginDirectory);
        if (!Directory.Exists(directory))
        {
            return PluginManifestResult.Fail(PluginManifestError.PluginDirectoryNotFound, $"Plugin directory not found: {directory}");
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return PluginManifestResult.Fail(PluginManifestError.ManifestNotFound, $"Missing {FileName} in {directory}");
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxManifestBytes)
            {
                return PluginManifestResult.Fail(PluginManifestError.ManifestTooLarge, $"{FileName} exceeds {MaxManifestBytes} bytes");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, DocumentOptions);
            return Validate(directory, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginManifestResult.Fail(PluginManifestError.ManifestUnreadable, $"{FileName} could not be read: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidJson, $"{FileName} is not valid JSON: {ex.Message}");
        }
    }

    public static async Task<PluginManifestResult> ReadAsync(string pluginDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var directory = Path.GetFullPath(pluginDirectory);
        if (!Directory.Exists(directory))
        {
            return PluginManifestResult.Fail(PluginManifestError.PluginDirectoryNotFound, $"Plugin directory not found: {directory}");
        }

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return PluginManifestResult.Fail(PluginManifestError.ManifestNotFound, $"Missing {FileName} in {directory}");
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxManifestBytes)
            {
                return PluginManifestResult.Fail(PluginManifestError.ManifestTooLarge, $"{FileName} exceeds {MaxManifestBytes} bytes");
            }

            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            return Validate(directory, document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginManifestResult.Fail(PluginManifestError.ManifestUnreadable, $"{FileName} could not be read: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidJson, $"{FileName} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="value"/> is usable as a plugin id: 1..64 ASCII letters/digits with <c>. _ -</c> inside.</summary>
    public static bool IsValidId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdLength)
        {
            return false;
        }

        if (!IsAsciiIdChar(value[0]) || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        for (var i = 1; i < value.Length - 1; i++)
        {
            if (!IsAsciiIdChar(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="value"/> parses as a semver-like version: 1..4 numeric parts, optional <c>-prerelease</c> and <c>+build</c>.</summary>
    public static bool IsValidVersion(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        var span = value.AsSpan();
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            if (!AreValidIdentifiers(span[(plus + 1)..]))
            {
                return false;
            }

            span = span[..plus];
        }

        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            if (!AreValidIdentifiers(span[(dash + 1)..]))
            {
                return false;
            }

            span = span[..dash];
        }

        var parts = 0;
        foreach (var range in span.Split('.'))
        {
            parts++;
            if (parts > 4 || !IsNumeric(span[range]))
            {
                return false;
            }
        }

        return parts > 0;
    }

    private static PluginManifestResult Validate(string directory, JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidJson, $"{FileName} must contain a JSON object");
        }

        var root = document.RootElement;

        if (!TryGetString(root, "id", out var id))
        {
            return PluginManifestResult.Fail(PluginManifestError.MissingId, $"{FileName} is missing a string \"id\"");
        }

        if (!IsValidId(id))
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidId, $"Plugin id '{id}' must be 1..{MaxIdLength} ASCII letters, digits, '.', '_' or '-', starting and ending alphanumeric");
        }

        if (!TryGetString(root, "name", out var name))
        {
            return PluginManifestResult.Fail(PluginManifestError.MissingName, $"{FileName} is missing a string \"name\"");
        }

        if (!TryGetString(root, "version", out var version))
        {
            return PluginManifestResult.Fail(PluginManifestError.MissingVersion, $"{FileName} is missing a string \"version\"");
        }

        if (!IsValidVersion(version))
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidVersion, $"Plugin version '{version}' is not a numeric version");
        }

        if (!TryGetString(root, "entryAssembly", out var entryAssembly))
        {
            return PluginManifestResult.Fail(PluginManifestError.MissingEntryAssembly, $"{FileName} is missing a string \"entryAssembly\"");
        }

        if (!TryGetString(root, "entryType", out var entryType))
        {
            return PluginManifestResult.Fail(PluginManifestError.MissingEntryType, $"{FileName} is missing a string \"entryType\"");
        }

        if (entryType.Length > MaxTypeNameLength)
        {
            return PluginManifestResult.Fail(PluginManifestError.InvalidEntryType, $"\u0022entryType\u0022 exceeds {MaxTypeNameLength} characters");
        }

        var assemblyPath = ResolveEntryAssembly(directory, entryAssembly);
        if (assemblyPath is null)
        {
            return PluginManifestResult.Fail(PluginManifestError.EntryAssemblyOutsidePluginDirectory, $"Entry assembly '{entryAssembly}' must stay inside {directory}");
        }

        if (!File.Exists(assemblyPath))
        {
            return PluginManifestResult.Fail(PluginManifestError.EntryAssemblyNotFound, $"Entry assembly not found: {assemblyPath}");
        }

        var description = TryGetString(root, "description", out var text) ? text : null;
        return PluginManifestResult.Ok(new PluginManifest(id, name, version, entryAssembly, entryType, description));
    }

    /// <summary>Resolves the entry assembly inside <paramref name="directory"/>, or null when the path escapes it.</summary>
    public static string? ResolveEntryAssembly(string directory, string entryAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (string.IsNullOrWhiteSpace(entryAssembly) || Path.IsPathRooted(entryAssembly))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(directory, entryAssembly));
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), full);
        if (relative.Length == 0 || Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return full;
    }

    private static bool TryGetString(JsonElement root, string property, out string value)
    {
        if (root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsAsciiIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private static bool IsNumeric(ReadOnlySpan<char> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreValidIdentifiers(ReadOnlySpan<char> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (var range in span.Split('.'))
        {
            var part = span[range];
            if (part.Length == 0)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
