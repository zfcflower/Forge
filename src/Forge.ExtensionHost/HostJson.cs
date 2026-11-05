using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Ipc;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>Serializer settings and message shapes for every payload carried by <see cref="Forge.Ipc.IpcMessage"/>.
/// Property names travel camelCase as documented by <see cref="ExtensionProtocol"/>; incoming payloads are matched
/// case-insensitively so a peer that writes PascalCase still binds.</summary>
internal static class HostJson
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonElement EmptyObject => Empty;

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Reads the arguments object of an inbound request; a request without one is refused, never defaulted.</summary>
    public static T Read<T>(IpcMessage message)
        where T : class
    {
        if (message.Arguments is not { ValueKind: JsonValueKind.Object } arguments)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }

        try
        {
            return arguments.Deserialize<T>(Options) ??
                throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }
        catch (JsonException exception)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' has unreadable arguments: {exception.Message}");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        return options;
    }
}

/// <summary>A request the host cannot serve. <see cref="Code"/> reaches the caller as the IPC error code.</summary>
internal sealed class HostOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Main program → host.
internal sealed record PluginExecuteRequest(
    string PluginId,
    string Tool,
    JsonElement? Args,
    string SessionId,
    string WorkingDir,
    int Depth,
    ExtensionCallContext? Context = null,
    int? TimeoutMilliseconds = null);

internal sealed record PluginApproveRequest(
    string PluginId,
    string Version,
    string? SourceHash,
    string? DependencyHash,
    string? SdkVersion);

internal sealed record PluginRevokeRequest(string PluginId, string Version);

// Host → main program.
internal sealed record PluginExecuteResponse(string Content, bool IsError);

internal sealed record ToolsRegisterNotification(string PluginId, IReadOnlyList<ToolDeclaration> Tools);

internal sealed record ToolsUnregisterNotification(string PluginId);

/// <summary>One <c>host.tool_call</c> relayed to the main program: the run id and plugin identity come from the host's
/// active-run table, never from the worker, and the worker-supplied session/directory/depth are not part of it.</summary>
internal sealed record HostToolCallForward(string RunId, string PluginId, string Name, JsonElement? Args);

// Worker → host.
internal sealed record WorkerBoundToolCall(string? RunId, string? Name, JsonElement? Args);

// Host ↔ worker.
internal sealed record WorkerLoadRequest(
    string PluginId,
    string Version,
    string AssemblyPath,
    string AssemblySha256,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<Permission> Permissions);

internal sealed record WorkerLoadResponse(
    IReadOnlyList<ToolDeclaration> Tools,
    PluginContributionSet? Contributions);

internal sealed record WorkerExecuteRequest(
    string PluginId,
    string Tool,
    JsonElement? Args,
    string SessionId,
    string WorkingDir,
    int Depth,
    string HostPipeName,
    ExtensionCallContext Context,
    int TimeoutMilliseconds);

internal sealed record ToolCallResult(string Content, bool IsError);
