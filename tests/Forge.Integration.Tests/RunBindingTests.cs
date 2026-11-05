using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>Run binding at the main-program boundary: every <c>host.tool_call</c> that is not bound to a live
/// run of this connection generation is refused before the tool resolver and the approval path run. A minimal
/// in-test fake host speaks the real pipe protocol to a real <c>ExtensionHostClient</c>; no fake worker binary
/// is involved and nothing here bypasses the client's own checks.</summary>
public sealed class RunBindingTests
{
    [Fact]
    public async Task Missing_runId_rejected_before_resolver_and_approval()
    {
        await using var fake = await FakeHost.StartAsync();
        var result = await fake.CallHostToolAsync(new { pluginId = "p1", name = "t1", args = new { } });

        Assert.True(result.IsError, result.Content);
        Assert.Contains("not bound to a run id", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);
    }

    [Fact]
    public async Task Blank_runId_and_legacy_shape_rejected_before_resolver_and_approval()
    {
        await using var fake = await FakeHost.StartAsync();

        var blank = await fake.CallHostToolAsync(new { runId = "", pluginId = "p1", name = "t1", args = new { } });
        Assert.True(blank.IsError);
        Assert.Contains("run id", blank.Content, StringComparison.OrdinalIgnoreCase);

        var legacy = await fake.CallHostToolAsync(
            new { pluginId = "p1", name = "t1", sessionId = "s", workingDir = fake.Root, depth = 0 });
        Assert.True(legacy.IsError);
        Assert.Contains("not bound to a run id", legacy.Content, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);
    }

