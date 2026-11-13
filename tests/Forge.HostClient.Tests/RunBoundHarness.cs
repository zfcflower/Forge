using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>Drives run-bound <c>host.tool_call</c> callbacks from inside an in-flight root execution.
/// A root is a client-initiated <c>plugin.execute</c>, <c>code.execute</c>, <c>plugin.command</c> or
/// <c>plugin.prompt</c> whose fake-host handler parks on <see cref="InFlightRoot.Gate"/>; while it is
/// parked the test issues bound callbacks carrying that root's <c>runId</c> and then releases the gate.
/// Everything is TCS-coordinated; no sleeps.</summary>
internal sealed class InFlightRoot
{
    public TaskCompletionSource<IpcMessage> Arrived { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Gate { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => Gate.TrySetResult();

    /// <summary>The content the parked root answers with once released.</summary>
    public string Content { get; set; } = "root-ok";

    public bool IsError { get; set; }

    /// <summary>The answer to the parked request: <c>code.execute</c> returns a script result, every other root a tool
    /// result.</summary>
    public HostReply Reply(string operation) =>
        string.Equals(operation, Sdk.ExtensionProtocol.CodeExecute, StringComparison.Ordinal)
            ? HostReply.Of(new Sdk.CodeExecutionResult(Content, IsError))
            : HostReply.Of(new Sdk.ToolResult(Content, IsError));
}

internal sealed class TestRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> tools = new(StringComparer.Ordinal);

    public TestRegistry(IEnumerable<ITool>? initial = null)
    {
        if (initial is not null)
        {
            foreach (var tool in initial)
            {
                tools[tool.Definition.Name] = tool;
            }
        }
    }

    public IReadOnlyList<ITool> Tools => tools.Values.ToArray();

    public bool TryGet(string name, out ITool tool) => tools.TryGetValue(name, out tool!);

    public void Add(ITool tool) => tools[tool.Definition.Name] = tool;

    public void Remove(string name) => tools.TryRemove(name, out _);
}

/// <summary>A client-initiated root execution parked inside the fake host (<c>plugin.execute</c> or
/// <c>code.execute</c>), addressed by the run id its <c>host.tool_call</c> callbacks must carry.</summary>
internal sealed record ActiveRoot(string RunId, InFlightRoot Hold, Task<ToolResult> RootTask)
{
    /// <summary>Releases the parked root and waits until it has finished.</summary>
    public async Task<ToolResult> FinishAsync()
    {
        Hold.Release();
        return await RootTask.Bounded().ConfigureAwait(false);
    }

    /// <summary>Issues one run-bound <c>host.tool_call</c> from inside this root, releases the root and waits until it
    /// has finished; returns the callback's answer.</summary>
    public async Task<IpcMessage> CallAsync(
        FakeHost host, string name, string argsJson, CancellationToken ct,
        string pluginId = "alpha", bool forgeLegacy = false)
    {
        var payload = BoundRoots.BoundPayload(RunId, pluginId, name, argsJson, forgeLegacy);
        var response = await host.CallClientAsync(Sdk.ExtensionProtocol.HostToolCall, payload, ct).ConfigureAwait(false);
        await FinishAsync().ConfigureAwait(false);
        return response;
    }
}

internal static class BoundRoots
{
    /// <summary>Starts an <c>alpha</c>/<c>entry</c> plugin root against a parked <c>plugin.execute</c>.</summary>
    public static Task<ActiveRoot> StartPluginToolRootAsync(
        FakeHost host, ExtensionHostClient client, CancellationToken ct) =>
        StartPluginToolRootAsync(host, client, new ToolContext("sess-root", AbsoluteDir("root")), ct);

    /// <summary>Starts a plugin tool root of <paramref name="pluginId"/> against a parked <c>plugin.execute</c>
    /// and returns the run id its callbacks must carry.</summary>
    public static async Task<ActiveRoot> StartPluginToolRootAsync(
        FakeHost host, ExtensionHostClient client, ToolContext context, CancellationToken ct,
        string pluginId = "alpha", string tool = "entry")
    {
        var hold = new InFlightRoot();
        ParkRoot(host, "plugin.execute", hold);
        var proxy = await RegisterPluginToolAsync(client, host, pluginId, tool).ConfigureAwait(false);
        var rootTask = await StartPluginRootAsync(proxy, context, hold, ct).ConfigureAwait(false);
        var runId = RunIdOf(await WaitArrivedAsync(hold).ConfigureAwait(false));
        Assert.NotEqual(string.Empty, runId);
        return new ActiveRoot(runId, hold, rootTask);
    }

    /// <summary>Starts a <c>run_code</c> root against a parked <c>code.execute</c>.</summary>
    public static async Task<ActiveRoot> StartScriptRootAsync(
        FakeHost host, ExtensionHostClient client, IToolRegistry registry, CancellationToken ct,
        Sdk.CodeExecutionOptions? execution = null, string code = "1+1")
    {
        var hold = new InFlightRoot { Content = "script-ok" };
        ParkRoot(host, "code.execute", hold);
        var proxy = client.CreateRunCodeTool(
            registry, execution is null ? null : new RunCodeProxyOptions { Execution = execution });
        var rootTask = proxy.InvokeAsync(
            new ToolCall("root-rc", "run_code", "{\"code\":" + JsonSerializer.Serialize(code) + "}"),
            new ToolContext("sess-script", AbsoluteDir("script"), 0),
            ct).AsTask();
        var runId = RunIdOf(await WaitArrivedAsync(hold).ConfigureAwait(false));
        return new ActiveRoot(runId, hold, rootTask);
    }

