using Forge.Plugin.Sdk;
using MessagePack;

namespace Forge.Ipc.Protocol;

// The host-internal payloads of the operations in Forge.Plugin.Sdk.ExtensionProtocol: one definition per shape, shared by
// the main program (Forge.HostClient), the Extension Host, the Plugin Worker and the script executor. They are not part
// of the plugin SDK, so changing one never raises the SDK version or invalidates approvals. Payloads that are already SDK
// types (CodeExecutionRequest, PluginStatusResponse, ...) travel as those types. Tool arguments are the model's JSON and
// travel as raw UTF-8 JSON bytes, parsed only by the endpoint that hands them to a tool; empty means "{}".

/// <summary><c>plugin.execute</c> (main program → host). The host refuses a request without <see cref="Context"/>;
/// a null <see cref="TimeoutMilliseconds"/> means the host's own tool budget.</summary>
[MessagePackObject]
public sealed partial record PluginExecuteRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Tool,
    [property: Key(2)] ReadOnlyMemory<byte> ArgumentsJson,
    [property: Key(3)] ExtensionCallContext? Context,
    [property: Key(4)] int? TimeoutMilliseconds = null);

/// <summary><c>plugin.approve</c>: approves exactly these hashes for one version. Revocation uses
/// <see cref="PluginVersionRequest"/>.</summary>
[MessagePackObject]
public sealed partial record PluginApproveRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] string SourceHash,
    [property: Key(3)] string DependencyHash,
    [property: Key(4)] string SdkVersion);

/// <summary><c>tools.register</c> (host → main program, notification). <c>tools.unregister</c>,
/// <c>contributions.unregister</c> and <c>worker.unload</c> carry <see cref="ExtensionIdRequest"/>.</summary>
[MessagePackObject]
public sealed partial record ToolsRegisterNotification(
    [property: Key(0)] string PluginId,
    [property: Key(1)] IReadOnlyList<ToolDeclaration> Tools);

/// <summary><c>plugin.problem</c> (host → main program, notification): something went wrong with a plugin that the user
/// should hear about even though the call that hit it already has a result. <see cref="Code"/> is a stable English token
/// (<c>workflow-terminated</c>, <c>worker-disabled</c>); <see cref="Message"/> is the host's own English sentence for the
/// log and for codes the main program does not know; <see cref="Count"/> is the number the code quantifies (runs
/// terminated, failures recorded) and zero when it quantifies nothing.</summary>
[MessagePackObject]
public sealed partial record PluginProblemNotification(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Code,
    [property: Key(2)] string Message,
    [property: Key(3)] int Count = 0);

/// <summary><c>host.tool_call</c> from a worker or a script executor to the host: the run it was bound to and the target
/// tool. Session, directory, depth and plugin identity are deliberately absent - the host takes them from its active-run
/// table. The result of every tool call on the wire is <see cref="ToolResult"/>.</summary>
[MessagePackObject]
public sealed partial record HostToolCallRequest(
    [property: Key(0)] string RunId,
    [property: Key(1)] string Name,
    [property: Key(2)] ReadOnlyMemory<byte> ArgumentsJson);

/// <summary><c>host.tool_call</c> relayed by the host to the main program. <see cref="PluginId"/> comes from the host's
/// active-run table, never from the worker.</summary>
[MessagePackObject]
public sealed partial record HostToolCallForward(
    [property: Key(0)] string RunId,
    [property: Key(1)] string PluginId,
    [property: Key(2)] string Name,
    [property: Key(3)] ReadOnlyMemory<byte> ArgumentsJson);

/// <summary><c>worker.load</c>: the worker must load exactly the bytes that match <see cref="AssemblySha256"/> and reject
/// any file of the plugin directory that is not listed in <see cref="Files"/> (relative <c>/</c> name → SHA-256 hex) or
/// does not match. <see cref="Permissions"/> are the ones captured before approval; registration may not ask for more.</summary>
[MessagePackObject]
public sealed partial record WorkerLoadRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Version,
    [property: Key(2)] string AssemblyPath,
    [property: Key(3)] string AssemblySha256,
    [property: Key(4)] IReadOnlyDictionary<string, string> Files,
    [property: Key(5)] IReadOnlyList<Permission> Permissions);

/// <summary><c>worker.load</c> result: everything the plugin registered, tools included.</summary>
[MessagePackObject]
public sealed partial record WorkerLoadResponse([property: Key(0)] PluginContributionSet Contributions);

/// <summary><c>worker.execute</c>: one tool call under the context the host bound; <see cref="TimeoutMilliseconds"/> is
/// the remaining cooperative budget (the host still kills the process tree when it runs out).</summary>
[MessagePackObject]
public sealed partial record WorkerExecuteRequest(
    [property: Key(0)] string PluginId,
    [property: Key(1)] string Tool,
    [property: Key(2)] ReadOnlyMemory<byte> ArgumentsJson,
    [property: Key(3)] string HostPipeName,
    [property: Key(4)] ExtensionCallContext Context,
    [property: Key(5)] int TimeoutMilliseconds);
