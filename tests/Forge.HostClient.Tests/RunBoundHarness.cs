using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

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

    public string ResultJson { get; set; } = """{"content":"root-ok","isError":false}""";
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

internal static class BoundRoots
{
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
            return Json.Element(root.ResultJson);
        });
    }

    public static Task<IpcMessage> WaitArrivedAsync(InFlightRoot root) => root.Arrived.Task.Bounded();

    public static string RunIdOf(IpcMessage rootRequest)
    {
        var args = rootRequest.Arguments ?? throw new InvalidOperationException("The root request has no arguments.");
        return args.GetProperty("context").GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The root request carries no run id.");
    }

    public static string PluginOf(IpcMessage rootRequest)
    {
        var args = rootRequest.Arguments ?? throw new InvalidOperationException("The root request has no arguments.");
        return args.TryGetProperty("pluginId", out var plugin) ? plugin.GetString()! : "$run_code";
    }

    /// <summary>A run-bound <c>host.tool_call</c> payload. When <paramref name="forgeLegacy"/> is set,
    /// worker-impersonating legacy fields (plus a forged <c>context</c> object) ride along; the client
    /// must ignore all of them and use the pending record.</summary>
    public static string BoundPayload(
        string runId, string pluginId, string name, string argsJson, bool forgeLegacy = false)
    {
        var forged = forgeLegacy
            ? ""","sessionId":"FORGED","workingDir":"C:/forged","depth":99,"context":{"runId":"forged","sessionId":"FORGED","workingDirectory":"C:/forged","depth":99,"pluginChain":[]}"""
            : string.Empty;
        return "{\"runId\":\"" + runId + "\",\"pluginId\":\"" + pluginId + "\",\"name\":\"" + name
            + "\",\"args\":" + argsJson + forged + "}";
    }

    public static (string Content, bool IsError) OutputOf(IpcMessage response)
    {
        var result = response.Result
            ?? throw new InvalidOperationException("The relay answered with an error envelope.");
        return (
            result.GetProperty("content").GetString()!,
            result.GetProperty("isError").GetBoolean());
    }

    public static async Task<ExtensionHostTool> RegisterPluginToolAsync(
        ExtensionHostClient client, FakeHost host, string pluginId, string tool, int sideEffect = 0)
    {
        await host.NotifyClientAsync("tools.register",
            "{\"pluginId\":\"" + pluginId + "\",\"tools\":[{\"name\":\"" + tool
            + "\",\"description\":\"d\",\"inputSchemaJson\":\"{}\",\"sideEffect\":" + sideEffect + "}]}");
        await host.SyncAsync();
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
