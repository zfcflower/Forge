using System.Text.Json;
using System.Text.Json.Serialization;
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
    int Depth);

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

// Host ↔ worker.
internal sealed record WorkerLoadRequest(string PluginId, string Version, string AssemblyPath);

internal sealed record WorkerLoadResponse(IReadOnlyList<ToolDeclaration> Tools);

internal sealed record WorkerExecuteRequest(
    string PluginId,
    string Tool,
    JsonElement? Args,
    string SessionId,
    string WorkingDir,
    string HostPipeName);

internal sealed record ToolCallResult(string Content, bool IsError);
