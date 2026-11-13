using Forge.Plugin.Sdk;
using MessagePack;

namespace Forge.ScriptRuntime;

/// <summary>
/// Ipc operations between the Extension Host and one single-use script executor process. The executor serves exactly
/// one image and one run, then the host kills and waits for its process tree; nothing here is ever reused.
/// Inner tool calls leave the executor through the existing <c>host.tool_call</c> operation, bound by the host to the
/// run id exactly as for plugin workers. Frames are <c>Forge.Ipc</c> MessagePack frames.
/// </summary>
public static class ScriptExecutorProtocol
{
    /// <summary><c>ScriptArtifactLoadRequest</c> → <c>IpcEmpty</c>: validate and load the image from memory. Fails with an
    /// error (and never loads anything) on any bound, length, hash, runtime or globals-surface mismatch.</summary>
    public const string Load = "executor.load";

    /// <summary><c>ScriptRunRequest</c> → <c>CodeExecutionResult</c>: run the loaded image once with the supplied
    /// globals surface and the remaining budget. Inner tool calls are <c>ExtensionProtocol.HostToolCall</c> with
    /// <c>Forge.Ipc.Protocol.HostToolCallRequest</c> → <c>ToolResult</c>.</summary>
    public const string Run = "executor.run";

    /// <summary><c>IpcEmpty</c> → <c>IpcEmpty</c>: clean stop; the normal teardown is the host killing the process tree,
    /// so this is only a cooperative exit.</summary>
    public const string Stop = "executor.stop";
}

/// <summary>Delivers the compiled image (raw PE bytes) and its descriptor in one frame. The executor validates and loads
/// the bytes from memory before acknowledging; it never writes a temporary file and never falls back to <c>Assembly.Load</c>.</summary>
[MessagePackObject]
public sealed partial record ScriptArtifactLoadRequest(
    [property: Key(0)] ReadOnlyMemory<byte> Assembly,
    [property: Key(1)] ScriptArtifactDescriptor Descriptor);

/// <summary>One run on the already-loaded image. The host sends exactly the remaining budget after spawn and load; it
/// keeps the hard deadline and the kill for itself, so the executor never needs to overrun it.</summary>
[MessagePackObject]
public sealed partial record ScriptRunRequest(
    [property: Key(0)] ExtensionCallContext Context,
    [property: Key(1)] int TimeoutMilliseconds,
    [property: Key(2)] IReadOnlyList<ToolDeclaration> Tools,
    [property: Key(3)] CodeExecutionOptions Options);
