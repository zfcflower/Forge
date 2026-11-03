using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Ipc;

/// <summary>Copies an already parsed <see cref="JsonElement"/> to the wire as its original JSON text.</summary>
internal sealed class RawJsonElementConverter : JsonConverter<JsonElement>
{
    public static RawJsonElementConverter Instance { get; } = new();

    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
}

/// <summary>Reads an absent JSON property as <c>null</c> and a present JSON <c>null</c> as a
/// <see cref="JsonValueKind.Null"/> element so the two remain distinguishable.</summary>
internal sealed class JsonElementPresenceConverter : JsonConverter<JsonElement?>
{
    public static JsonElementPresenceConverter Instance { get; } = new();

    public override bool HandleNull => true;

    public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
    {
        if (value is not { } element || element.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
    }
}

/// <summary>Write-side context: the contract shapes plus raw <see cref="JsonElement"/> payloads.</summary>
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(IpcError))]
internal partial class IpcWireJsonContext : JsonSerializerContext
{
    private static readonly JsonSerializerOptions WriteOptions = CreateWriteOptions();

    public static IpcWireJsonContext Wire { get; } = new(WriteOptions);

    private static JsonSerializerOptions CreateWriteOptions()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(RawJsonElementConverter.Instance);
        return options;
    }
}
