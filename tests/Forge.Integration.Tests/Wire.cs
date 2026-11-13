using System.Buffers;
using System.Text;
using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Builds a payload by hand, one MessagePack value at a time, for the protocol tests that must send something no
/// formatter would produce: a record with a member missing, a nil where the shape forbids one, a value of the wrong type.
/// Typed payloads go through <see cref="IpcPeer.RequestAsync{TArgs}"/> instead; this is only for the malformed cases.
/// Container headers only cover the small counts these tests need (at most 15 entries).
/// </summary>
internal sealed class WirePayload
{
    private readonly ArrayBufferWriter<byte> buffer = new();

    /// <summary>A map; pairs follow as <see cref="Key"/> and <see cref="Value{T}"/>.</summary>
    public static WirePayload Map(int entries) => new WirePayload().Header(0x80, entries);

    /// <summary>An array; items follow as <see cref="Value{T}"/> or <see cref="Nil"/>.</summary>
    public static WirePayload Array(int items) => new WirePayload().Header(0x90, items);

    /// <summary>A string key; encoded exactly the way a typed payload encodes one.</summary>
    public WirePayload Key(string name)
    {
        IpcSerializer.Serialize(buffer, name);
        return this;
    }

    /// <summary>A string value; null writes nil.</summary>
    public WirePayload Text(string? value)
    {
        IpcSerializer.Serialize(buffer, value);
        return this;
    }

    /// <summary>An empty array, written as its own header (an empty typed collection is not a root the source-generated
    /// chain serves, so it is built here rather than through <see cref="Value{T}"/>).</summary>
    public WirePayload EmptyArray() => Header(0x90, 0);

    public WirePayload Nil()
    {
        buffer.GetSpan(1)[0] = 0xc0;
        buffer.Advance(1);
        return this;
    }

    /// <summary>One value of the caller's own type, straight from the IPC serializer.</summary>
    public WirePayload Value<T>(T value)
    {
        IpcSerializer.Serialize(buffer, value);
        return this;
    }

    public byte[] ToArray() => buffer.WrittenSpan.ToArray();

    private WirePayload Header(byte fix, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 15);
        buffer.GetSpan(1)[0] = (byte)(fix | count);
        buffer.Advance(1);
        return this;
    }
}

/// <summary>Sends a hand-built payload. The bytes travel verbatim; the receiver decodes them like any other payload.</summary>
internal static class WirePeer
{
    public static Task<IpcMessage> AskRawAsync(
        this IpcPeer peer, string operation, WirePayload payload, CancellationToken ct) =>
        peer.RequestRawAsync(operation, payload.ToArray(), ct);
}

/// <summary>The typed request payloads the raw-host tests send, built in one place so every test binds the same way:
/// an identity is a <see cref="Sdk.ExtensionCallContext"/> the host validates, never loose fields.</summary>
internal static class WireRequests
{
    public static Sdk.ExtensionCallContext Context(
        string workingDirectory,
        string? runId = null,
        string sessionId = "raw",
        IReadOnlyList<string>? chain = null,
        string? rootRunId = null) =>
        new(
            runId ?? Guid.NewGuid().ToString("N"),
            sessionId,
            workingDirectory,
            chain?.Count ?? 0,
            chain ?? [])
        {
            RootRunId = rootRunId,
        };

    /// <summary>A bound <c>plugin.execute</c>; <paramref name="argsJson"/> is the model's own JSON text.</summary>
    public static Forge.Ipc.Protocol.PluginExecuteRequest Execute(
        string pluginId,
        string tool,
        string argsJson,
        string workingDirectory,
        int? timeoutMilliseconds = null) =>
        new(
            pluginId,
            tool,
            Encoding.UTF8.GetBytes(argsJson),
            Context(workingDirectory),
            timeoutMilliseconds);

    /// <summary>A bound <c>plugin.execute</c> under a caller-built context (a nested call, a duplicate run id).</summary>
    public static Forge.Ipc.Protocol.PluginExecuteRequest Execute(
        string pluginId,
        string tool,
        string argsJson,
        Sdk.ExtensionCallContext context,
        int? timeoutMilliseconds = null) =>
        new(pluginId, tool, Encoding.UTF8.GetBytes(argsJson), context, timeoutMilliseconds);

    public static Sdk.CommandExecutionRequest Command(
        string pluginId,
        string command,
        string arguments,
        string workingDirectory,
        int timeoutMilliseconds) =>
        new(pluginId, command, arguments, Context(workingDirectory), timeoutMilliseconds);

    public static Sdk.PromptExecutionRequest Prompt(
        string pluginId,
        string contributorId,
        string workingDirectory,
        int timeoutMilliseconds) =>
        new(pluginId, contributorId, Context(workingDirectory), timeoutMilliseconds);

    public static Forge.Ipc.Protocol.PluginApproveRequest Approve(
        string pluginId,
        string version,
        string sourceHash,
        string dependencyHash,
        string sdkVersion) =>
        new(pluginId, version, sourceHash, dependencyHash, sdkVersion);

    /// <summary>The tool list of one <c>tools.register</c> notification.</summary>
    public static (string PluginId, IReadOnlyList<Sdk.ToolDeclaration> Tools) Registered(IpcMessage register) =>
        register.Read<Forge.Ipc.Protocol.ToolsRegisterNotification>() is { } notification
            ? (notification.PluginId, notification.Tools)
            : throw new InvalidOperationException("the tools.register frame carried no payload.");

    public static (string Content, bool IsError) ToolResultOf(IpcMessage message) =>
        message.Read<Sdk.ToolResult>() is { } result
            ? (result.Content ?? string.Empty, result.IsError)
            : throw new InvalidOperationException("the answer carried no tool result.");

    public static (string Output, bool IsError) CommandResultOf(IpcMessage message) =>
        message.Read<Sdk.CommandOutput>() is { } output
            ? (output.Output ?? string.Empty, output.IsError)
            : throw new InvalidOperationException("the answer carried no command result.");
}
