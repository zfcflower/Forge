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
/// (object, or a JSON string holding the object); <c>workingDir</c>/<c>sessionId</c>/<c>depth</c> are
/// accepted when the host supplies them.</summary>
internal sealed record HostToolCallArgs(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("args")] JsonElement Args,
    [property: JsonPropertyName("workingDir")] string? WorkingDirectory,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("depth")] int Depth);

internal sealed record HostToolCallOutput(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);

/// <summary><c>{content, isError}</c> response of <c>plugin.execute</c>.</summary>
internal sealed record ToolOutput(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("isError")] bool IsError);

/// <summary>A <see cref="SdkToolDeclaration"/> as received from the host; <c>sideEffect</c> stays raw so a
/// numeric or a named value both decode.</summary>
internal sealed record ToolDeclarationWire(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("inputSchemaJson")] string? InputSchemaJson,
    [property: JsonPropertyName("sideEffect")] JsonElement SideEffect);

internal sealed record ToolsRegisterArgs(
    [property: JsonPropertyName("pluginId")] string? PluginId,
    [property: JsonPropertyName("tools")] ToolDeclarationWire[]? Tools);

internal sealed record ToolsUnregisterArgs(
    [property: JsonPropertyName("pluginId")] string? PluginId);

[JsonSerializable(typeof(PluginExecuteArgs))]
[JsonSerializable(typeof(PluginApproveArgs))]
[JsonSerializable(typeof(PluginRevokeArgs))]
[JsonSerializable(typeof(HostToolCallArgs))]
[JsonSerializable(typeof(HostToolCallOutput))]
[JsonSerializable(typeof(ToolOutput))]
[JsonSerializable(typeof(ToolsRegisterArgs))]
[JsonSerializable(typeof(ToolsUnregisterArgs))]
[JsonSerializable(typeof(ToolDeclarationWire))]
[JsonSerializable(typeof(ToolDeclarationWire[]))]
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

    /// <summary>Reads <c>{pluginId, tools}</c>; invalid entries are dropped rather than failing the whole message.</summary>
    public static bool TryReadToolsRegister(
        JsonElement? payload, out string pluginId, out IReadOnlyList<SdkToolDeclaration> tools)
    {
        pluginId = string.Empty;
        tools = [];
        var message = Deserialize<ToolsRegisterArgs>(payload, HostClientJsonContext.Default.ToolsRegisterArgs);
        if (message?.PluginId is not { Length: > 0 } id)
        {
            return false;
        }

        pluginId = id;
        if (message.Tools is not { Length: > 0 } declared)
        {
            tools = [];
            return true;
        }

        var parsed = new List<SdkToolDeclaration>(declared.Length);
        foreach (var wire in declared)
        {
            if (wire?.Name is not { Length: > 0 } name)
            {
                continue;
            }

            var schema = string.IsNullOrWhiteSpace(wire.InputSchemaJson) ? "{}" : wire.InputSchemaJson;
            parsed.Add(new SdkToolDeclaration(
                name,
                wire.Description ?? string.Empty,
                schema,
                ReadSideEffect(wire.SideEffect)));
        }

        tools = parsed;
        return true;
    }

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

    /// <summary>Reads a <c>host.tool_call</c> request; the arguments JSON is normalized to a text form.</summary>
    public static bool TryReadHostToolCall(
        JsonElement? payload,
        out string name,
        out string argumentsJson,
        out string? workingDirectory,
        out string sessionId,
        out int depth)
    {
        name = string.Empty;
        argumentsJson = "{}";
        workingDirectory = null;
        sessionId = string.Empty;
        depth = 0;

        var message = Deserialize<HostToolCallArgs>(payload, HostClientJsonContext.Default.HostToolCallArgs);
        if (message?.Name is not { Length: > 0 } toolName)
        {
            return false;
        }

        name = toolName;
        workingDirectory = string.IsNullOrWhiteSpace(message.WorkingDirectory) ? null : message.WorkingDirectory;
        sessionId = message.SessionId ?? string.Empty;
        depth = Math.Max(0, message.Depth);
        argumentsJson = NormalizeArguments(message.Args);
        return true;
    }

    /// <summary>Reads the <c>{content, isError}</c> response of <c>plugin.execute</c>.</summary>
    public static bool TryReadToolOutput(JsonElement? result, out string content, out bool isError)
    {
        content = string.Empty;
        isError = true;
        if (result is not { ValueKind: JsonValueKind.Object } element)
        {
            content = "The Extension Host returned no tool result.";
            return false;
        }

        var output = Deserialize<ToolOutput>(element, HostClientJsonContext.Default.ToolOutput);
        content = output?.Content ?? string.Empty;
        isError = output?.IsError ?? false;
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