    public static string AbsoluteDir(string name) =>
        Path.Combine(Path.GetTempPath(), "forge-hc", name).Replace('\\', '/');

    /// <summary>Parks the fake host's answer to <paramref name="operation"/> until the test releases
    /// <see cref="InFlightRoot.Gate"/>. The parked request (with its bound <c>context.runId</c>) is
    /// published through <see cref="InFlightRoot.Arrived"/>.</summary>
    public static void ParkRoot(FakeHost host, string operation, InFlightRoot root)
    {
        host.On(operation, async (message, ct) =>
        {
            root.Arrived.TrySetResult(message);
            await root.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return root.Reply(operation);
        });
    }

    public static Task<IpcMessage> WaitArrivedAsync(InFlightRoot root) => root.Arrived.Task.Bounded();

    public static string RunIdOf(IpcMessage rootRequest) =>
        ContextOf(rootRequest).RunId ?? throw new InvalidOperationException("The root request carries no run id.");

    /// <summary>The call context the client bound to a root request, decoded as the type its operation documents.</summary>
    public static Sdk.ExtensionCallContext ContextOf(IpcMessage rootRequest) =>
        (rootRequest.Operation switch
        {
            Sdk.ExtensionProtocol.PluginExecute => rootRequest.Read<PluginExecuteRequest>().Context,
            Sdk.ExtensionProtocol.CodeExecute => rootRequest.Read<Sdk.CodeExecutionRequest>().Context,
            Sdk.ExtensionProtocol.PluginCommand => rootRequest.Read<Sdk.CommandExecutionRequest>().Context,
            Sdk.ExtensionProtocol.PluginPrompt => rootRequest.Read<Sdk.PromptExecutionRequest>().Context,
            _ => throw new InvalidOperationException($"'{rootRequest.Operation}' is not a root request."),
        }) ?? throw new InvalidOperationException("The root request carries no context.");

    public static string PluginOf(IpcMessage rootRequest) =>
        rootRequest.Operation switch
        {
            Sdk.ExtensionProtocol.PluginExecute => rootRequest.Read<PluginExecuteRequest>().PluginId,
            Sdk.ExtensionProtocol.PluginCommand => rootRequest.Read<Sdk.CommandExecutionRequest>().PluginId,
            Sdk.ExtensionProtocol.PluginPrompt => rootRequest.Read<Sdk.PromptExecutionRequest>().PluginId,
            _ => "$run_code",
        };

    /// <summary>A run-bound <c>host.tool_call</c> payload. When <paramref name="forgeLegacy"/> is set, trailing
    /// worker-impersonating fields (a session, a directory, a depth and a forged context) ride along after the
    /// documented ones; the client must ignore all of them and use the pending record.</summary>
    public static RawPayload BoundPayload(
        string runId, string pluginId, string name, string argsJson, bool forgeLegacy = false)
    {
        var bound = IpcSerializer.Serialize(HostWire.Forward(runId, pluginId, name, argsJson));
        if (!forgeLegacy)
        {
            return new RawPayload(bound);
        }

        // HostToolCallForward is a 4-element MessagePack array (fixarray 0x94); 0x98 declares 8 elements and the
        // forged ones follow the documented four.
        Assert.Equal(0x94, bound[0]);
        byte[][] forged =
        [
            IpcSerializer.Serialize("FORGED"),
            IpcSerializer.Serialize("C:/forged"),
            IpcSerializer.Serialize(99),
            IpcSerializer.Serialize(new Sdk.ExtensionCallContext("forged", "FORGED", "C:/forged", 99, [])),
        ];
        var payload = new List<byte>(bound) { [0] = 0x98 };
        foreach (var field in forged)
        {
            payload.AddRange(field);
        }

        return new RawPayload(payload.ToArray());
    }

    /// <summary>The content of a refused callback answer; asserts <c>isError</c> so the message assertions that
    /// follow cannot pass on a success envelope.</summary>
    public static string RefusedContent(IpcMessage response)
    {
        var (content, isError) = OutputOf(response);
        Assert.True(isError, "The callback should have been refused, but it succeeded with: " + content);
        return content;
    }

    public static (string Content, bool IsError) OutputOf(IpcMessage response) => HostWire.ToolOutput(response);

    public static async Task<ExtensionHostTool> RegisterPluginToolAsync(
        ExtensionHostClient client, FakeHost host, string pluginId, string tool,
        Sdk.ToolSideEffect sideEffect = Sdk.ToolSideEffect.ReadOnly)
    {
        await host.NotifyAndSyncAsync(
            Sdk.ExtensionProtocol.ToolsRegister, HostWire.Tools(pluginId, HostWire.Tool(tool, sideEffect)));
        return (ExtensionHostTool)client.Tools.Single(t => t.Definition.Name == tool);
    }

    public static async Task<Task<ToolResult>> StartPluginRootAsync(
        ExtensionHostTool proxy, ToolContext context, InFlightRoot root, CancellationToken ct)
    {
        var task = proxy.InvokeAsync(new ToolCall("root-1", proxy.Definition.Name, "{}"), context, ct).AsTask();
        await WaitArrivedAsync(root);
        return task;
    }

    public static void AssertDiagnostic(ExtensionHostClient client, string fragment)
    {
        Assert.Contains(client.Diagnostics, entry => entry.Contains(fragment, StringComparison.Ordinal));
    }
}
