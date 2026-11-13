using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Forge.Ipc;
using Forge.ScriptRuntime;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Script execution behavior of the migrated <c>run_code</c>: every in-flight script owns a fresh
/// single-use executor process, a timed-out or cancelled uncooperative script is killed with its process tree without
/// failing its neighbors, the next call gets a new process, bounded queues fail fast instead of starting new executors,
/// and host shutdown drains everything. A real executor is also spoken to directly to prove it runs one image once
/// while refusing every plugin operation.</summary>
public sealed class ScriptPoolTests
{
    [Fact]
    public async Task Infinite_loop_timeout_then_recovers_without_failing_concurrent_script()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry();

        var burning = RunScriptAsync(fx.Client, registry, "while (true) {}", "burn", fx.Root, fx.Ct, timeoutSeconds: 3);
        var steady = RunScriptAsync(fx.Client, registry, "return 7 * 6;", "steady", fx.Root, fx.Ct);

        var steadyResult = await steady.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.False(steadyResult.IsError, steadyResult.Content);
        Assert.Contains("42", steadyResult.Content, StringComparison.Ordinal);

        var burned = await burning.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.True(burned.IsError);
        Assert.Contains("3 s", burned.Content, StringComparison.Ordinal);

        var followUp = await RunScriptAsync(fx.Client, registry, "return 1 + 1;", "follow", fx.Root, fx.Ct);
        Assert.False(followUp.IsError, followUp.Content);
        Assert.Contains("2", followUp.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncooperative_cancel_then_recovers()
    {
        var entered = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var enteredTool = new ScriptMainTool(
            "entered",
            (_, _, _) =>
            {
                entered.Enqueue("yes");
                return ValueTask.FromResult(new ToolResult("go"));
            });
        await using var fx = E2eFixture.AllowingApprovals(enteredTool);
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry(enteredTool);

        using var runBudget = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var pending = RunScriptAsync(
            fx.Client, registry, "await Tools.Call(\"entered\", new {});\nwhile (true) {}", "cancel", fx.Root, runBudget.Token);
        await fx.WaitUntilAsync(() => !entered.IsEmpty, "the script to start");
        runBudget.Cancel();

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the cancelled caller waited {clock.Elapsed}");

        var followUp = await RunScriptAsync(fx.Client, registry, "return 9;", "follow", fx.Root, fx.Ct)
            .WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.False(followUp.IsError, followUp.Content);
        Assert.Contains("9", followUp.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pool_of_one_queued_script_times_out_without_starting()
    {
        var entered = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        ScriptMainTool Gate(string name) => new(
            name,
            async (_, _, ct) =>
            {
                if (name == "gate")
                {
                    entered.Enqueue("gate");
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    return new ToolResult("gated-done");
                }

                Interlocked.Increment(ref probeCalls);
                return new ToolResult("probe-ran");
            },
            ToolSideEffect.Execute);

        await using var fx = E2eFixture.AllowingApprovals(
            name => name is "gate" or "probeB" ? Gate(name) : null, ["--script-workers", "1"]);
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry(Gate("gate"), Gate("probeB"));

        var holder = RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"gate\", new {});", "holder", fx.Root, fx.Ct,
            timeoutSeconds: 60);
        await fx.WaitUntilAsync(() => !entered.IsEmpty, "the first script to hold the only worker");

        var queued = await RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"probeB\", new {});", "queued", fx.Root, fx.Ct,
            timeoutSeconds: 3).WaitAsync(TimeSpan.FromSeconds(30), fx.Ct);
        Assert.True(queued.IsError, queued.Content);
        Assert.Equal(0, Volatile.Read(ref probeCalls));

        release.TrySetResult();
        var held = await holder.WaitAsync(TimeSpan.FromSeconds(60), fx.Ct);
        Assert.False(held.IsError, held.Content);
        Assert.Contains("gated-done", held.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Precanceled_script_never_dispatches()
    {
        var probeCalls = 0;
        var probe = new ScriptMainTool(
            "probeB",
            (_, _, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                return ValueTask.FromResult(new ToolResult("probe-ran"));
            });
        await using var fx = E2eFixture.AllowingApprovals(probe);
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry(probe);

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"probeB\", new {});", "pre", fx.Root,
            alreadyCancelled.Token));
        Assert.Equal(0, Volatile.Read(ref probeCalls));
    }

    [Fact]
    public async Task Host_stop_drains_running_and_queued_scripts()
    {
        var entered = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        var gate = new ScriptMainTool(
            "gate",
            async (_, _, ct) =>
            {
                entered.Enqueue("gate");
                try
                {
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
                    return new ToolResult("gated-done");
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref cancelled, 1);
                    throw;
                }
            },
            ToolSideEffect.Execute);
        await using var fx = E2eFixture.AllowingApprovals(gate, ["--script-workers", "1"]);
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry(gate);
        var hostPid = fx.Client.HostProcessId ?? throw new InvalidOperationException("The client did not start a host.");

        var running = RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"gate\", new {});", "running", fx.Root, fx.Ct);
        await fx.WaitUntilAsync(() => !entered.IsEmpty, "the running script to hold the worker");
        var queued = RunScriptAsync(
            fx.Client, registry, "await Task.Delay(60000);\nreturn \"b\";", "queued", fx.Root, fx.Ct);

