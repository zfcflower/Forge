using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Ipc;
using AbstractionsToolSideEffect = Forge.Abstractions.ToolSideEffect;
using SdkToolDeclaration = Forge.Plugin.Sdk.ToolDeclaration;
using SdkToolSideEffect = Forge.Plugin.Sdk.ToolSideEffect;

namespace Forge.HostClient;

/// <summary>Main program → Host request payloads; field names are those documented on
/// <see cref="Forge.Plugin.Sdk.ExtensionProtocol"/>.</summary>
internal sealed record PluginExecuteArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("args")] JsonElement Args,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("workingDir")] string WorkingDirectory,
    [property: JsonPropertyName("depth")] int Depth);

internal sealed record PluginApproveArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("dependencyHash")] string DependencyHash,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion);

internal sealed record PluginRevokeArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Host → main program: a plugin calls a main-program tool. <c>args</c> is read tolerantly
/// (object, or a JSON string holding the object); <c>workingDir</c>/<c>sessionId</c>/<c>depth</c> are the
/// originating invocation's context and <c>workingDir</c> must be a fully qualified path.</summary>
internal sealed record HostToolCallArgs(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("args")] JsonElement Args,
    [property: JsonPropertyName("workingDir")] string? WorkingDirectory,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("depth")] int Depth);

/// <summary>A decoded <c>host.tool_call</c>. <see cref="WorkingDirectoryProvided"/> separates an absent
/// <c>workingDir</c> (an old host, the only case that may fall back) from one that is present but blank or not
/// fully qualified.</summary>
internal readonly record struct HostToolCallRequest(
    string Name,
    string ArgumentsJson,
    string? WorkingDirectory,
    bool WorkingDirectoryProvided,
    string SessionId,
    int Depth);

internal sealed record HostToolCallOutput(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);

internal sealed record ToolsUnregisterArgs(
    [property: JsonPropertyName("pluginId")] string? PluginId);

[JsonSerializable(typeof(PluginExecuteArgs))]
[JsonSerializable(typeof(PluginApproveArgs))]
[JsonSerializable(typeof(PluginRevokeArgs))]
[JsonSerializable(typeof(HostToolCallArgs))]
[JsonSerializable(typeof(HostToolCallOutput))]
[JsonSerializable(typeof(ToolsUnregisterArgs))]
internal sealed partial class HostClientJsonContext : JsonSerializerContext
{
}

/// <summary>Encodes and decodes the protocol payloads of <see cref="Forge.Plugin.Sdk.ExtensionProtocol"/>.</summary>
internal static class HostProtocolJson
{
    public static JsonElement SerializePluginExecute(
        string pluginId, string tool, JsonElement arguments, string sessionId, string workingDirectory, int depth)
    {
        var payload = new PluginExecuteArgs(
            pluginId, tool, arguments, sessionId ?? string.Empty, workingDirectory ?? string.Empty, depth);
        return JsonSerializer.SerializeToElement(payload, HostClientJsonContext.Default.PluginExecuteArgs);
    }

    public static JsonElement SerializeApprove(
        string pluginId, string version, string sourceHash, string dependencyHash, string sdkVersion) =>
        JsonSerializer.SerializeToElement(
            new PluginApproveArgs(pluginId, version, sourceHash, dependencyHash, sdkVersion),
            HostClientJsonContext.Default.PluginApproveArgs);

    public static JsonElement SerializeRevoke(string pluginId, string version) =>
        JsonSerializer.SerializeToElement(
            new PluginRevokeArgs(pluginId, version), HostClientJsonContext.Default.PluginRevokeArgs);

    public static JsonElement SerializeHostToolOutput(string content, bool isError) =>
        JsonSerializer.SerializeToElement(
            new HostToolCallOutput(content ?? string.Empty, isError),
            HostClientJsonContext.Default.HostToolCallOutput);

