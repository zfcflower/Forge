using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

internal static class ProviderJson
{
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>Serialises with a fixed property order so identical requests stay byte-identical.</summary>
    internal static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static void WriteRaw(Utf8JsonWriter writer, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        writer.WriteRawValue(json, skipInputValidation: false);
    }

    internal static string? Text(IReadOnlyList<ContentBlock> blocks)
    {
        System.Text.StringBuilder? builder = null;
        foreach (var block in blocks)
        {
            var text = block switch
            {
                TextBlock t => t.Text,
                ArtifactRefBlock a => RenderArtifact(a),
                _ => null,
            };
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder is null)
            {
                builder = new System.Text.StringBuilder();
            }
            else
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder?.ToString();
    }

    /// <summary>Whether any message in the range carries an image or a file, which needs the content-array form.</summary>
    internal static bool HasMedia(IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        for (var i = start; i <= end; i++)
        {
            if (HasMedia(messages[i].Content))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasMedia(IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is ImageBlock or FileBlock)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>data:</c> URL both OpenAI protocols take for an inline image.</summary>
    internal static string DataUrl(ImageBlock image) => string.Concat("data:", image.MediaType, ";base64,", image.Data);

    internal static string RenderArtifact(ArtifactRefBlock artifact) =>
        $"[artifact {artifact.ArtifactId}] {artifact.Description} ({artifact.SizeBytes} bytes)";

    internal static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