        await fx.Client.StopAsync(fx.Ct);
        Assert.Equal(HostStatus.Stopped, fx.Client.Status);
        var runningEnding = await CaptureAsync(running).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var queuedEnding = await CaptureAsync(queued).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(
            runningEnding is Exception || (runningEnding as ToolResult)?.IsError == true,
            "the running script must not succeed after the host stopped.");
        Assert.True(
            queuedEnding is Exception || (queuedEnding as ToolResult)?.IsError == true,
            "the queued script must not succeed after the host stopped.");
        Assert.Equal(1, Volatile.Read(ref cancelled));
        await fx.WaitForExitAsync(hostPid, "the host");
        Assert.False(E2eFixture.PipeAcceptsConnections(fx.PipeName));
    }

    [Fact]
    public async Task Short_caller_budget_stops_long_script()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry();

        var clock = Stopwatch.StartNew();
        var stopped = await RunScriptAsync(
            fx.Client, registry, "while (true) {}", "short", fx.Root, fx.Ct, timeoutSeconds: 2);
        clock.Stop();

        Assert.True(stopped.IsError);
        Assert.Contains("2 s", stopped.Content, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the short budget took {clock.Elapsed}");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"after"}""", "a:after");
    }

    [Fact]
    public async Task Single_use_executor_refuses_plugin_ops_and_runs_one_script()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var executor = await DirectWorker.StartExecutorAsync(startup.Token);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        foreach (var operation in new[]
        {
            Sdk.ExtensionProtocol.WorkerLoad, Sdk.ExtensionProtocol.WorkerExecute,
            Sdk.ExtensionProtocol.WorkerCommand, Sdk.ExtensionProtocol.WorkerPrompt,
        })
        {
            var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
                executor.SendAsync(operation, RawHost.EmptyObject(), budget.Token));
            Assert.Equal("unsupported_operation", refused.Code);
        }

        // Nothing to run before an image is loaded.
        var notLoaded = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            executor.SendAsync(ScriptRuntime.ScriptExecutorProtocol.Run, RunPayload("r1", "return 1 + 1;"), budget.Token));
        Assert.Equal("not_loaded", notLoaded.Code);

        var artifact = await CompileArtifactAsync("return 1 + 1;");
        await executor.SendAsync(ScriptRuntime.ScriptExecutorProtocol.Load, LoadPayload(artifact), budget.Token);

        var response = await executor.SendAsync(
            ScriptRuntime.ScriptExecutorProtocol.Run,
            RunPayload("r1", "return 1 + 1;"),
            budget.Token);
        var output = response.Result!.Value.Deserialize(ScriptRuntime.ScriptExecutorJsonContext.Default.CodeExecutionResult);
        Assert.NotNull(output);
        Assert.False(output!.IsError, output.Content);
        Assert.Equal("2", output.Content.Trim());

        // The executor is single use: a second run is refused, never served.
        var second = await Assert.ThrowsAsync<IpcRemoteException>(() => executor.SendAsync(
            ScriptRuntime.ScriptExecutorProtocol.Run, RunPayload("r2", "return 1 + 1;"), budget.Token));
        Assert.Equal("already_ran", second.Code);
        var secondLoad = await Assert.ThrowsAsync<IpcRemoteException>(() => executor.SendAsync(
            ScriptRuntime.ScriptExecutorProtocol.Load, LoadPayload(artifact), budget.Token));
        Assert.Equal("already_loaded", secondLoad.Code);

        await executor.SendAsync(ScriptRuntime.ScriptExecutorProtocol.Stop, RawHost.EmptyObject(), budget.Token);
    }

    [Fact]
    public async Task Context_missing_at_the_executor_boundary_rejected()
    {
        await using var executor = await DirectWorker.StartExecutorAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var artifact = await CompileArtifactAsync("return 1;");
        await executor.SendAsync(ScriptRuntime.ScriptExecutorProtocol.Load, LoadPayload(artifact), budget.Token);

        var withoutContext = JsonDocument.Parse(
            """{"timeoutMilliseconds":5000,"tools":[],"options":{}}""").RootElement.Clone();
        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            executor.SendAsync(ScriptRuntime.ScriptExecutorProtocol.Run, withoutContext, budget.Token));
        Assert.Equal("invalid_arguments", refused.Code);
    }

    private static async Task<Forge.Scripting.ScriptCompilation> CompileArtifactAsync(string code)
    {
        var compiled = await new Forge.Scripting.ScriptCodeCompiler()
            .CompileAsync(code, new Sdk.CodeExecutionOptions(), CancellationToken.None);
        Assert.True(compiled.Ok, compiled.Error);
        return compiled;
    }

    private static JsonElement LoadPayload(Forge.Scripting.ScriptCompilation compiled) =>
        JsonSerializer.SerializeToElement(
            new ScriptRuntime.ScriptArtifactLoadRequest(
                ScriptRuntime.ScriptArtifact.Encode(compiled.Image!),
                compiled.Descriptor!),
            ScriptRuntime.ScriptExecutorJsonContext.Default.ScriptArtifactLoadRequest);

    private static JsonElement RunPayload(string runId, string code) =>
        JsonSerializer.SerializeToElement(
            new ScriptRuntime.ScriptRunRequest(
                new Sdk.ExtensionCallContext(runId, "sess", Path.GetTempPath(), 0, []),
                10_000,
                [],
                new Sdk.CodeExecutionOptions()),
            ScriptRuntime.ScriptExecutorJsonContext.Default.ScriptRunRequest);

    [Fact]
    public async Task Bounds_truncate_result_and_inner_calls()
    {
        var big = new ScriptMainTool(
            "big",
            (_, _, _) => ValueTask.FromResult(new ToolResult(new string('x', 500))));
        await using var fx = E2eFixture.AllowingApprovals(big);
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var options = new RunCodeProxyOptions
        {
            Execution = new Sdk.CodeExecutionOptions { MaxResultChars = 64, MaxInnerResultChars = 16 },
        };
        var registry = new ScriptRegistry(big);

        var longResult = await RunScriptAsync(
            fx.Client, registry, "return new string('x', 500);", "bounds", fx.Root, fx.Ct, options: options);
        Assert.False(longResult.IsError, longResult.Content);
        Assert.StartsWith(new string('x', 64), longResult.Content, StringComparison.Ordinal);
        Assert.Contains("[... result truncated: kept 64 of ", longResult.Content, StringComparison.Ordinal);

        var innerOptions = new RunCodeProxyOptions
        {
            Execution = new Sdk.CodeExecutionOptions { MaxResultChars = 40_000, MaxInnerResultChars = 16 },
        };
        var innerResult = await RunScriptAsync(
            fx.Client, registry, "return await Tools.Call(\"big\", new {});", "inner", fx.Root, fx.Ct,
            options: innerOptions);
        Assert.False(innerResult.IsError, innerResult.Content);
        Assert.StartsWith(new string('x', 16), innerResult.Content, StringComparison.Ordinal);
        Assert.Contains("[... tool result truncated: kept 16 of ", innerResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_compile_does_not_poison_next_call()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        await fx.StartWithStatusApprovedPluginAsync("alpha", "a");
        var registry = new ScriptRegistry();

        var first = await RunScriptAsync(fx.Client, registry, "var x = ;\nreturn 1;", "bad", fx.Root, fx.Ct);
        Assert.True(first.IsError);
        var second = await RunScriptAsync(fx.Client, registry, "var x = ;\nreturn 1;", "bad", fx.Root, fx.Ct);
        Assert.True(second.IsError);
        Assert.Equal(first.Content, second.Content);

        var good = await RunScriptAsync(fx.Client, registry, "return 3 * 3;", "good", fx.Root, fx.Ct);
        Assert.False(good.IsError, good.Content);
        Assert.Contains("9", good.Content, StringComparison.Ordinal);

        var badAgain = await RunScriptAsync(fx.Client, registry, "var x = ;\nreturn 1;", "bad", fx.Root, fx.Ct);
        Assert.True(badAgain.IsError);
    }

    [Fact]
    public void Code_execute_wire_context_is_camelCase()
    {
        var request = new Sdk.CodeExecutionRequest(
            "return 1;",
            new Sdk.ExtensionCallContext("run-1", "sess", "/tmp/work", 2, ["alpha", "beta"]),
            5_000,
            [new Sdk.ToolDeclaration("add", "adds", "{}", Sdk.ToolSideEffect.ReadOnly)],
            new Sdk.CodeExecutionOptions());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            request, Sdk.ExtensionExecutionJsonContext.Default.CodeExecutionRequest));
        var root = document.RootElement;

        Assert.Equal("return 1;", root.GetProperty("code").GetString());
        var context = root.GetProperty("context");
        Assert.Equal("run-1", context.GetProperty("runId").GetString());
        Assert.Equal("sess", context.GetProperty("sessionId").GetString());
        Assert.Equal("/tmp/work", context.GetProperty("workingDirectory").GetString());
        Assert.Equal(2, context.GetProperty("depth").GetInt32());
        Assert.Equal(
            new[] { "alpha", "beta" },
            context.GetProperty("pluginChain").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(5_000, root.GetProperty("timeoutMilliseconds").GetInt32());

        var tool = root.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("add", tool.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Number, tool.GetProperty("sideEffect").ValueKind);
        Assert.Equal(0, tool.GetProperty("sideEffect").GetInt32());

        var options = root.GetProperty("options");
        Assert.Equal(8, options.GetProperty("maxConcurrentToolCalls").GetInt32());

        foreach (var property in root.EnumerateObject())
        {
            Assert.True(char.IsLower(property.Name[0]), $"property '{property.Name}' is not camelCase.");
        }

        foreach (var property in context.EnumerateObject())
        {
            Assert.True(char.IsLower(property.Name[0]), $"property '{property.Name}' is not camelCase.");
        }
    }

    private static async Task<object> CaptureAsync(Task<ToolResult> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Task<ToolResult> RunScriptAsync(
        ExtensionHostClient client,
        ScriptRegistry registry,
        string code,
        string session,
        string workingDirectory,
        CancellationToken ct,
        double? timeoutSeconds = null,
        RunCodeProxyOptions? options = null)
    {
        var runCode = client.CreateRunCodeTool(registry, options);
        var arguments = timeoutSeconds is null
            ? JsonSerializer.Serialize(new { code })
            : JsonSerializer.Serialize(new { code, timeout_seconds = timeoutSeconds });
        return runCode.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "run_code", arguments),
            new ToolContext(session, workingDirectory),
            ct).AsTask();
    }

    private sealed class ScriptMainTool : ITool
    {
        private readonly Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke;

        public ScriptMainTool(
            string name,
            Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> invoke,
            ToolSideEffect sideEffect = ToolSideEffect.ReadOnly)
        {
            Definition = new ToolDefinition(name, "script-pool tool " + name, """{"type":"object"}""");
            this.invoke = invoke;
            SideEffect = sideEffect;
        }

        public ToolDefinition Definition { get; }

        public ToolSideEffect SideEffect { get; }

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
