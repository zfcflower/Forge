using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>Run binding: a <c>host.tool_call</c> is accepted only when it names a live run of this
/// connection generation with the recorded plugin identity. Identity rejection happens before the
/// resolver and the approval path are reached.</summary>
public sealed class HostToolCallRunBindingTests
{
    private const string Operation = "host.tool_call";

    [Fact]
    public async Task Missing_runId_is_refused_and_neither_resolver_nor_approval_runs()
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, """{"pluginId":"alpha","name":"echo","args":{}}""", limit.Token);

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("not bound to a run id", content);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Blank_or_non_string_runId_is_refused_before_identity_checks(string runIdJson)
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            "{\"runId\":" + runIdJson + ",\"pluginId\":\"alpha\",\"name\":\"echo\",\"args\":{}}",
            limit.Token);

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("run id", content);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
    }

    [Fact]
    public async Task Whitespace_only_runId_matches_no_live_run()
    {
        var resolved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, resolver: _ =>
        {
            Interlocked.Increment(ref resolved);
            return null;
        });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            "{\"runId\":\"   \",\"pluginId\":\"alpha\",\"name\":\"echo\",\"args\":{}}",
            limit.Token);

        var (whitespaceContent, whitespaceIsError) = BoundRoots.OutputOf(response);
        Assert.True(whitespaceIsError);
        Assert.Contains("not part of an execution", whitespaceContent);
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task Unknown_runId_is_refused_and_neither_resolver_nor_approval_runs()
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            "{\"runId\":\"" + new string('a', 32) + "\",\"pluginId\":\"alpha\",\"name\":\"echo\",\"args\":{}}",
            limit.Token);

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("not part of an execution", content);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        BoundRoots.AssertDiagnostic(client, "refused");
    }

    [Fact]
    public async Task Forged_pluginId_is_refused_and_neither_resolver_nor_approval_runs()
    {
        var resolved = 0;
        var approved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: _ =>
            {
                Interlocked.Increment(ref resolved);
                return null;
            },
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approved);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, limit.Token);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(root.RunId, "beta", "echo", "{}"), limit.Token);
        root.Hold.Gate.TrySetResult();
        await root.RootTask.Bounded();

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("claims plugin 'beta'", content);
        Assert.Contains("belongs to 'alpha'", content);
        Assert.Equal(0, resolved);
        Assert.Equal(0, approved);
        BoundRoots.AssertDiagnostic(client, "refused");
    }

    [Fact]
    public async Task Missing_or_blank_pluginId_is_refused_before_resolution()
    {
        var resolved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, resolver: _ =>
        {
            Interlocked.Increment(ref resolved);
            return null;
        });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, limit.Token);

        foreach (var pluginJson in new[] { "null", "\"\"", "\"   \"" })
        {
            var response = await host.CallClientAsync(
                Operation,
                "{\"runId\":\"" + root.RunId + "\",\"pluginId\":" + pluginJson + ",\"name\":\"echo\",\"args\":{}}",
                limit.Token);
            Assert.True(BoundRoots.OutputOf(response).IsError);
        }

        var absent = await host.CallClientAsync(
            Operation,
            "{\"runId\":\"" + root.RunId + "\",\"name\":\"echo\",\"args\":{}}",
            limit.Token);
        Assert.True(BoundRoots.OutputOf(absent).IsError);

        root.Hold.Gate.TrySetResult();
        await root.RootTask.Bounded();
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task Replay_after_completion_is_refused_and_the_resolver_never_runs_again()
    {
        var resolved = 0;
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name =>
            {
                Interlocked.Increment(ref resolved);
                return name == "echo" ? tool : null;
            },
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, limit.Token);

        var first = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(root.RunId, "alpha", "echo", "{}"), limit.Token);
        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(first));
        root.Hold.Gate.TrySetResult();
        await root.RootTask.Bounded();
        Assert.Equal(1, resolved);

        var replay = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(root.RunId, "alpha", "echo", "{}"), limit.Token);

        var (content, isError) = BoundRoots.OutputOf(replay);
        Assert.True(isError);
        Assert.Contains("not part of an execution", content);
        Assert.Equal(1, resolved);
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task Callback_from_an_earlier_connection_after_reconnect_is_refused()
    {
        var resolved = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, resolver: _ =>
        {
            Interlocked.Increment(ref resolved);
            return null;
        });
        var statuses = new EventLog<(HostStatus Status, string? Detail)>();
        client.StatusChanged += (status, detail) => statuses.Add((status, detail));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, limit.Token);
        var staleRunId = root.RunId;

        await host.DropConnectionAsync();
        await statuses.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Equal(0, resolved);

        var accepting = host.AcceptAsync();
        await client.StartAsync(limit.Token).Bounded();
        await accepting.Bounded();
        Assert.Equal(HostStatus.Running, client.Status);

        var response = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(staleRunId, "alpha", "echo", "{}"), limit.Token);

        var (content, isError) = BoundRoots.OutputOf(response);
        Assert.True(isError);
        Assert.Contains("not part of an execution", content);
        Assert.Equal(0, resolved);
        root.Hold.Gate.TrySetResult();
    }

    [Fact]
    public async Task Parent_completion_while_approval_ignores_cancellation_then_allows_does_not_invoke_the_target()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        var enteredApproval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApproval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: async (_, _) =>
            {
                enteredApproval.TrySetResult();
                await releaseApproval.Task.ConfigureAwait(false);
                return ApprovalDecision.Allow;
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, limit.Token);

        var pending = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(root.RunId, "alpha", "echo", "{}"), limit.Token);
        await enteredApproval.Task.Bounded();
        root.Hold.Gate.TrySetResult();
        await root.RootTask.Bounded();
        releaseApproval.TrySetResult();

        var (content, isError) = BoundRoots.OutputOf(await pending.Bounded());
        Assert.True(isError);
        Assert.Contains("already ended", content);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task Sibling_runs_keep_their_original_contexts_isolated()
    {
        var seen = new ConcurrentDictionary<string, ToolContext>(StringComparer.Ordinal);
        var tool = new StubTool("t", ToolSideEffect.ReadOnly, (call, context, _) =>
        {
            seen[call.ArgumentsJson] = context;
            return ValueTask.FromResult(new ToolResult("ok"));
        });
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "t" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var holdA = new InFlightRoot();
        var holdB = new InFlightRoot();
        host.On("plugin.execute", async (message, ct) =>
        {
            var plugin = message.Arguments!.Value.GetProperty("pluginId").GetString();
            var hold = string.Equals(plugin, "alpha", StringComparison.Ordinal) ? holdA : holdB;
            hold.Arrived.TrySetResult(message);
            await hold.Gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Json.Element("""{"content":"root-ok","isError":false}""");
        });
        var proxyA = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entryA");
        var proxyB = await BoundRoots.RegisterPluginToolAsync(client, host, "beta", "entryB");
        var dirA = BoundRoots.AbsoluteDir("sibling-a");
        var dirB = BoundRoots.AbsoluteDir("sibling-b");
        var taskA = proxyA.InvokeAsync(
            new ToolCall("r1", "entryA", "{}"), new ToolContext("sess-a", dirA, 1), limit.Token).AsTask();
        var taskB = proxyB.InvokeAsync(
            new ToolCall("r2", "entryB", "{}"), new ToolContext("sess-b", dirB, 5), limit.Token).AsTask();
        var runA = BoundRoots.RunIdOf(await holdA.Arrived.Task.Bounded());
        var runB = BoundRoots.RunIdOf(await holdB.Arrived.Task.Bounded());
        Assert.NotEqual(runA, runB);

        var cross = await host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(runA, "beta", "t", "{}"), limit.Token);
        Assert.True(BoundRoots.OutputOf(cross).IsError);

        await host.CallClientAsync(Operation, BoundRoots.BoundPayload(runA, "alpha", "t", "{\"tag\":\"a\"}"), limit.Token);
        await host.CallClientAsync(Operation, BoundRoots.BoundPayload(runB, "beta", "t", "{\"tag\":\"b\"}"), limit.Token);
        holdA.Gate.TrySetResult();
        holdB.Gate.TrySetResult();
        await taskA.Bounded();
        await taskB.Bounded();

        Assert.Equal(new ToolContext("sess-a", dirA, 2), seen["{\"tag\":\"a\"}"]);
        Assert.Equal(new ToolContext("sess-b", dirB, 6), seen["{\"tag\":\"b\"}"]);
        Assert.Equal(2, tool.InvocationCount);
    }

    [Fact]
    public async Task Forged_context_object_in_the_callback_payload_is_ignored()
    {
        var tool = new StubTool("echo");
        var rootDirectory = BoundRoots.AbsoluteDir("root-ctx");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await StartAlphaRootAsync(host, client, new ToolContext("sess-r", rootDirectory, 2), limit.Token);

        var response = await host.CallClientAsync(
            Operation,
            BoundRoots.BoundPayload(root.RunId, "alpha", "echo", "{}", forgeLegacy: true),
            limit.Token);
        root.Hold.Gate.TrySetResult();
        await root.RootTask.Bounded();

        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(response));
        Assert.Equal(new ToolContext("sess-r", rootDirectory, 3), Assert.Single(tool.Invocations).Context);
    }

    [Fact]
    public async Task Plugin_tool_root_with_a_relative_working_directory_is_rejected_locally()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");

        var result = await proxy.InvokeAsync(
            new ToolCall("root-1", "entry", "{}"),
            new ToolContext("sess", "relative/dir"),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("fully qualified", result.Content);
        Assert.Empty(host.Requests("plugin.execute"));
    }

    [Fact]
    public async Task Run_code_root_with_a_relative_working_directory_is_rejected_locally()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var proxy = client.CreateRunCodeTool(new TestRegistry());

        var result = await proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", "relative/dir"),
            limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("fully qualified", result.Content);
        Assert.Empty(host.Requests("code.execute"));
    }

    private sealed record ActiveRoot(string RunId, InFlightRoot Hold, Task<ToolResult> RootTask);

    private static async Task<ActiveRoot> StartAlphaRootAsync(
        FakeHost host, ExtensionHostClient client, CancellationToken ct) =>
        await StartAlphaRootAsync(
            host, client, new ToolContext("sess-root", BoundRoots.AbsoluteDir("root")), ct);

    private static async Task<ActiveRoot> StartAlphaRootAsync(
        FakeHost host, ExtensionHostClient client, ToolContext context, CancellationToken ct)
    {
        var hold = new InFlightRoot();
        BoundRoots.ParkRoot(host, "plugin.execute", hold);
        var proxy = await BoundRoots.RegisterPluginToolAsync(client, host, "alpha", "entry");
        var rootTask = proxy.InvokeAsync(new ToolCall("root-1", "entry", "{}"), context, ct).AsTask();
        var runId = BoundRoots.RunIdOf(await hold.Arrived.Task.Bounded());
        return new ActiveRoot(runId, hold, rootTask);
    }
}
