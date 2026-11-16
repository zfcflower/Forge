using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace Forge.App.Services;

/// <summary>One installable package of an update channel, keyed by runtime identifier (RID).</summary>
internal sealed record UpdatePackage(string Url, string Sha256, long SizeBytes);

/// <summary>One release channel: the version it offers, when it was published, the Markdown notes and the per-RID packages.</summary>
internal sealed record UpdateChannel(
    string Version,
    string ReleasedAt,
    string Notes,
    IReadOnlyDictionary<string, UpdatePackage> Packages)
{
    public UpdatePackage? Package(string rid) => Packages.TryGetValue(rid, out var package) ? package : null;
}

/// <summary>The parsed <c>updates.json</c>; <see cref="UpdateManifestParser.TryParse"/> produces it.</summary>
internal sealed record UpdateManifest(IReadOnlyDictionary<string, UpdateChannel> Channels)
{
    public UpdateChannel? Channel(string name) => Channels.TryGetValue(name, out var channel) ? channel : null;
}

/// <summary>
/// Reads the <c>updates.json</c> manifest (schema 1, frozen in <c>src/Forge.App/SPEC.md</c>「自动更新」). It is someone else's
/// file shape, so parsing goes through <see cref="JsonDocument"/> with no reflection and no source-generated model; the
/// schema number is the compatibility gate, and every field is read defensively with a readable error instead of a
/// half-populated channel.
/// </summary>
internal static class UpdateManifestParser
{
    /// <summary>A manifest is a few channels of release notes and hashes; a hostile one must not become an in-memory tree.</summary>
    public const long MaxBytes = 1024 * 1024;

    public const int Schema = 1;

    public static bool TryParse(
        ReadOnlyMemory<byte> utf8,
        [NotNullWhen(true)] out UpdateManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;

        if (utf8.IsEmpty)
        {
            error = "the manifest response was empty.";
            return false;
        }

        if (utf8.Length > MaxBytes)
        {
            error = "the manifest response is larger than " + MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            error = "the manifest response is not valid JSON: " + exception.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the manifest must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("schema", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schema) ||
                schema != Schema)
            {
                error = "the manifest schema is missing or not " + Schema.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            if (!root.TryGetProperty("channels", out var channelsElement) || channelsElement.ValueKind != JsonValueKind.Object)
            {
                error = "the manifest must hold a channels object.";
                return false;
            }

            var channels = new Dictionary<string, UpdateChannel>(StringComparer.Ordinal);
            foreach (var channelProperty in channelsElement.EnumerateObject())
            {
                if (channelProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    error = "channel '" + channelProperty.Name + "' must be a JSON object.";
                    return false;
                }

                if (!TryParseChannel(channelProperty.Name, channelProperty.Value, out var channel, out error))
                {
                    return false;
                }

                channels[channelProperty.Name] = channel;
            }

            manifest = new UpdateManifest(channels);
            return true;
        }
    }

    private static bool TryParseChannel(
        string name,
        JsonElement element,
        [NotNullWhen(true)] out UpdateChannel? channel,
        out string? error)
    {
        channel = null;
        error = null;

        if (!element.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(versionElement.GetString()))
        {
            error = "channel '" + name + "' must declare a version string.";
            return false;
        }

        var version = versionElement.GetString()!;
        var releasedAt = ReadOptionalString(element, "releasedAt");
        var notes = ReadOptionalString(element, "notes");

        if (!element.TryGetProperty("packages", out var packagesElement) || packagesElement.ValueKind != JsonValueKind.Object)
        {
            error = "channel '" + name + "' must hold a packages object.";
            return false;
        }

        var packages = new Dictionary<string, UpdatePackage>(StringComparer.Ordinal);
        foreach (var packageProperty in packagesElement.EnumerateObject())
        {
            if (packageProperty.Value.ValueKind != JsonValueKind.Object)
            {
                error = "package '" + packageProperty.Name + "' of channel '" + name + "' must be a JSON object.";
                return false;
            }

            if (!TryParsePackage(name, packageProperty.Name, packageProperty.Value, out var package, out error))
            {
                return false;
            }

            packages[packageProperty.Name] = package;
        }

        channel = new UpdateChannel(version, releasedAt, notes, packages);
        return true;
    }

    private static bool TryParsePackage(
        string channel,
        string rid,
        JsonElement element,
        [NotNullWhen(true)] out UpdatePackage? package,
        out string? error)
    {
        package = null;
        error = null;

        if (!element.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "package '" + rid + "' of channel '" + channel + "' must declare an absolute http(s) url.";
            return false;
        }

        if (!element.TryGetProperty("sha256", out var shaElement) ||
            shaElement.ValueKind != JsonValueKind.String ||
            !IsSha256(shaElement.GetString()))
        {
            error = "package '" + rid + "' of channel '" + channel + "' must declare a 64-character SHA-256 hash.";
            return false;
        }

        long sizeBytes = 0;
        if (element.TryGetProperty("sizeBytes", out var sizeElement))
        {
            if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out sizeBytes) || sizeBytes < 0)
            {
                error = "package '" + rid + "' of channel '" + channel + "' declares an invalid sizeBytes.";
                return false;
            }
        }

        package = new UpdatePackage(uri.AbsoluteUri, shaElement.GetString()!, sizeBytes);
        return true;
    }

    private static string ReadOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
