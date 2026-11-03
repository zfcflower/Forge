using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

internal sealed record WorkerLoadRequest
{
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("assemblyPath")]
    public string? AssemblyPath { get; init; }
}

internal sealed record WorkerExecuteRequest
{
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("args")]
    public JsonElement Arguments { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("workingDir")]
    public string? WorkingDir { get; init; }

    [JsonPropertyName("hostPipeName")]
    public string? HostPipeName { get; init; }
}

internal sealed record WorkerUnloadRequest
{
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }
}

internal sealed record WorkerLoadResult
{
    [JsonPropertyName("tools")]
    public ToolDeclaration[] Tools { get; init; } = [];
}

internal sealed record ToolResultPayload
{
    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

internal sealed record HostToolCallRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("args")]
    public JsonElement Arguments { get; init; }
}

/// <summary>Wire helpers for the worker operations: one source-generated context and the shared
/// <c>{}</c> payload used by replies that carry no data.</summary>
internal static class WorkerJson
{
    public static readonly JsonElement EmptyObject = CreateEmptyObject();

    /// <summary>An absent or JSON <c>null</c> payload becomes <c>{}</c> so plugins and the host never
    /// see an <see cref="JsonValueKind.Undefined"/> element.</summary>
    public static JsonElement ObjectOrEmpty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyObject : value;

    public static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToElement(value, info);

    public static T? TryDeserialize<T>(JsonElement? value, JsonTypeInfo<T> info, out string? error)
        where T : class
    {
        error = null;
        if (value is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "The request carries no arguments.";
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "The request arguments must be a JSON object.";
            return null;
        }

        try
        {
            var deserialized = element.Deserialize(info);
            if (deserialized is null)
            {
                error = "The request arguments decoded to null.";
            }

            return deserialized;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

[JsonSerializable(typeof(WorkerLoadRequest))]
[JsonSerializable(typeof(WorkerExecuteRequest))]
[JsonSerializable(typeof(WorkerUnloadRequest))]
[JsonSerializable(typeof(WorkerLoadResult))]
[JsonSerializable(typeof(ToolResultPayload))]
[JsonSerializable(typeof(HostToolCallRequest))]
internal partial class WorkerJsonContext : JsonSerializerContext;
