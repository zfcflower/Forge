using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>End-to-end proof for the migrated <c>run_code</c>: the main program exposes it through
/// <c>CreateRunCodeTool</c>, while compilation, semantics and execution live in the real Host and its
/// script workers. Every inner tool call is individually approved, bound to the original context, and
/// truncated by the main program; nesting back into <c>run_code</c> or around a call cycle is refused.</summary>
public sealed class RunCodeScriptTests
{
    [Fact]
    public async Task Script_returns_real_arithmetic_result()
    {
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var registry = new ScriptRegistry();
        var result = await RunScriptAsync(
            fx.Client, registry, "var a = 40;\nvar b = 2;\nreturn a + b;", new ToolContext("arith", fx.Root), fx.Ct);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("42", result.Content.Trim());
    }

    [Fact]
    public async Task Syntax_error_reports_line_and_does_not_run()
    {
        var probeCalls = 0;
        var probe = new ScriptMainTool(
            "probe",
            (_, _, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                return ValueTask.FromResult(new ToolResult("called"));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var result = await RunScriptAsync(
            fx.Client,
            new ScriptRegistry(probe),
            "var x = ;\nreturn await Tools.Call(\"probe\", new {});",
            new ToolContext("syntax", fx.Root),
            fx.Ct);

        Assert.True(result.IsError);
        Assert.Contains("line 1", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref probeCalls));
    }

    [Fact]
    public async Task Unknown_tool_error_lists_available_names()
    {
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var result = await RunScriptAsync(
            fx.Client,
            new ScriptRegistry(ScriptMainTool.Add()),
            "return await Tools.Call(\"nope\", new {});",
            new ToolContext("unknown", fx.Root),
            fx.Ct);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool 'nope'", result.Content, StringComparison.Ordinal);
        Assert.Contains("add", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inner_calls_logs_and_final_output_compose()
    {
        await using var fx = new E2eFixture(
            toolResolver: name => name == "add" ? ScriptMainTool.Add() : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var result = await RunScriptAsync(
            fx.Client,
            new ScriptRegistry(ScriptMainTool.Add()),
            "var n = await Tools.Call<int>(\"add\", new { a = 20, b = 22 });\nLog(\"sum is \" + n);\nreturn n + 1;",
            new ToolContext("compose", fx.Root),
            fx.Ct);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("43", result.Content, StringComparison.Ordinal);
        Assert.Contains("[log]", result.Content, StringComparison.Ordinal);
        Assert.Contains("sum is 42", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_inner_tool_approved_individually_allow_and_deny()
    {
        var seenRequests = new ConcurrentQueue<ApprovalRequest>();
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (request, _) =>
            {
                seenRequests.Enqueue(request);
                return ValueTask.FromResult(
                    request.ToolName == "bad" ? ApprovalDecision.Deny : ApprovalDecision.Allow);
            });
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        var registry = new ScriptRegistry(
            new ScriptMainTool("good", (_, _, _) => ValueTask.FromResult(new ToolResult("fine"))),
            new ScriptMainTool("bad", (_, _, _) => ValueTask.FromResult(new ToolResult("should-not-surface"))));

        var allowed = await RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"good\", new {});", new ToolContext("allow", fx.Root), fx.Ct);
        Assert.False(allowed.IsError, allowed.Content);
        Assert.Contains("fine", allowed.Content, StringComparison.Ordinal);

        var denied = await RunScriptAsync(
            fx.Client,
            registry,
            "try { await Tools.Call(\"bad\", new {}); return \"SHOULD-NOT\"; } catch (ToolCallException ex) { return \"denied-text=\" + ex.Message; }",
            new ToolContext("deny", fx.Root),
            fx.Ct);
        Assert.False(denied.IsError, denied.Content);
        Assert.Contains("denied 'bad'", denied.Content, StringComparison.Ordinal);

        Assert.Contains(seenRequests, request => request.ToolName == "good");
        Assert.Contains(seenRequests, request => request.ToolName == "bad");
    }

    [Fact]
    public async Task WhenAll_overlaps_inner_calls_up_to_run_cap()
    {
        var entered = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptMainTool Gated(string name) => new(
            name,
            async (_, _, ct) =>
            {
                entered.Enqueue(name);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                return new ToolResult(name == "left" ? "L" : "R");
            },
            ToolSideEffect.Execute);

        await using var fx = new E2eFixture(
            toolResolver: name => name is "left" or "right" ? Gated(name) : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        var options = new RunCodeProxyOptions
        {
            Execution = new Sdk.CodeExecutionOptions { MaxConcurrentToolCalls = 2 },
        };
        var registry = new ScriptRegistry(Gated("left"), Gated("right"));

        var pending = RunScriptAsync(
            fx.Client,
            registry,
            "var parts = await Task.WhenAll(Tools.Call(\"left\"), Tools.Call(\"right\"));\nreturn parts[0] + \"+\" + parts[1];",
            new ToolContext("overlap", fx.Root),
            fx.Ct,
            options);
        await fx.WaitUntilAsync(() => entered.Count == 2, "both inner calls to be in flight at once");
        release.TrySetResult();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("L+R", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inner_calls_keep_original_session_path_and_depth_and_ignore_forged_fields()
    {
        var observed = new ConcurrentQueue<ToolContext>();
        var probe = new ScriptMainTool(
            "probe",
            (call, context, _) =>
            {
                observed.Enqueue(context);
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var forged = document.RootElement.TryGetProperty("sessionId", out var sessionId);
                return ValueTask.FromResult(new ToolResult(
                    forged ? "saw-forged-arg:" + sessionId : "saw-clean"));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var result = await RunScriptAsync(
            fx.Client,
            new ScriptRegistry(probe),
            "return await Tools.Call(\"probe\", new { sessionId = \"evil\", workingDirectory = \"/evil\", depth = 99 });",
            new ToolContext("sess-7", fx.Root),
            fx.Ct);

        Assert.False(result.IsError, result.Content);
        var context = Assert.Single(observed);
        Assert.Equal("sess-7", context.SessionId);
        Assert.Equal(fx.Root, context.WorkingDirectory);
        Assert.Equal(1, context.AgentDepth);
    }

    [Fact]
    public async Task Nested_plugin_chain_A_to_B_succeeds_B_to_A_and_run_code_denied()
    {
        var resolverNames = new ConcurrentQueue<string>();
        var approvalNames = new ConcurrentQueue<string>();
        E2eFixture? box = null;
        await using var fx = new E2eFixture(
            toolResolver: name =>
            {
                resolverNames.Enqueue(name);
                var home = box ?? throw new InvalidOperationException("The fixture is not ready.");
                return name switch
                {
                    "toEchoB" => new NestingTool("toEchoB", (call, context, ct) =>
                        home.ToolOf("beta", "echo").InvokeAsync(
                            new ToolCall(call.Id, "echo", call.ArgumentsJson), context, ct)),
                    "toB" => new NestingTool("toB", (call, context, ct) =>
                        home.ToolOf("beta", "callHost").InvokeAsync(
                            new ToolCall(call.Id, "callHost", call.ArgumentsJson), context, ct)),
                    "backToA" => new NestingTool("backToA", (call, context, ct) =>
                        home.ToolOf("alpha", "echo").InvokeAsync(
                            new ToolCall(call.Id, "echo", call.ArgumentsJson), context, ct)),
                    _ => null,
                };
            },
            approvalPath: (request, _) =>
            {
                approvalNames.Enqueue(request.ToolName);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        box = fx;
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        fx.WritePlugin("beta", PluginSources.Standard("beta", echoPrefix: "b"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        await fx.ApproveViaStatusAsync("beta");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length &&
                  fx.ToolsOf("beta").Count == PluginSources.AllTools.Length,
            "tools of both plugins");

        var leg = await fx.InvokeAsync(
            "alpha", "callHost", """{"target":"toEchoB","args":{"text":"deep"}}""");
        Assert.False(leg.IsError, leg.Content);
        Assert.Equal("b:deep", leg.Content);

        var cycle = await fx.InvokeAsync(
            "alpha", "callHost", """{"target":"toB","args":{"target":"backToA","args":{}}}""");
        Assert.True(cycle.IsError, cycle.Content);
        Assert.True(
            cycle.Content.Contains("chain", StringComparison.OrdinalIgnoreCase) ||
            cycle.Content.Contains("cycle", StringComparison.OrdinalIgnoreCase),
            cycle.Content);

        var selfCall = await fx.InvokeAsync("alpha", "callHost", """{"target":"run_code","args":{}}""");
        Assert.True(selfCall.IsError);
        Assert.Contains("run_code", selfCall.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(resolverNames, name => name == "run_code");
        Assert.DoesNotContain(approvalNames, name => name == "run_code");
    }

    [Fact]
    public async Task Nested_run_code_from_script_refused()
    {
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");

        var result = await RunScriptAsync(
            fx.Client,
            new ScriptRegistry(),
            "try { await Tools.Call(\"run_code\", new {}); return \"SHOULD-NOT\"; } catch (ToolCallException ex) { return \"self=\" + ex.Denied; }",
            new ToolContext("self", fx.Root),
            fx.Ct);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("self=True", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simultaneous_roots_A_to_B_and_B_to_A_do_not_deadlock_and_report_busy()
    {
        var arrivedA = new ConcurrentQueue<string>();
        var arrivedB = new ConcurrentQueue<string>();
        var nestedOutcomes = new ConcurrentQueue<string>();
        E2eFixture? box = null;
        ITool? Resolver(string name)
        {
            var home = box ?? throw new InvalidOperationException("The fixture is not ready.");
            return name switch
            {
            "gatedToB" => new NestingTool(
                "gatedToB",
                async (call, context, ct) =>
                {
                    arrivedB.Enqueue("b");
                    await WaitForAsync(arrivedA, home.Ct);
                    var nested = await home.ToolOf("beta", "echo").InvokeAsync(
                        new ToolCall(call.Id, "echo", """{"text":"nested"}"""), context, ct);
                    var outcomeB = "nested-error=" + nested.IsError + ":" + nested.Content;
                    nestedOutcomes.Enqueue(outcomeB);
                    await WaitForCountAsync(nestedOutcomes, 2, home.Ct);
                    return new ToolResult(outcomeB, nested.IsError);
                }),
            "gatedToA" => new NestingTool(
                "gatedToA",
                async (call, context, ct) =>
                {
                    arrivedA.Enqueue("a");
                    await WaitForAsync(arrivedB, home.Ct);
                    var nested = await home.ToolOf("alpha", "echo").InvokeAsync(
                        new ToolCall(call.Id, "echo", """{"text":"nested"}"""), context, ct);
                    var outcomeA = "nested-error=" + nested.IsError + ":" + nested.Content;
                    nestedOutcomes.Enqueue(outcomeA);
                    await WaitForCountAsync(nestedOutcomes, 2, home.Ct);
                    return new ToolResult(outcomeA, nested.IsError);
                }),
            _ => null,
            };
        }

        await using var fx = new E2eFixture(
            toolResolver: Resolver,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        box = fx;
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        fx.WritePlugin("beta", PluginSources.Standard("beta", echoPrefix: "b"));
        await fx.StartAsync();
        await fx.ApproveViaStatusAsync("alpha");
        await fx.ApproveViaStatusAsync("beta");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length &&
                  fx.ToolsOf("beta").Count == PluginSources.AllTools.Length,
            "tools of both plugins");

        var rootA = fx.InvokeAsync("alpha", "callHost", """{"target":"gatedToB","args":{}}""");
        var rootB = fx.InvokeAsync("beta", "callHost", """{"target":"gatedToA","args":{}}""");
        var roots = await Task.WhenAll(rootA, rootB).WaitAsync(TimeSpan.FromSeconds(90), fx.Ct);

        Assert.All(roots, root => Assert.True(root.IsError, root.Content));
        Assert.All(
            roots,
            root => Assert.Contains("busy", root.Content, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ToolResult> RunScriptAsync(
        ExtensionHostClient client,
        ScriptRegistry registry,
        string code,
        ToolContext context,
        CancellationToken ct,
        RunCodeProxyOptions? options = null,
        double? timeoutSeconds = null)
    {
        var runCode = client.CreateRunCodeTool(registry, options);
        var arguments = timeoutSeconds is null
            ? JsonSerializer.Serialize(new { code })
            : JsonSerializer.Serialize(new { code, timeout_seconds = timeoutSeconds });
        return await runCode.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "run_code", arguments), context, ct);
    }

    private static async Task WaitForAsync(ConcurrentQueue<string> queue, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (queue.IsEmpty)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private static async Task WaitForCountAsync(ConcurrentQueue<string> queue, int count, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (queue.Count < count)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private sealed class ScriptMainTool : ITool
    {
        private readonly Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke;

        public ScriptMainTool(
            string name,
            Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke,
            ToolSideEffect sideEffect = ToolSideEffect.ReadOnly)
        {
            Definition = new ToolDefinition(name, "script-test tool " + name, """{"type":"object"}""");
            this.invoke = invoke;
            SideEffect = sideEffect;
        }

        public static ScriptMainTool Add() => new(
            "add",
            (call, _, _) =>
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var root = document.RootElement;
                var sum = root.GetProperty("a").GetInt32() + root.GetProperty("b").GetInt32();
                return ValueTask.FromResult(new ToolResult(sum.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            });

        public ToolDefinition Definition { get; }

        public ToolSideEffect SideEffect { get; }

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            invoke(call, context, ct);
    }

    private sealed class NestingTool : ITool
    {
        private readonly Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke;

        public NestingTool(
            string name,
            Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke)
        {
            Definition = new ToolDefinition(name, "nesting-test tool " + name, """{"type":"object"}""");
            this.invoke = invoke;
        }

        public ToolDefinition Definition { get; }

        public ToolSideEffect SideEffect => ToolSideEffect.Execute;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            invoke(call, context, ct);
    }

    private sealed class ScriptRegistry : IToolRegistry
    {
        private readonly Dictionary<string, ITool> tools;

        public ScriptRegistry(params ITool[] entries)
        {
            tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                tools[entry.Definition.Name] = entry;
            }

            Tools = tools.Values.ToArray();
        }

        public IReadOnlyList<ITool> Tools { get; }

        public bool TryGet(string name, out ITool tool) => tools.TryGetValue(name, out tool!);
    }
}
