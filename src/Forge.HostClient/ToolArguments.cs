using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Forge.HostClient;

/// <summary>Tool arguments at the main-program boundary. On the wire they are raw UTF-8 JSON bytes (empty means
/// <c>{}</c>); the main program's tools and approval path take them as text.</summary>
internal static class ToolArguments
{
    /// <summary>Takes the UTF-8 bytes of a tool call's arguments once and checks that they are one JSON value, without
    /// building a document. Blank text is the empty payload.</summary>
    public static bool TryGetUtf8(string? json, out ReadOnlyMemory<byte> utf8)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            utf8 = ReadOnlyMemory<byte>.Empty;
            return true;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
            }
        }
        catch (JsonException)
        {
            utf8 = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        utf8 = bytes;
        return true;
    }

    /// <summary>The text a main-program tool receives for wire arguments; the empty payload is <c>{}</c>.</summary>
    public static string ToText(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return "{}";
        }

        var text = Encoding.UTF8.GetString(utf8.Span);
        return string.IsNullOrWhiteSpace(text) ? "{}" : text;
    }

    /// <summary>A JSON string literal of <paramref name="text"/>: the approval record of a plugin command carries the
    /// command's raw argument text this way.</summary>
    public static string JsonString(string? text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text ?? string.Empty);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
