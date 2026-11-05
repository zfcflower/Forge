using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Same-root wait cycles, same-root fan-out, cancelled roots and repeated same-edge nesting.</summary>
public sealed class SameRootResourceCycleTests
{
    private const string CycleCode = "plugin_wait_cycle";

    private const string BusyCode = "plugin_busy";

    /// <summary>A refused closing edge must arrive within this bound; the request budgets are 60 s and 120 s.</summary>
    private static readonly TimeSpan RefusalLimit = TimeSpan.FromSeconds(10);

    /// <summary>Bound on a whole script; a script that reaches it is hung.</summary>
    private static readonly TimeSpan ScriptLimit = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan MarkerLimit = TimeSpan.FromSeconds(30);

    private const string CrossForksScript = """
        var forks = new[]
        {
            Tools.Call("cycleRelay", new { mode = "fork", plugin = "pa", other = "pb", inner = "cross", text = "a" }),
            Tools.Call("cycleRelay", new { mode = "fork", plugin = "pb", other = "pa", inner = "cross", text = "b" }),
        };
        var parts = await System.Threading.Tasks.Task.WhenAll(forks);
        return string.Join("|", parts);
        """;

    private const string SameEdgeForksScript = """
        var forks = new[]
        {
            Tools.Call("cycleRelay", new { mode = "fork", plugin = "pa", other = "pb", inner = "hop", text = "a" }),
            Tools.Call("cycleRelay", new { mode = "fork", plugin = "pa", other = "pb", inner = "hop", text = "b" }),
        };
        var parts = await System.Threading.Tasks.Task.WhenAll(forks);
        return string.Join("|", parts);
        """;

    private const string FanoutScript = """
        var calls = System.Linq.Enumerable.Range(0, 5).Select(i => Tools.Call("cycleRelay", new { mode = "sleepThenEcho", plugin = "pa", ms = 200, text = "t" + i }));
        var parts = await System.Threading.Tasks.Task.WhenAll(calls);
        return string.Join("|", parts);
        """;

    [Fact]
    public async Task Same_root_cross_branch_wait_cycle_refuses_the_closing_edge_and_the_other_branch_drains()
    {
        SameRootResourceCycleRelay? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == SameRootResourceCycleRelay.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new SameRootResourceCycleRelay(fx);
        try
        {
            await StartPluginsAsync(fx, "pa", "pb");
            var runCode = fx.Client.CreateRunCodeTool(new CycleRegistry(relay));

            var script = await InvokeScriptAsync(fx, runCode, CrossForksScript, TimeSpan.FromSeconds(60)).WaitAsync(ScriptLimit, fx.Ct);

            Assert.False(script.IsError, script.Content);
            Assert.Contains(CycleCode, script.Content, StringComparison.Ordinal);
            var outcomes = relay.Outcomes;
            Assert.Equal(2, outcomes.Count);

            var refused = Assert.Single(outcomes, outcome => outcome.IsError);
            Assert.Contains(CycleCode, refused.Content, StringComparison.Ordinal);
            Assert.True(
                refused.At - relay.CrossReleasedAt < RefusalLimit,
                $"the closing edge was refused {(refused.At - relay.CrossReleasedAt).TotalSeconds:0.###} s after both branches were inside their plugins; it must be refused at once.");

            var survivor = Assert.Single(outcomes, outcome => !outcome.IsError);
            Assert.Equal(EchoExpectation(survivor.Target, survivor.Text), survivor.Content);
            Assert.True(
                survivor.At >= refused.At,
                "the surviving branch completed before the refused closing edge released its worker slot, so it cannot have been waiting on that slot.");

            var afterP = await fx.InvokeAsync("pa", PluginSources.EchoTool, """{"text":"after"}""");
            Assert.False(afterP.IsError, afterP.Content);
            Assert.Equal("p:after", afterP.Content);
            var afterQ = await fx.InvokeAsync("pb", PluginSources.EchoTool, """{"text":"after"}""");
            Assert.False(afterQ.IsError, afterQ.Content);
            Assert.Equal("q:after", afterQ.Content);
        }
        finally
        {
            relay.ReleaseBarrier();
        }
    }

    [Fact]
    public async Task Same_root_fanout_of_five_siblings_on_a_capacity_one_plugin_all_queue_and_return_their_own_results()
    {
        SameRootResourceCycleRelay? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == SameRootResourceCycleRelay.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new SameRootResourceCycleRelay(fx);
        await StartPluginsAsync(fx, "pa");
        var runCode = fx.Client.CreateRunCodeTool(new CycleRegistry(relay));

        var outcome = await InvokeScriptAsync(fx, runCode, FanoutScript, TimeSpan.FromSeconds(60)).WaitAsync(ScriptLimit, fx.Ct);

        AssertExactFanout(outcome);
    }

