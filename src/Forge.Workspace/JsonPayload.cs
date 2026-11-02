using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Forge.Workspace;

/// <summary>
/// Reflection-free JSON writer for tool payloads: nested dictionaries, sequences and the BCL primitives only.
/// Anonymous types are unsupported by design, because a source-generated
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> cannot name them, and reflection-based
/// serialization fails outright under Native AOT. Anything outside the supported shapes throws instead of
/// silently falling back to reflection.
/// </summary>
internal static class JsonPayload
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object? payload)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            Write(writer, payload);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case bool flag:
                writer.WriteBooleanValue(flag);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }

                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName((string)entry.Key);
                    Write(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                throw new NotSupportedException(
                    $"'{value.GetType()}' cannot be written as a tool payload; use dictionaries, sequences and primitives.");
        }
    }
}