    /// <summary>Parses the arguments of a tool call into a JSON value; empty means <c>{}</c>.</summary>
    public static bool TryParseArguments(string? json, out JsonElement arguments)
    {
        arguments = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            arguments = IpcJsonTypes.EmptyObject;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads <c>{pluginId, tools}</c>; every declaration is decoded on its own so one invalid entry is
    /// dropped instead of failing the whole message.</summary>
    public static bool TryReadToolsRegister(
        JsonElement? payload, out string pluginId, out IReadOnlyList<SdkToolDeclaration> tools)
    {
        pluginId = string.Empty;
        tools = [];
        if (payload is not { ValueKind: JsonValueKind.Object } message)
        {
            return false;
        }

        if (!message.TryGetProperty("pluginId", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
            idElement.GetString() is not { Length: > 0 } id)
        {
            return false;
        }

        pluginId = id;
        if (!message.TryGetProperty("tools", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            tools = [];
            return true;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<SdkToolDeclaration>(list.GetArrayLength());
        foreach (var entry in list.EnumerateArray())
        {
            if (ReadDeclaration(entry) is { } declaration)
            {
                parsed.Add(declaration);
            }
        }

        tools = parsed;
        return true;
    }

    private static SdkToolDeclaration? ReadDeclaration(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        var schema = ReadString(entry, "inputSchemaJson");
        return new SdkToolDeclaration(
            name,
            ReadString(entry, "description") ?? string.Empty,
            string.IsNullOrWhiteSpace(schema) ? "{}" : schema,
            ReadSideEffect(entry.TryGetProperty("sideEffect", out var sideEffect) ? sideEffect : default));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryReadToolsUnregister(JsonElement? payload, out string pluginId)
    {
        pluginId = string.Empty;
        var message = Deserialize<ToolsUnregisterArgs>(payload, HostClientJsonContext.Default.ToolsUnregisterArgs);
        if (message?.PluginId is not { Length: > 0 } id)
        {
            return false;
        }

        pluginId = id;
        return true;
    }

    /// <summary>Reads a <c>host.tool_call</c> request; the arguments JSON is normalized to a text form and
    /// <c>workingDir</c> is returned verbatim, so the caller can reject a blank value instead of defaulting it.</summary>
    public static bool TryReadHostToolCall(JsonElement? payload, out HostToolCallRequest call)
    {
        call = default;
        var message = Deserialize<HostToolCallArgs>(payload, HostClientJsonContext.Default.HostToolCallArgs);
        if (message?.Name is not { Length: > 0 } toolName)
        {
            return false;
        }

        call = new HostToolCallRequest(
            toolName,
            NormalizeArguments(message.Args),
            message.WorkingDirectory,
            payload.HasValue && payload.Value.TryGetProperty("workingDir", out _),
            message.SessionId ?? string.Empty,
            Math.Max(0, message.Depth));
        return true;
    }

    /// <summary>Reads the <c>{content, isError}</c> response of <c>plugin.execute</c> field by field; a field of the
    /// wrong JSON type makes the whole result an error instead of a successful empty result.</summary>
    public static bool TryReadToolOutput(JsonElement? result, out string content, out bool isError)
    {
        const string Undecodable = "The Extension Host returned a tool result that cannot be decoded.";
        content = string.Empty;
        isError = true;
        if (result is not { ValueKind: JsonValueKind.Object } element)
        {
            content = "The Extension Host returned no tool result.";
            return false;
        }

        if (element.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                content = Undecodable;
                return false;
            }

            content = contentElement.GetString() ?? string.Empty;
        }

        var reportedError = false;
        if (element.TryGetProperty("isError", out var errorElement))
        {
            if (errorElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                content = Undecodable;
                return false;
            }

            reportedError = errorElement.GetBoolean();
        }

        isError = reportedError;
        return true;
    }

    public static AbstractionsToolSideEffect ToAbstractionsSideEffect(SdkToolSideEffect sideEffect) => sideEffect switch
    {
        SdkToolSideEffect.ReadOnly => AbstractionsToolSideEffect.ReadOnly,
        SdkToolSideEffect.Write => AbstractionsToolSideEffect.Write,
        _ => AbstractionsToolSideEffect.Execute,
    };

    private static string NormalizeArguments(JsonElement arguments)
    {
        switch (arguments.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return arguments.GetRawText();
            case JsonValueKind.String:
                var text = arguments.GetString();
                return string.IsNullOrWhiteSpace(text) ? "{}" : TryParseArguments(text, out var parsed) ? parsed.GetRawText() : "{}";
            default:
                return "{}";
        }
    }

    /// <summary>Accepts a numeric ordinal (the STJ default for enums) or a named value; anything else is
    /// treated as the most privileged side effect.</summary>
    private static SdkToolSideEffect ReadSideEffect(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var ordinal))
        {
            return ordinal switch
            {
                0 => SdkToolSideEffect.ReadOnly,
                1 => SdkToolSideEffect.Write,
                2 => SdkToolSideEffect.Execute,
                _ => SdkToolSideEffect.Execute,
            };
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } name &&
            Enum.TryParse<SdkToolSideEffect>(name, ignoreCase: true, out var named) &&
            Enum.IsDefined(named))
        {
            return named;
        }

        return SdkToolSideEffect.Execute;
    }

    private static T? Deserialize<T>(JsonElement? payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(element, info);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
