using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Same-root siblings queue on a busy worker; cross-root nested calls fail fast with plugin_busy.</summary>
public sealed class NestedRootQueueTests
{
    /// <summary>Side B's tools carry this prefix so they do not clash with side A's same-named
    /// <see cref="PluginSources.Standard"/> tools; the host refuses a tool name another plugin already publishes.</summary>
    private const string SideBToolPrefix = "b_";
    [Fact]
    public async Task Same_script_root_fans_out_beyond_plugin_capacity_and_all_succeed()
    {
        const int count = 5;
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueRelayTool? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == QueueRelayTool.Name ? relay : null,
            approvalPath: async (request, ct) =>
            {
                if (string.Equals(request.ToolName, QueueRelayTool.Name, StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref arrived) == count)
                    {
                        release.TrySetResult();
                    }

                    await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }

                return ApprovalDecision.Allow;
            });
        relay = new QueueRelayTool(fx);
        fx.WritePlugin("target", PluginSources.Standard("target", echoPrefix: "t"));
        await fx.StartAsync();
        await fx.ApproveAsync("target");
        await fx.WaitUntilAsync(() => fx.ToolsOf("target").Count == PluginSources.AllTools.Length, "target tools");

        var runCode = fx.Client.CreateRunCodeTool(new SingleToolRegistry(relay!));
        var script = """
            var calls = System.Linq.Enumerable.Range(0, 5).Select(_ => Tools.Call("relay", new { mode = "sleepOnce", plugin = "target", ms = 1000 }));
            var parts = await System.Threading.Tasks.Task.WhenAll(calls);
            return string.Join("|", parts);
            """;
        var outcome = await InvokeScriptAsync(fx, runCode, script, TimeSpan.FromSeconds(90));

        Assert.False(outcome.IsError, outcome.Content);
        Assert.Equal(count, arrived);
        var parts = outcome.Content.Split('|');
        Assert.Equal(count, parts.Length);
        Assert.All(parts, part => Assert.Equal("ok:slept:1000", part));
        Assert.DoesNotContain("plugin_busy", outcome.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("error:", outcome.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_root_plugin_callback_fanout_queues_and_all_succeed()
    {
        const int count = 5;
        QueueRelayTool? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == QueueRelayTool.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new QueueRelayTool(fx);
        fx.WritePlugin("target", PluginSources.Standard("target", echoPrefix: "t"));
        await fx.StartAsync();
        await fx.ApproveAsync("target");
        await fx.WaitUntilAsync(() => fx.ToolsOf("target").Count == PluginSources.AllTools.Length, "target tools");

        var runCode = fx.Client.CreateRunCodeTool(new SingleToolRegistry(relay!));
        var script = """
            var result = await Tools.Call("relay", new { mode = "fanout", plugin = "target", count = 5, ms = 1000 });
            return result;
            """;
        var outcome = await InvokeScriptAsync(fx, runCode, script, TimeSpan.FromSeconds(90));

        Assert.False(outcome.IsError, outcome.Content);
        Assert.Equal(count, relay!.FanoutAdmissions);
        Assert.StartsWith("fanout:", outcome.Content, StringComparison.Ordinal);
        var parts = outcome.Content.Substring("fanout:".Length).Split('|');
        Assert.Equal(count, parts.Length);
        Assert.All(parts, part => Assert.Equal("ok:slept:1000", part));
        Assert.DoesNotContain("plugin_busy", outcome.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("error:", outcome.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cross_root_nested_call_is_refused_while_same_chain_name_is_shared()
    {
        QueueRelayTool? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1", "--script-workers", "2"],
            toolResolver: name => name == QueueRelayTool.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new QueueRelayTool(fx);
        fx.WritePlugin("target", PluginSources.Standard("target", echoPrefix: "t"));
        await fx.StartAsync();
        await fx.ApproveAsync("target");
        await fx.WaitUntilAsync(() => fx.ToolsOf("target").Count == PluginSources.AllTools.Length, "target tools");

        var runCode = fx.Client.CreateRunCodeTool(new SingleToolRegistry(relay!));

        var marker = Path.Combine(fx.Root, "holder-started.txt");
        var holdScript = """
            var result = await Tools.Call("relay", new { mode = "hold", plugin = "target", marker = "MARKER", ms = 20000 });
            return result;
            """.Replace("MARKER", marker.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);
        var holder = InvokeScriptAsync(fx, runCode, holdScript, TimeSpan.FromSeconds(60));
        await fx.WaitUntilAsync(() => File.Exists(marker), "the holding script to occupy the target worker");

        var contenderScript = """
            var result = await Tools.Call("relay", new { mode = "once", plugin = "target", text = "c" });
            return result;
            """;
        var contender = await InvokeScriptAsync(fx, runCode, contenderScript, TimeSpan.FromSeconds(60));

        Assert.False(contender.IsError, contender.Content);
        Assert.Contains("plugin_busy", contender.Content, StringComparison.Ordinal);

        var held = await holder.WaitAsync(fx.Ct);
        Assert.False(held.IsError, held.Content);
        Assert.Contains("slept:20000", held.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opposite_nested_plugin_calls_finish_instead_of_deadlocking()
    {
        QueueRelayTool? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == QueueRelayTool.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new QueueRelayTool(fx);
        fx.WritePlugin("sideA", PluginSources.Standard("sideA", echoPrefix: "a"));
        fx.WritePlugin("sideB", PluginSources.Standard("sideB", echoPrefix: "b", toolPrefix: SideBToolPrefix));
        await fx.StartAsync();
        await fx.ApproveAsync("sideA");
        await fx.ApproveAsync("sideB");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("sideA").Count == PluginSources.AllTools.Length &&
                  fx.ToolsOf("sideB").Count == PluginSources.AllTools.Length,
            "tools of both sides");

        var towardsB = fx.InvokeAsync(
            "sideA", "callHost",
            JsonSerializer.Serialize(new
            {
                target = QueueRelayTool.Name,
                args = new { mode = "pairedOnce", plugin = "sideB", tool = SideBToolPrefix + PluginSources.EchoTool, text = "x" },
            }));
        var towardsA = fx.InvokeAsync(
            "sideB", SideBToolPrefix + PluginSources.CallHostTool,
            JsonSerializer.Serialize(new
            {
                target = QueueRelayTool.Name,
                args = new { mode = "pairedOnce", plugin = "sideA", text = "y" },
            }));

        var results = await Task.WhenAll(towardsB, towardsA).WaitAsync(fx.Ct);
        Assert.All(results, result => Assert.False(result.IsError, result.Content));
        Assert.All(
            results,
            result => Assert.Contains("plugin_busy", result.Content, StringComparison.Ordinal));
    }

    private static Task<ToolResult> InvokeScriptAsync(
        E2eFixture fx, ITool runCode, string code, TimeSpan timeout) =>
        runCode.InvokeAsync(
            new ToolCall(
                Guid.NewGuid().ToString("N"),
                "run_code",
                JsonSerializer.Serialize(new { code, timeout_seconds = timeout.TotalSeconds })),
            new ToolContext("nested-queue", fx.Root),
            fx.Ct).AsTask();

    private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [tool];

        public bool TryGet(string name, out ITool found)
        {
            found = tool;
            return string.Equals(tool.Definition.Name, name, StringComparison.Ordinal);
        }
    }

    private sealed class QueueRelayTool(E2eFixture fx) : ITool
    {
        private int pairedEntered;
        private int pairedCompleted;
        private readonly TaskCompletionSource pairedEnterGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource pairedReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int fanoutAdmissions;

        public const string Name = "relay";

        public int FanoutAdmissions => Volatile.Read(ref fanoutAdmissions);

        public ToolDefinition Definition { get; } = new(Name, "Test relay that nests plugin calls", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.Execute;

        public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var args = document.RootElement;
            var mode = args.GetProperty("mode").GetString();
            var plugin = args.GetProperty("plugin").GetString()!;

            switch (mode)
            {
                case "once":
                {
                    var text = args.GetProperty("text").GetString() ?? string.Empty;
                    var result = await fx.InvokeAsync(
                        plugin, "echo", JsonSerializer.Serialize(new { text }));
                    return new ToolResult($"once:{(result.IsError ? "error:" : "ok:")}{result.Content}", IsError: false);
                }

                case "pairedOnce":
                {
                    var text = args.GetProperty("text").GetString() ?? string.Empty;
                    var tool = args.TryGetProperty("tool", out var toolElement) && toolElement.ValueKind == JsonValueKind.String
                        ? toolElement.GetString()!
                        : "echo";
                    if (Interlocked.Increment(ref pairedEntered) == 2)
                    {
                        pairedEnterGate.TrySetResult();
                    }

                    try
                    {
                        await pairedEnterGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                    {
                        pairedEnterGate.TrySetResult();
                        pairedReleaseGate.TrySetResult();
                        throw;
                    }

                    ToolResult nested;
                    try
                    {
                        nested = await fx.InvokeAsync(
                            plugin, tool, JsonSerializer.Serialize(new { text })).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (Interlocked.Increment(ref pairedCompleted) == 2)
                        {
                            pairedReleaseGate.TrySetResult();
                        }

                        throw;
                    }

                    var outcome = $"once:{(nested.IsError ? "error:" : "ok:")}{nested.Content}";
                    if (Interlocked.Increment(ref pairedCompleted) == 2)
                    {
                        pairedReleaseGate.TrySetResult();
                    }

                    try
                    {
                        await pairedReleaseGate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                    {
                        pairedReleaseGate.TrySetResult();
                        throw;
                    }

                    return new ToolResult(outcome, IsError: false);
                }

                case "sleepOnce":
                {
                    var ms = args.TryGetProperty("ms", out var milliseconds) ? milliseconds.GetInt32() : 1000;
                    var result = await fx.InvokeAsync(
                        plugin, "sleep", JsonSerializer.Serialize(new { ms }));
                    return new ToolResult($"{(result.IsError ? "error:" : "ok:")}{result.Content}", IsError: false);
                }

                case "fanout":
                {
                    var count = args.GetProperty("count").GetInt32();
                    var ms = args.GetProperty("ms").GetInt32();
                    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var pending = new List<Task<ToolResult>>(count);
                    for (var index = 0; index < count; index++)
                    {
                        pending.Add(Task.Run(async () =>
                        {
                            await start.Task.WaitAsync(ct).ConfigureAwait(false);
                            Interlocked.Increment(ref fanoutAdmissions);
                            return await fx.InvokeAsync(
                                plugin, "sleep", JsonSerializer.Serialize(new { ms })).ConfigureAwait(false);
                        }, ct));
                    }

                    start.TrySetResult();
                    var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    var joined = string.Join("|", results.Select(result =>
                        $"{(result.IsError ? "error:" : "ok:")}{result.Content}"));
                    return new ToolResult($"fanout:{joined}", IsError: false);
                }

                case "hold":
                {
                    var marker = args.GetProperty("marker").GetString() ?? string.Empty;
                    var ms = args.TryGetProperty("ms", out var milliseconds) ? milliseconds.GetInt32() : 20_000;
                    var result = await fx.InvokeAsync(
                        plugin, "sleep", JsonSerializer.Serialize(new { marker, ms }));
                    return new ToolResult($"hold:{(result.IsError ? "error:" : "ok:")}{result.Content}", IsError: false);
                }

                default:
                    return new ToolResult($"unknown relay mode '{mode}'", IsError: true);
            }
        }
    }
}
