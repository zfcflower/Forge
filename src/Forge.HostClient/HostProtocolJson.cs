using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Ipc;
using AbstractionsToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>Main program → Host <c>plugin.execute</c>. The legacy fields stay for compatibility with direct
/// callers and older tests; new code always carries <c>context</c> (bound by this client) and
/// <c>timeoutMilliseconds</c>.</summary>
internal sealed record PluginExecuteArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("args")] JsonElement Args,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("workingDir")] string WorkingDirectory,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("context")] Sdk.ExtensionCallContext? Context,
    [property: JsonPropertyName("timeoutMilliseconds")] int? TimeoutMilliseconds);

internal sealed record PluginApproveArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("dependencyHash")] string DependencyHash,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion);

internal sealed record PluginRevokeArgs(
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version);

internal sealed record PluginIdArgs(
    [property: JsonPropertyName("pluginId")] string? PluginId);

internal sealed record HostToolCallOutput(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);

/// <summary>A decoded <c>host.tool_call</c>: only a run-bound call (<c>{runId, pluginId, name, args}</c>) is
/// accepted. Worker-supplied <c>sessionId</c>/<c>workingDir</c>/<c>depth</c> are never read.</summary>
internal readonly record struct BoundToolCallRequest(
    string RunId, string PluginId, string Name, string ArgumentsJson);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PluginExecuteArgs))]