    [Fact]
    public async Task Unknown_runId_rejected_before_resolver_and_approval()
    {
        await using var fake = await FakeHost.StartAsync();
        var result = await fake.CallHostToolAsync(
            new { runId = "deadbeefcafe0001", pluginId = "p1", name = "t1", args = new { } });

        Assert.True(result.IsError);
        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);
    }

    [Fact]
    public async Task Cross_plugin_runId_rejected_before_resolver_and_approval()
    {
        await using var fake = await FakeHost.StartAsync();
        await fake.RegisterToolAsync("p1", "t1");
        await fake.RegisterToolAsync("p2", "t2");

        fake.HoldExecutes = true;
        var run = fake.InvokeAsync("p1", "t1");
        var runId = await fake.WaitForExecuteAsync("p1", "t1");

        var forged = await fake.CallHostToolAsync(new { runId, pluginId = "p2", name = "t2", args = new { } });
        Assert.True(forged.IsError);
        Assert.Contains("p2", forged.Content, StringComparison.Ordinal);
        Assert.Contains("p1", forged.Content, StringComparison.Ordinal);
        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);

        fake.CompleteExecute("ok-t1");
        var completed = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(completed.IsError, completed.Content);
        Assert.Equal("ok-t1", completed.Content);
    }

    [Fact]
    public async Task Replayed_runId_rejected_after_the_run_ended()
    {
        await using var fake = await FakeHost.StartAsync();
        await fake.RegisterToolAsync("p1", "t1");

        var completed = await fake.InvokeAsync("p1", "t1");
        Assert.False(completed.IsError, completed.Content);
        var runId = Assert.Single(fake.ExecuteRunIds);

        var replay = await fake.CallHostToolAsync(new { runId, pluginId = "p1", name = "t1", args = new { } });
        Assert.True(replay.IsError);
        Assert.Contains("in flight", replay.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);
    }

    [Fact]
    public async Task Run_code_name_rejected_even_for_a_live_bound_run()
    {
        await using var fake = await FakeHost.StartAsync();
        await fake.RegisterToolAsync("p1", "t1");

        fake.HoldExecutes = true;
        var run = fake.InvokeAsync("p1", "t1");
        var runId = await fake.WaitForExecuteAsync("p1", "t1");

        var nested = await fake.CallHostToolAsync(new { runId, pluginId = "p1", name = "run_code", args = new { } });
        Assert.True(nested.IsError);
        Assert.Contains("run_code", nested.Content, StringComparison.Ordinal);
        Assert.Empty(fake.ResolverCalls);
        Assert.Empty(fake.ApprovalCalls);

        fake.CompleteExecute("ok-t1");
        var completed = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(completed.IsError, completed.Content);
    }

    [Fact]
    public async Task Live_bound_call_flows_to_resolver_and_approval()
    {
        await using var fake = await FakeHost.StartAsync();
        await fake.RegisterToolAsync("p1", "t1");

        fake.HoldExecutes = true;
        var run = fake.InvokeAsync("p1", "t1");
        var runId = await fake.WaitForExecuteAsync("p1", "t1");

        var forwarded = await fake.CallHostToolAsync(new { runId, pluginId = "p1", name = "inner", args = new { v = 1 } });
        Assert.False(forwarded.IsError, forwarded.Content);
        Assert.Equal("inner-ok", forwarded.Content);
        Assert.Contains("inner", fake.ResolverCalls);
        var approval = Assert.Single(fake.ApprovalCalls);
        Assert.Equal("inner", approval.ToolName);

        fake.CompleteExecute("ok-t1");
        var completed = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(completed.IsError, completed.Content);
    }

    [Fact]
    public async Task Malformed_contributions_dropped_with_diagnostics_and_valid_kept()
    {
        await using var fake = await FakeHost.StartAsync();
        var set = new Sdk.PluginContributionSet
        {
            Commands =
            [
                new Sdk.CommandDeclaration("", "nameless", ""),
                new Sdk.CommandDeclaration("kept", "kept command", ""),
                new Sdk.CommandDeclaration("kept", "duplicate", ""),
            ],
            Workflows =
            [
                new Sdk.WorkflowDeclaration("empty", "no body", ""),
                new Sdk.WorkflowDeclaration("guide", "has body", "Do it."),
            ],
            Agents =
            [
                new Sdk.AgentDeclaration("", "nameless", "prompt", new Sdk.AgentModelSettings("m")),
                new Sdk.AgentDeclaration(
                    "helper", "helps", "Be helpful.", new Sdk.AgentModelSettings("m", Sdk.AgentThinkingEffort.Low, 64)),
            ],
            PromptContributors =
            [
                new Sdk.PromptContributorDeclaration(""),
                new Sdk.PromptContributorDeclaration("brief"),
            ],
        };
        var payload = JsonSerializer.SerializeToElement(
            new Sdk.PluginContributionsNotification("p9", set),
            Sdk.ExtensionExecutionJsonContext.Default.PluginContributionsNotification);
        await fake.Peer.SendNotificationAsync(Sdk.ExtensionProtocol.ContributionsRegister, payload, fake.Ct);

        await fake.WaitUntilAsync(() => fake.Client.Commands.Count == 1, "the single valid command");
        var command = Assert.Single(fake.Client.Commands);
        Assert.Equal("kept", command.Definition.Name);

        var workflow = Assert.Single(fake.Client.Workflows);
        Assert.Equal("guide", workflow.Name);

        var agent = Assert.Single(fake.Client.Agents);
        Assert.Equal("helper", agent.Name);

        Assert.Equal(new PluginPromptReference("p9", "brief"), Assert.Single(fake.Client.PromptContributors));
        Assert.NotEmpty(fake.Client.Diagnostics);
    }

    /// <summary>A minimal fake Extension Host: it answers the client's requests over a real named pipe and
    /// sends <c>host.tool_call</c> requests the way the real host would relay them. The client under test is
    /// real; only its peer is scripted.</summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(120));
        private readonly TaskCompletionSource executeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<ExecuteSeen> executes = new();
        private readonly ConcurrentQueue<string> runIds = new();
        private string executeResult = "ok";

        public bool HoldExecutes { get; set; }

        private FakeHost(
            NamedPipeServerTransport transport,
            IpcPeer peer,
            ExtensionHostClient client,
            string pipe)
        {
            Transport = transport;
            Peer = peer;
            Client = client;
            Pipe = pipe;
            Root = Path.GetTempPath();
        }

        public IpcPeer Peer { get; }

        public ExtensionHostClient Client { get; }

        public string Pipe { get; }

        public string Root { get; }

        public CancellationToken Ct => budget.Token;

        public ConcurrentQueue<string> ResolverCalls { get; } = new();

        public ConcurrentQueue<ApprovalRequest> ApprovalCalls { get; } = new();

        public IReadOnlyList<string> ExecuteRunIds => runIds.ToArray();

        private NamedPipeServerTransport Transport { get; }

        public static async Task<FakeHost> StartAsync()
        {
            var pipe = "forge-e2e-fake-" + Guid.NewGuid().ToString("N")[..20];
            var transport = new NamedPipeServerTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(10) });
            FakeHost? fake = null;
            var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(30) });
            peer.RequestReceived += (message, ct) => HandleAsync(fake!, message, ct);
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var accept = transport.StartAsync(startup.Token);
            var client = new ExtensionHostClient(
                new HostConnection { PipeName = pipe },
                options: new ExtensionHostClientOptions
                {
                    RequestTimeout = TimeSpan.FromSeconds(30),
                    StopRequestTimeout = TimeSpan.FromSeconds(5),
                    ProcessExitTimeout = TimeSpan.FromSeconds(5),
                },
                toolResolver: name =>
                {
                    fake!.ResolverCalls.Enqueue(name);
                    return name == "inner" ? new InnerTool() : null;
                },
                approvalPath: (request, _) =>
                {
                    fake!.ApprovalCalls.Enqueue(request);
                    return ValueTask.FromResult(ApprovalDecision.Allow);
                });
            fake = new FakeHost(transport, peer, client, pipe);
            await client.StartAsync(fake.Ct);
            await accept;
            await peer.StartAsync(fake.Ct);
            return fake;
        }

        private static async Task HandleAsync(FakeHost fake, IpcMessage message, CancellationToken ct)
        {
            switch (message.Operation)
            {
                case Sdk.ExtensionProtocol.PluginExecute:
                    var runId = ReadRunId(message.Arguments);
                    if (runId is not null)
                    {
                        fake.runIds.Enqueue(runId);
                    }

                    fake.executes.Enqueue(new ExecuteSeen(message.Arguments?.GetRawText() ?? "{}"));
                    if (fake.HoldExecutes)
                    {
                        await fake.executeGate.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
                    }
                    await fake.Peer.RespondAsync(
                        message.Id,
                        JsonSerializer.SerializeToElement(new { content = fake.executeResult, isError = false }),
                        CancellationToken.None);
                    break;
                case Sdk.ExtensionProtocol.HostStop:
                    await fake.Peer.RespondAsync(
                        message.Id, JsonDocument.Parse("{}").RootElement.Clone(), CancellationToken.None);
                    break;
                default:
                    await fake.Peer.RespondAsync(
                        message.Id, JsonDocument.Parse("{}").RootElement.Clone(), CancellationToken.None);
                    break;
            }
        }

        private static string? ReadRunId(JsonElement? arguments)
        {
            if (arguments is not { ValueKind: JsonValueKind.Object } root)
            {
                return null;
            }

            if (root.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object &&
                context.TryGetProperty("runId", out var runId) && runId.ValueKind == JsonValueKind.String)
            {
                return runId.GetString();
            }

            return null;
        }

        public async Task RegisterToolAsync(string pluginId, string tool)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                pluginId,
                tools = new[] { new { name = tool, description = "fake tool " + tool, inputSchemaJson = "{}", sideEffect = 0 } },
            });
            await Peer.SendNotificationAsync(Sdk.ExtensionProtocol.ToolsRegister, payload, Ct);
            await WaitUntilAsync(
                () => Client.Tools.Any(candidate =>
                    candidate.Definition.Name == tool &&
                    ((ExtensionHostTool)candidate).PluginId == pluginId),
                $"tool {pluginId}:{tool}");
        }

        public Task<ToolResult> InvokeAsync(string pluginId, string tool)
        {
            var proxy = Client.Tools.Single(candidate =>
                candidate.Definition.Name == tool && ((ExtensionHostTool)candidate).PluginId == pluginId);
            var call = new ToolCall(Guid.NewGuid().ToString("N"), tool, "{}");
            return proxy.InvokeAsync(call, new ToolContext("fake-session", Path.GetTempPath()), Ct).AsTask();
        }

        public async Task<string> WaitForExecuteAsync(string pluginId, string tool)
        {
            var limit = TestBudgets.Condition;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(limit);
            while (true)
            {
                if (runIds.TryPeek(out var runId))
                {
                    return runId;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"the host never relayed a '{tool}' call for '{pluginId}' within {limit.TotalSeconds:0} s.");
                }
            }
        }

        public void CompleteExecute(string content)
        {
            executeResult = content;
            executeGate.TrySetResult();
        }

        public async Task<ToolResult> CallHostToolAsync(object args)
        {
            var element = JsonSerializer.SerializeToElement(args);
            var response = await Peer.SendRequestAsync(Sdk.ExtensionProtocol.HostToolCall, element, Ct);
            var result = response.Result!.Value;
            return new ToolResult(
                result.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : result.GetRawText(),
                result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        }

        public async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var limit = TestBudgets.Condition;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(limit);
            while (!condition())
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
            }

            await Client.DisposeAsync();
            await Peer.DisposeAsync();
            await Transport.DisposeAsync();
            budget.Dispose();
        }

        private sealed record ExecuteSeen(string ArgumentsJson);

        private sealed class InnerTool : ITool
        {
            public ToolDefinition Definition { get; } = new("inner", "fake inner tool", """{"type":"object"}""");

            public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

            public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
                ValueTask.FromResult(new ToolResult("inner-ok"));
        }
    }
}
