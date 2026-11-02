using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>Builds JSON-RPC 2.0 envelopes as UTF-8 bytes.</summary>
internal static class RpcJson
{
    internal const string Version = "2.0";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    public static byte[] Request(long id, string method, Action<Utf8JsonWriter>? writeParams) => Build(writer =>
    {
        writer.WriteString("jsonrpc", Version);
        writer.WriteNumber("id", id);
        writer.WriteString("method", method);
        WriteParams(writer, writeParams);
    });

    public static byte[] Notification(string method, Action<Utf8JsonWriter>? writeParams) => Build(writer =>
    {
        writer.WriteString("jsonrpc", Version);
        writer.WriteString("method", method);
        WriteParams(writer, writeParams);
    });

    public static byte[] Result(JsonElement id, string resultJson) => Build(writer =>
    {
        writer.WriteString("jsonrpc", Version);
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("result");
        JsonText.WriteCompact(writer, resultJson);
    });

    public static byte[] Error(JsonElement id, int code, string message) => Build(writer =>
    {
        writer.WriteString("jsonrpc", Version);
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    private static void WriteParams(Utf8JsonWriter writer, Action<Utf8JsonWriter>? writeParams)
    {
        if (writeParams is null)
        {
            return;
        }

        writer.WritePropertyName("params");
        writer.WriteStartObject();
        writeParams(writer);
        writer.WriteEndObject();
    }

    private static byte[] Build(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

internal static class JsonText
{
    /// <summary>
    /// Writes a pre-built JSON object into an enclosing document. Line-delimited stdio framing forbids
    /// embedded newlines, so formatted input is re-serialised compactly; already compact input is copied.
    /// </summary>
    public static void WriteCompact(Utf8JsonWriter writer, ReadOnlySpan<char> json)
    {
        if (json.IsWhiteSpace())
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        if (json.IndexOfAny('\n', '\r') < 0)
        {
            writer.WriteRawValue(json, skipInputValidation: false);
            return;
        }

        using var document = JsonDocument.Parse(json.ToString());
        document.RootElement.WriteTo(writer);
    }

    public static string Compact(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        if (json.AsSpan().IndexOfAny('\n', '\r') < 0)
        {
            return json;
        }

        var buffer = new ArrayBufferWriter<byte>(json.Length);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static byte[] ToUtf8(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
