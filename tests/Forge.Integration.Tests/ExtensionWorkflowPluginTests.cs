using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Workflow plugins (L0): manifest-declared tool registration with <c>Execute</c> side effect, bound
/// <c>host.tool_call</c> payloads, <c>$input</c> flow, parallel/sequential results, step failures,
/// fail-closed edits, bad documents, cancellation, timeout and revoke-during-execution.
/// Fast paths use the typed client; the wire payload test speaks raw IPC like a main program would.
/// </summary>
public sealed class ExtensionWorkflowPluginTests
{
    [Fact]
    public async Task Commit_registers_the_manifest_tool_with_execute_side_effect()
    {
        var probeCalls = 0;
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            (_, _) =>
            {
                Interlocked.Increment(ref probeCalls);
                return Task.FromResult(new ToolResult("probe-ok"));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StageWorkflowRequest(
                "greet", ExtensionAuthoringSupport.SingleStepWorkflow("probe"), toolName: "greet"),
            fx.Ct);
        Assert.Empty(staged.Diagnostics);

        var result = await fx.Client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest("greet", staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
            fx.Ct);

        Assert.Equal("greet", result.Status.PluginId);
        Assert.Equal(["greet"], result.ToolNames);
        await fx.WaitUntilAsync(() => fx.ToolsOf("greet").Count == 1, "greet tool");
        var tool = fx.ToolOf("greet", "greet");
        Assert.Equal(ToolSideEffect.Execute, tool.SideEffect);
        Assert.Equal("Approved", fx.ReadRecord("greet", "1.0.0")!.Status);

        var invoked = await fx.InvokeAsync("greet", "greet", "{}");
        Assert.False(invoked.IsError, invoked.Content);
        Assert.Equal("probe-ok", invoked.Content);
        Assert.Equal(1, Volatile.Read(ref probeCalls));
    }

    [Fact]
    public async Task Host_tool_call_carries_run_plugin_name_and_args()
    {
        await using var raw = await RawWorkflowHost.StartAsync();
        var workflow = """{"steps":[{"id":"only","tool":"step-tool","after":[],"args":{"v":1,"w":"x"}}]}""";

        var staged = await raw.StageWorkflowAsync("wf1", workflow, toolName: "do-it");
        var commit = await raw.CommitAsync("wf1", staged);
        Assert.Contains("do-it", commit.GetProperty("toolNames").EnumerateArray().Select(e => e.GetString()));

        var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
        var first = await raw.ExecuteAsync("wf1", "do-it", """{"a":1}""", runId);
        Assert.False(first.GetProperty("isError").GetBoolean(), first.GetProperty("content").GetString());
        Assert.Equal("step-ok", first.GetProperty("content").GetString());

        var payload = Assert.Single(raw.ToolCalls);
        Assert.Equal(runId, payload.GetProperty("runId").GetString());
        Assert.Equal("wf1", payload.GetProperty("pluginId").GetString());
        Assert.Equal("step-tool", payload.GetProperty("name").GetString());
        Assert.Equal("""{"v":1,"w":"x"}""", payload.GetProperty("args").GetRawText());

        // The run id belongs to this call: a second execution gets a different one.
        var runId2 = "run-" + Guid.NewGuid().ToString("N")[..12];
        await raw.ExecuteAsync("wf1", "do-it", """{"a":2}""", runId2);
        Assert.Equal(2, raw.ToolCalls.Count);
        Assert.Equal(runId2, raw.ToolCalls[1].GetProperty("runId").GetString());
        Assert.NotEqual(
            raw.ToolCalls[0].GetProperty("runId").GetString(),
            raw.ToolCalls[1].GetProperty("runId").GetString());
    }