    [Fact]
    public async Task Cancelled_root_with_queued_siblings_leaves_no_wait_edge_for_an_equivalent_root()
    {
        SameRootResourceCycleRelay? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == SameRootResourceCycleRelay.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new SameRootResourceCycleRelay(fx);
        await StartPluginsAsync(fx, "pa");
        var runCode = fx.Client.CreateRunCodeTool(new CycleRegistry(relay));

        var marker = Path.Combine(fx.Root, "sleeper-started.txt");
        var workerPid = await fx.WorkerPidAsync("pa");
        var cancelledScript = """
            var calls = System.Linq.Enumerable.Range(0, 5).Select(_ => Tools.Call("cycleRelay", new { mode = "sleep", plugin = "pa", ms = 60000, marker = "MARKER" }));
            var parts = await System.Threading.Tasks.Task.WhenAll(calls);
            return string.Join("|", parts);
            """.Replace("MARKER", marker.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);

        using var cancelRoot = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var first = InvokeScriptAsync(fx, runCode, cancelledScript, TimeSpan.FromSeconds(60), cancelRoot.Token);
        await PluginQueueBudgetWait.UntilAsync(
            () => File.Exists(marker), MarkerLimit, fx.Ct, "the first sibling to occupy the only slot of pa");
        cancelRoot.Cancel();

        var ending = await EndAsync(first, fx.Ct);
        if (ending.Fault is not null)
        {
            Assert.True(
                ending.Fault is OperationCanceledException or IOException or InvalidOperationException,
                $"the cancelled root failed with an unexpected {ending.Fault.GetType().FullName}: {ending.Fault.Message}");
        }
        else
        {
            Assert.True(ending.Result is { IsError: true }, $"the cancelled root succeeded: '{ending.Result?.Content}'");
            Assert.DoesNotContain(CycleCode, ending.Result!.Content, StringComparison.Ordinal);
        }

        // Barrier: the cancelled worker is gone and its replacement is serving.
        await fx.WaitForExitAsync(workerPid, "the worker that ran the cancelled root");
        var replacementPid = await fx.WorkerPidAsync("pa");
        Assert.NotEqual(workerPid, replacementPid);

        var equivalent = await InvokeScriptAsync(fx, runCode, FanoutScript, TimeSpan.FromSeconds(60)).WaitAsync(ScriptLimit, fx.Ct);

        AssertExactFanout(equivalent);
        Assert.DoesNotContain(CycleCode, equivalent.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(BusyCode, equivalent.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_same_edge_nesting_from_one_root_queues_on_the_caller_and_never_reports_a_cycle()
    {
        SameRootResourceCycleRelay? relay = null;
        await using var fx = new E2eFixture(
            hostArguments: ["--concurrency", "1"],
            toolResolver: name => name == SameRootResourceCycleRelay.Name ? relay : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        relay = new SameRootResourceCycleRelay(fx);
        await StartPluginsAsync(fx, "pa", "pb");
        var runCode = fx.Client.CreateRunCodeTool(new CycleRegistry(relay));

        var script = await InvokeScriptAsync(fx, runCode, SameEdgeForksScript, TimeSpan.FromSeconds(60)).WaitAsync(ScriptLimit, fx.Ct);

        Assert.False(script.IsError, script.Content);
        Assert.DoesNotContain(CycleCode, script.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(BusyCode, script.Content, StringComparison.Ordinal);
        var outcomes = relay.Outcomes;
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.False(outcome.IsError, outcome.Content));
        Assert.Equal(new[] { "q:a", "q:b" }, outcomes.Select(outcome => outcome.Content).Order().ToArray());
    }

    private static void AssertExactFanout(ToolResult outcome)
    {
        Assert.False(outcome.IsError, outcome.Content);
        var expected = Enumerable.Range(0, 5).Select(index => $"ok:p:t{index}").ToArray();
        Assert.Equal(expected, outcome.Content.Split('|'));
    }

    private static string EchoExpectation(string target, string text) => $"{PrefixOf(target)}:{text}";

    private static string PrefixOf(string pluginId) => pluginId switch
    {
        "pa" => "p",
        "pb" => "q",
        _ => throw new ArgumentOutOfRangeException(nameof(pluginId), pluginId, "no echo prefix is defined for this plugin"),
    };

    private static async Task StartPluginsAsync(E2eFixture fx, params string[] pluginIds)
    {
        foreach (var id in pluginIds)
        {
            fx.WritePlugin(id, PluginSources.Standard(id, echoPrefix: PrefixOf(id)));
        }

        await fx.StartAsync();
        foreach (var id in pluginIds)
        {
            await fx.ApproveAsync(id);
        }

        foreach (var id in pluginIds)
        {
            await fx.WaitUntilAsync(() => fx.ToolsOf(id).Count == PluginSources.AllTools.Length, $"the tools of {id}");
        }
    }

    private static Task<ToolResult> InvokeScriptAsync(
        E2eFixture fx, ITool runCode, string code, TimeSpan timeout, CancellationToken ct = default) =>
        runCode.InvokeAsync(
            new ToolCall(
                Guid.NewGuid().ToString("N"),
                "run_code",
                JsonSerializer.Serialize(new { code, timeout_seconds = timeout.TotalSeconds })),
            new ToolContext("same-root-cycle", fx.Root),
            ct == default ? fx.Ct : ct).AsTask();

    private static async Task<(ToolResult? Result, Exception? Fault)> EndAsync(Task<ToolResult> call, CancellationToken ct)
    {
        try
        {
            return (await call.WaitAsync(ScriptLimit, ct), null);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"A script did not end within {ScriptLimit.TotalSeconds:0} s after its root was cancelled.");
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private sealed class CycleRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [tool];

        public bool TryGet(string name, out ITool found)
        {
            found = tool;
            return string.Equals(tool.Definition.Name, name, StringComparison.Ordinal);
        }
    }
}