[JsonSerializable(typeof(PluginApproveArgs))]
[JsonSerializable(typeof(PluginRevokeArgs))]
[JsonSerializable(typeof(PluginIdArgs))]
[JsonSerializable(typeof(HostToolCallOutput))]
[JsonSerializable(typeof(Sdk.ExtensionCallContext))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class HostClientJsonContext : JsonSerializerContext
{
}

/// <summary>Encodes and decodes the protocol payloads of <see cref="Forge.Plugin.Sdk.ExtensionProtocol"/>.
/// Payloads shared with the SDK go through its source-generated <see cref="Sdk.ExtensionExecutionJsonContext"/>
/// (camelCase); nothing here reflects over a payload type.</summary>
internal static class HostProtocolJson
{
    public static JsonElement SerializePluginExecute(
        string pluginId,
        string tool,
        JsonElement arguments,
        string sessionId,
        string workingDirectory,
        int depth,
        Sdk.ExtensionCallContext? context,
        int? timeoutMilliseconds)
    {
        var payload = new PluginExecuteArgs(
            pluginId,
            tool,
            arguments,
            sessionId ?? string.Empty,
            workingDirectory ?? string.Empty,
            depth,
            context,
            timeoutMilliseconds);
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

    public static JsonElement SerializeCodeExecute(Sdk.CodeExecutionRequest request) =>
        JsonSerializer.SerializeToElement(request, Sdk.ExtensionExecutionJsonContext.Default.CodeExecutionRequest);

    public static JsonElement SerializeCommandExecute(Sdk.CommandExecutionRequest request) =>
        JsonSerializer.SerializeToElement(request, Sdk.ExtensionExecutionJsonContext.Default.CommandExecutionRequest);

    public static JsonElement SerializePromptExecute(Sdk.PromptExecutionRequest request) =>
        JsonSerializer.SerializeToElement(request, Sdk.ExtensionExecutionJsonContext.Default.PromptExecutionRequest);

    /// <summary>The approval record of a command carries the command's raw argument text as a JSON string.</summary>
    public static string SerializeText(string text) =>
        JsonSerializer.Serialize(text ?? string.Empty, HostClientJsonContext.Default.String);

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
        JsonElement? payload, out string pluginId, out IReadOnlyList<Sdk.ToolDeclaration> tools)
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

        var parsed = new List<Sdk.ToolDeclaration>(list.GetArrayLength());
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

    private static Sdk.ToolDeclaration? ReadDeclaration(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        var schema = ReadString(entry, "inputSchemaJson");
        return new Sdk.ToolDeclaration(
            name,
            ReadString(entry, "description") ?? string.Empty,
            string.IsNullOrWhiteSpace(schema) ? "{}" : schema,
            ReadSideEffect(entry.TryGetProperty("sideEffect", out var sideEffect) ? sideEffect : default));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryReadToolsUnregister(JsonElement? payload, out string pluginId) =>
        TryReadPluginId(payload, out pluginId);

    /// <summary>Reads <c>{pluginId}</c>, the payload of <c>tools.unregister</c> and <c>contributions.unregister</c>.</summary>
    public static bool TryReadPluginId(JsonElement? payload, out string pluginId)
    {
        pluginId = string.Empty;
        var message = Deserialize(payload, HostClientJsonContext.Default.PluginIdArgs);
        if (message?.PluginId is not { Length: > 0 } id)
        {
            return false;
        }

        pluginId = id;
        return true;
    }

    /// <summary>Reads a <c>contributions.register</c> notification: the complete contribution set of one plugin.</summary>
    public static bool TryReadContributionsRegister(
        JsonElement? payload, out string pluginId, out Sdk.PluginContributionSet contributions, out string problem)
    {
        pluginId = string.Empty;
        contributions = new Sdk.PluginContributionSet();
        problem = string.Empty;
        if (payload is not { ValueKind: JsonValueKind.Object } message)
        {
            problem = "the payload is not a JSON object.";
            return false;
        }

        if (!message.TryGetProperty("pluginId", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
            idElement.GetString() is not { Length: > 0 } id)
        {
            problem = "the payload carries no plugin id.";
            return false;
        }

        pluginId = id;
        var notification = Deserialize(message, Sdk.ExtensionExecutionJsonContext.Default.PluginContributionsNotification);
        if (notification is null)
        {
            problem = $"the contribution set of plugin '{id}' cannot be decoded.";
            return false;
        }

        contributions = notification.Contributions ?? new Sdk.PluginContributionSet();
        return true;
    }

    /// <summary>Reads a run-bound <c>host.tool_call</c>. A call without a <c>runId</c> is refused instead of
    /// being executed unsolicited; the legacy <c>sessionId</c>/<c>workingDir</c>/<c>depth</c> fields are ignored.</summary>
    public static bool TryReadBoundToolCall(
        JsonElement? payload, out BoundToolCallRequest call, out string problem)
    {
        call = default;
        problem = string.Empty;
        if (payload is not { ValueKind: JsonValueKind.Object } message)
        {
            problem = "The host.tool_call request has no arguments.";
            return false;
        }

        if (!message.TryGetProperty("runId", out var runIdElement) || runIdElement.ValueKind == JsonValueKind.Null)
        {
            problem = HasLegacyContext(message)
                ? "The host.tool_call request is not bound to a run id; unbound tool calls are refused. " +
                  "The Extension Host must forward {runId, pluginId, name, args} for an execution that is in flight."
                : "The host.tool_call request is not bound to a run id; unbound tool calls are refused.";
            return false;
        }

        if (runIdElement.ValueKind != JsonValueKind.String || runIdElement.GetString() is not { Length: > 0 } runId)
        {
            problem = "The host.tool_call request carries a blank or non-string run id; it was refused.";
            return false;
        }

        if (!message.TryGetProperty("pluginId", out var pluginElement) || pluginElement.ValueKind != JsonValueKind.String ||
            pluginElement.GetString() is not { Length: > 0 } pluginId)
        {
            problem = "The host.tool_call request carries no plugin identity; it was refused.";
            return false;
        }

        if (!message.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { Length: > 0 } name)
        {
            problem = "The host.tool_call request has no tool name.";
            return false;
        }

        call = new BoundToolCallRequest(
            runId,
            pluginId,
            name,
            NormalizeArguments(message.TryGetProperty("args", out var arguments) ? arguments : default));
        return true;
    }

    private static bool HasLegacyContext(JsonElement message) =>
        message.TryGetProperty("sessionId", out _) ||
        message.TryGetProperty("workingDir", out _) ||
        message.TryGetProperty("depth", out _);

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

    public static bool TryReadCodeExecutionResult(
        JsonElement? payload, [NotNullWhen(true)] out Sdk.CodeExecutionResult? result, out string problem)
    {
        result = Deserialize(payload, Sdk.ExtensionExecutionJsonContext.Default.CodeExecutionResult);
        problem = string.Empty;
        if (result is null)
        {
            problem = "The Extension Host returned no script result.";
            return false;
        }

        result = result with { Content = result.Content ?? string.Empty };
        return true;
    }

    public static bool TryReadCommandOutput(
        JsonElement? payload, [NotNullWhen(true)] out Sdk.CommandOutput? output, out string problem)
    {
        output = Deserialize(payload, Sdk.ExtensionExecutionJsonContext.Default.CommandOutput);
        problem = string.Empty;
        if (output is null)
        {
            problem = "The Extension Host returned no command result.";
            return false;
        }

        return true;
    }

    public static bool TryReadPromptOutput(
        JsonElement? payload, [NotNullWhen(true)] out Sdk.PromptOutput? output, out string problem)
    {
        output = Deserialize(payload, Sdk.ExtensionExecutionJsonContext.Default.PromptOutput);
        problem = string.Empty;
        if (output is null)
        {
            problem = "The Extension Host returned no prompt result.";
            return false;
        }

        return true;
    }

    /// <summary>Reads a <c>plugins.status</c> response: every row of <c>plugins</c> is validated on its own, and one
    /// unusable row makes the whole answer unusable instead of shortening the registry.</summary>
    public static bool TryReadPluginStatuses(
        JsonElement? payload, out IReadOnlyList<Sdk.PluginStatusInfo> plugins, out string problem)
    {
        plugins = [];
        problem = string.Empty;
        if (payload is not { ValueKind: JsonValueKind.Object } response ||
            !response.TryGetProperty("plugins", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            problem = "The Extension Host returned no plugin status response.";
            return false;
        }

        var reported = new List<Sdk.PluginStatusInfo>(list.GetArrayLength());
        var index = 0;
        foreach (var row in list.EnumerateArray())
        {
            if (!TryReadPluginStatus(row, $"plugins[{index}]", out var status, out problem))
            {
                return false;
            }

            reported.Add(status);
            index++;
        }

        plugins = reported;
        return true;
    }

    /// <summary>Validates one status row. <paramref name="label"/> names the row for the caller
    /// (<c>plugins[3]</c> in a registry answer, <c>status</c> inside a commit result) so the same rule set can be
    /// shared without losing the ability to locate the bad row.</summary>
    public static bool TryReadPluginStatus(
        JsonElement row, string label, out Sdk.PluginStatusInfo status, out string problem)
    {
        status = null!;
        problem = string.Empty;
        if (row.ValueKind != JsonValueKind.Object)
        {
            problem = $"{label} is {DescribeJson(row)} instead of a plugin status object.";
            return false;
        }

        Sdk.PluginStatusInfo? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize(row, Sdk.ExtensionExecutionJsonContext.Default.PluginStatusInfo);
        }
        catch (JsonException ex)
        {
            problem = $"{label} cannot be decoded: {ex.Message}";
            return false;
        }

        if (decoded is null)
        {
            problem = $"{label} cannot be decoded as a plugin status.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(decoded.PluginId))
        {
            problem = $"{label} has no plugin id.";
            return false;
        }

        var identity = $"{label} ('{decoded.PluginId}')";
        if (string.IsNullOrWhiteSpace(decoded.Status))
        {
            problem = $"{identity} has no status.";
            return false;
        }

        if (!IsInvalidStatus(decoded.Status))
        {
            if (string.IsNullOrWhiteSpace(decoded.Version))
            {
                problem = $"{identity} has no version.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded.SourceHash))
            {
                problem = $"{identity} has no source hash.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded.DependencyHash))
            {
                problem = $"{identity} has no dependency hash.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(decoded.SdkVersion))
            {
                problem = $"{identity} has no SDK version.";
                return false;
            }
        }

        if (!TryReadPermissions(row, decoded.Permissions, identity, out problem))
        {
            return false;
        }

        status = decoded with
        {
            Version = decoded.Version ?? string.Empty,
            SourceHash = decoded.SourceHash ?? string.Empty,
            DependencyHash = decoded.DependencyHash ?? string.Empty,
            SdkVersion = decoded.SdkVersion ?? string.Empty,
        };
        return true;
    }

    /// <summary>Checks a permission array against both the raw JSON and the decoded list, for any payload that carries
    /// <c>permissions</c> (<c>plugins.status</c> rows and staged extensions alike).</summary>
    public static bool TryReadPermissions(
        JsonElement row, IReadOnlyList<Sdk.Permission>? permissions, string label, out string problem)
    {
        problem = string.Empty;
        if (!row.TryGetProperty("permissions", out var list) || list.ValueKind != JsonValueKind.Array ||
            permissions is null)
        {
            problem = $"{label} has no permission list.";
            return false;
        }

        var position = 0;
        foreach (var item in list.EnumerateArray())
        {
            Sdk.Permission? permission = position < permissions.Count ? permissions[position] : null;
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out _) ||
                permission is null || !Enum.IsDefined(permission.Kind))
            {
                problem = $"{label} has an unusable permission at permissions[{position}].";
                return false;
            }

            if (permission.Scope is { } scope && string.IsNullOrWhiteSpace(scope))
            {
                problem = $"{label} has a blank scope at permissions[{position}] ({permission.Kind}).";
                return false;
            }

            position++;
        }

        return true;
    }

    private static bool IsInvalidStatus(string status) =>
        string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase);

    public static string DescribeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "an unusable value",
    };

    public static AbstractionsToolSideEffect ToAbstractionsSideEffect(Sdk.ToolSideEffect sideEffect) => sideEffect switch
    {
        Sdk.ToolSideEffect.ReadOnly => AbstractionsToolSideEffect.ReadOnly,
        Sdk.ToolSideEffect.Write => AbstractionsToolSideEffect.Write,
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
    private static Sdk.ToolSideEffect ReadSideEffect(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var ordinal))
        {
            return ordinal switch
            {
                0 => Sdk.ToolSideEffect.ReadOnly,
                1 => Sdk.ToolSideEffect.Write,
                2 => Sdk.ToolSideEffect.Execute,
                _ => Sdk.ToolSideEffect.Execute,
            };
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } name &&
            Enum.TryParse<Sdk.ToolSideEffect>(name, ignoreCase: true, out var named) &&
            Enum.IsDefined(named))
        {
            return named;
        }

        return Sdk.ToolSideEffect.Execute;
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
