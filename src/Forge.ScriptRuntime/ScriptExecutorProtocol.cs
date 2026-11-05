using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Plugin.Sdk;

namespace Forge.ScriptRuntime;

/// <summary>
/// Ipc operations between the Extension Host and one single-use script executor process. The executor serves exactly
/// one image and one run, then the host kills and waits for its process tree; nothing here is ever reused.
/// Inner tool calls leave the executor through the existing <c>host.tool_call</c> operation, bound by the host to the
/// run id exactly as for plugin workers.
/// </summary>
public static class ScriptExecutorProtocol
{
    /// <summary><c>{assemblyBase64, descriptor}</c> → <c>{}</c>: validate and load the image from memory. Fails with an
    /// error (and never loads anything) on any bound, length, hash, runtime or globals-surface mismatch.</summary>
    public const string Load = "executor.load";

    /// <summary><c>ScriptRunRequest</c> → <c>CodeExecutionResult</c>: run the loaded image once with the supplied
    /// globals surface and the remaining budget.</summary>
    public const string Run = "executor.run";

    /// <summary><c>{}</c> → <c>{}</c>: clean stop; the normal teardown is the host killing the process tree, so this is
    /// only a cooperative exit.</summary>
    public const string Stop = "executor.stop";

    /// <summary>The one operation the executor sends to the host, unchanged from the plugin worker protocol.</summary>
    public const string HostToolCall = "host.tool_call";
}

/// <summary>Delivers the compiled image and its descriptor in one frame. The executor validates and loads the bytes
/// from memory before acknowledging; it never writes a temporary file and never falls back to <c>Assembly.Load</c>.</summary>
public sealed record ScriptArtifactLoadRequest(string AssemblyBase64, ScriptArtifactDescriptor Descriptor);

/// <summary>One run on the already-loaded image. The host sends exactly the remaining budget after spawn and load; it
/// keeps the hard deadline and the kill for itself, so the executor never needs to overrun it.</summary>
public sealed record ScriptRunRequest(
    ExtensionCallContext Context,
    int TimeoutMilliseconds,
    IReadOnlyList<ToolDeclaration> Tools,
    CodeExecutionOptions Options);

/// <summary>One inner tool call the executor relays to the host: the run id it was bound to, the tool name and the JSON
/// arguments. The executor never reports a session, a working directory or a depth; those come from the host's binding.</summary>
public sealed record BoundToolCallRequest(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("args")] JsonElement Arguments);

/// <summary>The host's answer to one <c>host.tool_call</c>.</summary>
public sealed record ToolResultPayload(string Content, bool IsError = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScriptArtifactDescriptor))]
[JsonSerializable(typeof(ScriptArtifactLoadRequest))]
[JsonSerializable(typeof(ScriptRunRequest))]
[JsonSerializable(typeof(ToolDeclaration))]
[JsonSerializable(typeof(CodeExecutionOptions))]
[JsonSerializable(typeof(ExtensionCallContext))]
[JsonSerializable(typeof(CodeExecutionResult))]
[JsonSerializable(typeof(BoundToolCallRequest))]
[JsonSerializable(typeof(ToolResultPayload))]
public partial class ScriptExecutorJsonContext : JsonSerializerContext
{
}