    [Fact]
    public async Task Input_arguments_reach_steps_through_input_binding()
    {
        ExtensionAuthoringSupport.CapturedToolCall? seen = null;
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            (call, _) =>
            {
                seen = call;
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return Task.FromResult(new ToolResult("got:" + document.RootElement.GetProperty("text").GetString()));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var workflow = """{"steps":[{"id":"only","tool":"probe","args":{"text":{"$ref":"$input.label"}}}]}""";
        await CommitWorkflowAsync(fx, "inbind", workflow, "run-it");

        var result = await fx.InvokeAsync("inbind", "run-it", """{"label":"hello","other":1}""");
        Assert.False(result.IsError, result.Content);
        Assert.Equal("got:hello", result.Content);
        Assert.NotNull(seen);
        Assert.Equal("probe", seen!.Name);
        Assert.Equal("hello", JsonDocument.Parse(seen.ArgumentsJson).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Parallel_branches_join_and_terminal_step_wins()
    {
        var s1Arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s2Arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            async (call, _) =>
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var tag = document.RootElement.GetProperty("tag").GetString();
                switch (tag)
                {
                    case "s1":
                        s1Arrived.TrySetResult();
                        await s2Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        return new ToolResult("one");
                    case "s2":
                        s2Arrived.TrySetResult();
                        await s1Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        return new ToolResult("two");
                    default:
                        return new ToolResult("three");
                }
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var workflow = """{"steps":[{"id":"s1","tool":"probe","after":[],"args":{"tag":"s1"}},{"id":"s2","tool":"probe","after":[],"args":{"tag":"s2"}},{"id":"s3","tool":"probe","after":["s1","s2"],"args":{"tag":"s3"}}]}""";
        await CommitWorkflowAsync(fx, "fan", workflow, "run-it");

        var result = await fx.InvokeAsync("fan", "run-it", "{}");
        Assert.False(result.IsError, result.Content);
        Assert.Equal("three", result.Content);
        Assert.Equal(3, probe.Invocations);
    }

    [Fact]
    public async Task Failing_step_reports_step_and_reason_and_skips_dependents()
    {
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            (call, _) =>
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var mode = document.RootElement.GetProperty("mode").GetString();
                return Task.FromResult(
                    mode == "fail" ? new ToolResult("kaput-detail", IsError: true) : new ToolResult("fine"));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var workflow = """{"steps":[{"id":"bad","tool":"probe","after":[],"args":{"mode":"fail"}},{"id":"after","tool":"probe","args":{"mode":"ok"}}]}""";
        await CommitWorkflowAsync(fx, "stepfail", workflow, "run-it");

        var result = await fx.InvokeAsync("stepfail", "run-it", "{}");
        Assert.True(result.IsError);
        Assert.Contains("bad", result.Content, StringComparison.Ordinal);
        Assert.Contains("kaput-detail", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            probe.Calls,
            call =>
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return document.RootElement.GetProperty("mode").GetString() == "ok";
            });
    }

    [Fact]
    public async Task Unknown_step_tool_fails_the_step_with_names()
    {
        await using var fx = new E2eFixture(
            toolResolver: _ => null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var workflow = """{"steps":[{"id":"lost","tool":"no-such-tool","after":[]}]}""";
        await CommitWorkflowAsync(fx, "unknowntool", workflow, "run-it");

        var result = await fx.InvokeAsync("unknowntool", "run-it", "{}");
        Assert.True(result.IsError);
        Assert.Contains("lost", result.Content, StringComparison.Ordinal);
        Assert.Contains("no-such-tool", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edited_workflow_json_is_fail_closed_for_execute_and_approve()
    {
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            (call, _) =>
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return Task.FromResult(new ToolResult("v:" + document.RootElement.GetProperty("tag").GetString()));
            });
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();

        var v1 = """{"steps":[{"id":"only","tool":"probe","after":[],"args":{"tag":"one"}}]}""";
        await CommitWorkflowAsync(fx, "doc", v1, "run-it");
        var before = await fx.InvokeAsync("doc", "run-it", "{}");
        Assert.Equal("v:one", before.Content);
        var recordBefore = fx.ReadRecord("doc", "1.0.0")!;

        var liveDoc = Path.Combine(fx.PluginsDirectory, "doc", "workflow.json");
        File.WriteAllText(liveDoc, """{"steps":[{"id":"only","tool":"probe","after":[],"args":{"tag":"two"}}]}""");

        var blocked = await fx.InvokeAsync("doc", "run-it", "{}");
        Assert.True(blocked.IsError);

        var stale = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("doc", "1.0.0", recordBefore.SourceHash, recordBefore.DependencyHash, recordBefore.SdkVersion, fx.Ct));
        Assert.Equal("approval_mismatch", stale.Code);

        await fx.Client.RefreshPluginsAsync(fx.Ct);
        await fx.WaitUntilAsync(() => fx.ToolsOf("doc").Count == 0, "doc tools to be unregistered");

        await fx.ApproveViaStatusAsync("doc");
        await fx.WaitUntilAsync(() => fx.ToolsOf("doc").Count == 1, "doc tool after re-approval");
        var after = await fx.InvokeAsync("doc", "run-it", "{}");
        Assert.False(after.IsError, after.Content);
        Assert.Equal("v:two", after.Content);
    }

    [Fact]
    public async Task Unparsable_workflow_json_has_diagnostics_and_cannot_commit()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var staged = await fx.Client.StageExtensionAsync(
            new Sdk.ExtensionStageRequest(
                "badjson",
                "1.0.0",
                Sdk.ExtensionKinds.Workflow,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow.json"] = "{oops" },
                [],
                [],
                ExtensionAuthoringSupport.WorkflowTool("run-it")),
            fx.Ct);
        Assert.NotEmpty(staged.Diagnostics);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.CommitExtensionAsync(
                new Sdk.ExtensionCommitRequest("badjson", staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
                fx.Ct),
            "invalid_extension");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "badjson")));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "badjson", "1.0.0"));
        Assert.Empty(fx.Client.Tools);
    }

    [Fact]
    public async Task Invalid_workflow_has_diagnostics_and_cannot_commit()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var workflow = """{"steps":[{"id":"a","tool":"probe","after":[]},{"id":"b","tool":"probe","args":{"v":{"$ref":"$.steps.ghost.json"}}}]}""";
        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StageWorkflowRequest("badwf", workflow), fx.Ct);
        Assert.NotEmpty(staged.Diagnostics);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.CommitExtensionAsync(
                new Sdk.ExtensionCommitRequest("badwf", staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
                fx.Ct),
            "invalid_extension");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.LiveDirectory(fx.PluginsDirectory, "badwf")));
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "badwf", "1.0.0"));
    }

    [Fact]
    public async Task Cancelled_execution_unblocks_the_caller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ToolResult("never");
            },
            entered: () => entered.TrySetResult());
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();
        await CommitWorkflowAsync(fx, "cancelwf", ExtensionAuthoringSupport.SingleStepWorkflow("probe"), "run-it");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var tool = fx.ToolOf("cancelwf", "run-it");
        var pending = tool.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "run-it", "{}"),
            new ToolContext("cancel-session", fx.Root),
            cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), fx.Ct);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("Approved", fx.ReadRecord("cancelwf", "1.0.0")!.Status);
    }

    [Fact]
    public async Task Timed_out_execution_reports_timeout()
    {
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ToolResult("never");
            });
        await using var fx = new E2eFixture(
            hostArguments: ["--tool-timeout", "2"],
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();
        await CommitWorkflowAsync(fx, "slowwf", ExtensionAuthoringSupport.SingleStepWorkflow("probe"), "run-it");

        // The host links the deadline into the same token the engine sees as caller cancellation, and the
        // engine reports caller cancellation first: a deadline expiry surfaces as Cancelled ("run cancelled"),
        // not TimedOut (the engine-level TimedOut path is covered by unit tests with a manual clock). What the
        // integration proves is fail-closed bounded completion: the call ends promptly with isError set.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await fx.InvokeAsync("slowwf", "run-it", "{}");
        clock.Stop();
        Assert.True(result.IsError);
        Assert.Contains("cancelled", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the timed-out run took {clock.Elapsed}");
    }

    [Fact]
    public async Task Revoke_during_execution_ends_cleanly_with_no_new_runs()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ExtensionAuthoringSupport.ProbeTool(
            "probe",
            async (_, _) => new ToolResult(await gate.Task),
            entered: () => entered.TrySetResult());
        await using var fx = new E2eFixture(
            toolResolver: name => name == probe.Definition.Name ? probe : null,
            approvalPath: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await fx.StartAsync();
        await CommitWorkflowAsync(fx, "wfr", ExtensionAuthoringSupport.SingleStepWorkflow("probe"), "run-it");
        await fx.WaitUntilAsync(() => fx.ToolsOf("wfr").Count == 1, "wfr tool");
        var stale = fx.ToolOf("wfr", "run-it");

        var running = fx.InvokeAsync("wfr", "run-it", "{}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), fx.Ct);

        var revoking = fx.Client.RevokePluginAsync("wfr", "1.0.0", fx.Ct);
        // The revoke unregisters first and then waits for the in-flight run: observing the empty
        // tool table proves it reached the wait, so releasing the gate cannot hide a deadlock.
        await fx.WaitUntilAsync(() => fx.ToolsOf("wfr").Count == 0, "wfr tools revoked");
        gate.TrySetResult("late");

        await running.WaitAsync(TimeSpan.FromSeconds(30), fx.Ct);
        await revoking.WaitAsync(TimeSpan.FromSeconds(30), fx.Ct);

        Assert.Equal("Draft", fx.ReadRecord("wfr", "1.0.0")!.Status);
        Assert.Empty(fx.ToolsOf("wfr"));
        var again = await stale.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "run-it", "{}"),
            new ToolContext("wfr-session", fx.Root),
            fx.Ct);
        Assert.True(again.IsError);
    }

    private static async Task CommitWorkflowAsync(E2eFixture fx, string id, string workflowJson, string toolName)
    {
        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StageWorkflowRequest(id, workflowJson, toolName: toolName), fx.Ct);
        Assert.Empty(staged.Diagnostics);
        var result = await fx.Client.CommitExtensionAsync(
            new Sdk.ExtensionCommitRequest(id, staged.SourceHash, staged.DependencyHash, staged.SdkVersion),
            fx.Ct);
        Assert.Equal([toolName], result.ToolNames);
        await fx.WaitUntilAsync(() => fx.ToolsOf(id).Count == 1, id + " tool");
    }

    /// <summary>Raw main program: real host process, scripted peer that answers <c>host.tool_call</c>.</summary>
    private sealed class RawWorkflowHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly NamedPipeClientTransport transport;
        private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(180));

        private RawWorkflowHost(Process process, NamedPipeClientTransport transport, IpcPeer peer, string root)
        {
            this.process = process;
            this.transport = transport;
            Peer = peer;
            Root = root;
            PluginsDirectory = Path.Combine(root, "plugins");
            peer.RequestReceived += OnRequestAsync;
            peer.NotificationReceived += OnNotification;
        }

        public IpcPeer Peer { get; }

        public string Root { get; }

        public string PluginsDirectory { get; }

        public List<JsonElement> ToolCalls { get; } = [];

        public List<(string PluginId, List<string> Tools)> Registrations { get; } = [];

        private Task OnRequestAsync(IpcMessage message, CancellationToken ct)
        {
            if (string.Equals(message.Operation, Sdk.ExtensionProtocol.HostToolCall, StringComparison.Ordinal))
            {
                lock (ToolCalls)
                {
                    ToolCalls.Add(message.Arguments!.Value.Clone());
                }

                return Peer.RespondAsync(
                    message.Id,
                    JsonSerializer.SerializeToElement(new { content = "step-ok", isError = false }),
                    CancellationToken.None);
            }

            return Peer.RespondAsync(
                message.Id, JsonDocument.Parse("{}").RootElement.Clone(), CancellationToken.None);
        }

        private void OnNotification(IpcMessage message)
        {
            if (!string.Equals(message.Operation, Sdk.ExtensionProtocol.ToolsRegister, StringComparison.Ordinal) ||
                message.Arguments is not { ValueKind: JsonValueKind.Object } args)
            {
                return;
            }

            var tools = args.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
                .ToList();
            lock (Registrations)
            {
                Registrations.Add((args.GetProperty("pluginId").GetString() ?? string.Empty, tools));
            }
        }

        public static async Task<RawWorkflowHost> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-wfraw-" + Guid.NewGuid().ToString("N")[..10]);
            var plugins = Path.Combine(root, "plugins");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(data);
            var pipe = "forge-wfraw-" + Guid.NewGuid().ToString("N")[..16];
            var info = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(E2eFixture.HostAssemblyPath);
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(pipe);
            info.ArgumentList.Add("--plugins");
            info.ArgumentList.Add(plugins);
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(data);
            var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            NamedPipeClientTransport? transport = null;
            while (transport is null)
            {
                var candidate = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
                try
                {
                    await candidate.StartAsync(startup.Token);
                    transport = candidate;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                {
                    await candidate.DisposeAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(50), startup.Token);
                }
            }

            var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
            await peer.StartAsync(startup.Token);
            var host = new RawWorkflowHost(process, transport, peer, root);
            await host.SendAsync(Sdk.ExtensionProtocol.PluginsRefresh, JsonDocument.Parse("{}").RootElement.Clone());
            return host;
        }

        public async Task<JsonElement> StageWorkflowAsync(string id, string workflowJson, string toolName)
        {
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["pluginId"] = id,
                ["version"] = "1.0.0",
                ["kind"] = Sdk.ExtensionKinds.Workflow,
                ["files"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["workflow.json"] = workflowJson },
                ["permissions"] = Array.Empty<object>(),
                ["dependencies"] = Array.Empty<object>(),
                ["tool"] = new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["description"] = "raw workflow tool",
                    ["inputSchemaJson"] = """{"type":"object"}""",
                },
            });
            return (await SendAsync(Sdk.ExtensionProtocol.ExtensionsStage, args)).Clone();
        }

        public async Task<JsonElement> CommitAsync(string id, JsonElement staged)
        {
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["pluginId"] = id,
                ["sourceHash"] = staged.GetProperty("sourceHash").GetString(),
                ["dependencyHash"] = staged.GetProperty("dependencyHash").GetString(),
                ["sdkVersion"] = staged.GetProperty("sdkVersion").GetString(),
            });
            return (await SendAsync(Sdk.ExtensionProtocol.ExtensionsCommit, args)).Clone();
        }

        public async Task<JsonElement> ExecuteAsync(string pluginId, string tool, string argsJson, string runId)
        {
            using var parsed = JsonDocument.Parse(argsJson);
            var workingDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["pluginId"] = pluginId,
                ["tool"] = tool,
                ["args"] = parsed.RootElement.Clone(),
                ["sessionId"] = "raw-session",
                ["workingDir"] = workingDir,
                ["depth"] = 0,
                ["context"] = new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["sessionId"] = "raw-session",
                    ["workingDirectory"] = workingDir,
                    ["depth"] = 0,
                    ["pluginChain"] = Array.Empty<string>(),
                },
                ["timeoutMilliseconds"] = 60000,
            });
            return (await SendAsync(Sdk.ExtensionProtocol.PluginExecute, args)).Clone();
        }

        private async Task<JsonElement> SendAsync(string operation, JsonElement args) =>
            (await Peer.SendRequestAsync(operation, args, budget.Token)).Result!.Value.Clone();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Peer.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
            }

            await Peer.DisposeAsync();
            await transport.DisposeAsync();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            process.Dispose();
            budget.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
