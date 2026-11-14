using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>Relay coverage for run-bound <c>host.tool_call</c> callbacks. Every callback here is issued
/// from inside an in-flight <c>plugin.execute</c> root and carries that root's <c>runId</c>; unsolicited
/// traffic (the shape the old tests sent) is refused before it reaches the resolver. The recorded
/// originating context always wins: forged session/path/depth fields and the legacy options fallback
/// are ignored.</summary>
public sealed class HostToolCallRelayTests
{
    private const string Operation = "host.tool_call";

    [Fact]
    public async Task Unsolicited_call_without_a_runId_is_refused_before_resolution()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        // A typed sender cannot omit the run id: a nil in its place is undecodable and refused as such.
        var payload = RawForward.Of(
            RawForward.Field<string?>(null),
            RawForward.Field<string?>(null),
            RawForward.Field("echo"),
            RawForward.Field(Encoding.UTF8.GetBytes("{}")));
        var response = await host.CallClientAsync(Operation, payload, limit.Token);

        var content = BoundRoots.RefusedContent(response);
        Assert.Contains("cannot be decoded", content);
        Assert.Contains("is not a 'HostToolCallForward'", content);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Unsolicited_call_with_only_legacy_fields_is_refused_as_unbound()
    {
        var resolver = new ResolverSpy();
        var workingDirectory = BoundRoots.AbsoluteDir("host-dir");
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();

        // A blank run id and no plugin, but trailing worker-supplied directory/session/depth fields after the
        // documented four: the extras bind nothing.
        var payload = RawForward.Of(
            RawForward.Field(string.Empty),
            RawForward.Field(string.Empty),
            RawForward.Field("echo"),
            RawForward.Field(Encoding.UTF8.GetBytes("""{"a":1}""")),
            RawForward.Field(workingDirectory),
            RawForward.Field("s-9"),
            RawForward.Field(2));
        var response = await host.CallClientAsync(Operation, payload, limit.Token);

        var content = BoundRoots.RefusedContent(response);
        Assert.Contains("carries a blank run id", content);
        Assert.Contains("it was refused", content);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Without_a_resolver_the_bound_call_is_answered_as_an_unknown_tool()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var root = await BoundRoots.StartPluginToolRootAsync(harness.Host, harness.Client, harness.Limit.Token);

        var response = await root.CallAsync(harness.Host, "echo", "{}", harness.Limit.Token);

        Assert.Equal(("Unknown tool 'echo'.", true), BoundRoots.OutputOf(response));
    }

    [Fact]
    public async Task A_resolver_that_finds_no_tool_reports_unknown_and_never_reaches_approval()
    {
        var approvals = new ConcurrentQueue<ApprovalRequest>();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: _ => null,
            approval: (request, _) =>
            {
                approvals.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "missing", "{}", limit.Token);

        Assert.Equal(("Unknown tool 'missing'.", true), BoundRoots.OutputOf(response));
        Assert.Empty(approvals);
    }

    [Fact]
    public async Task A_resolver_without_an_approval_path_fails_closed_and_never_invokes_the_tool()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver());
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        var content = BoundRoots.RefusedContent(response);
        Assert.Contains("'echo' was not run", content);
        Assert.Contains("no approval path", content);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task A_denied_approval_is_an_error_and_the_tool_is_not_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: tool.Resolver(),
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Deny));
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(("The main program denied 'echo'.", true), BoundRoots.OutputOf(response));
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task An_approval_path_that_throws_is_an_error_and_the_tool_is_not_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: tool.Resolver(),
            approval: (_, _) => throw new InvalidOperationException("policy store offline"));
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(
            ("'echo' was not run: the approval path failed: policy store offline", true),
            BoundRoots.OutputOf(response));
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task Calls_are_resolved_then_approved_then_invoked_in_that_order()
    {
        var order = new ConcurrentQueue<string>();
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, _, _) =>
        {
            order.Enqueue("invoke");
            return ValueTask.FromResult(new ToolResult("done"));
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: name =>
            {
                order.Enqueue("resolve:" + name);
                return tool;
            },
            approval: (request, _) =>
            {
                order.Enqueue("approve:" + request.ToolName);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(("done", false), BoundRoots.OutputOf(response));
        Assert.Equal(new[] { "resolve:echo", "approve:echo", "invoke" }, order.ToArray());
    }

    [Fact]
    public async Task The_recorded_originating_context_wins_and_forged_host_fields_are_ignored()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write, (_, _, _) => ValueTask.FromResult(new ToolResult("42")));
        var rootDirectory = BoundRoots.AbsoluteDir("root-session");
        var rootContext = new ToolContext("sess-root", rootDirectory, 2);
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: approval.Approve);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, rootContext, limit.Token);

        var response = await root.CallAsync(host, "echo", """{"a":1}""", limit.Token, forgeLegacy: true);

        Assert.Equal(("42", false), BoundRoots.OutputOf(response));
        Assert.Equal(rootDirectory, approval.Last?.WorkingDirectory);
        Assert.DoesNotContain("FORGED", approval.Last?.WorkingDirectory ?? string.Empty, StringComparison.Ordinal);
        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal("echo", invocation.Call.Name);
        Assert.Equal("""{"a":1}""", invocation.Call.ArgumentsJson);
        Assert.StartsWith("hostcall-", invocation.Call.Id, StringComparison.Ordinal);
        Assert.Equal(new ToolContext("sess-root", rootDirectory, 3), invocation.Context);
    }

    [Fact]
    public async Task The_legacy_options_fallback_is_never_consulted()
    {
        var tool = new StubTool("echo");
        var rootDirectory = BoundRoots.AbsoluteDir("repo-sub");
        var rootContext = new ToolContext("sess-7", rootDirectory, 4);
        var approval = new ApprovalSpy();
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = BoundRoots.AbsoluteDir("configured") };
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: tool.Resolver(),
            approval: approval.Approve,
            options: options);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, rootContext, limit.Token);

        var response = await root.CallAsync(host, "echo", """{"a":1}""", limit.Token);

        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(response));
        Assert.Equal(rootDirectory, approval.Last?.WorkingDirectory);
        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal(new ToolContext("sess-7", rootDirectory, 5), invocation.Context);
    }

    [Fact]
    public async Task A_non_fully_qualified_options_value_is_harmless_because_it_is_never_read()
    {
        var tool = new StubTool("echo");
        var rootDirectory = BoundRoots.AbsoluteDir("repo-sub");
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = "relative/dir" };
        await using var client = await ClientFactory.StartAsync(
            host,
            resolver: tool.Resolver(),
            approval: Approvals.AllowAll,
            options: options);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, new ToolContext("sess-7", rootDirectory), limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(response));
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task A_bound_call_needs_no_legacy_fields_at_all()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await host.CallClientAsync(
            Operation, HostWire.Forward(root.RunId, "alpha", "echo", "{}"), limit.Token);
        await root.FinishAsync();

        Assert.Equal(("ran echo", false), BoundRoots.OutputOf(response));
    }

    [Fact]
    public async Task The_approval_request_carries_the_tool_side_effect_arguments_and_bound_working_directory()
    {
        var tool = new StubTool("writer", ToolSideEffect.Execute);
        var rootDirectory = BoundRoots.AbsoluteDir("proj");
        var approval = new ApprovalSpy(ApprovalDecision.Deny);
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: approval.Approve);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, new ToolContext("sess-9", rootDirectory), limit.Token);

        await root.CallAsync(host, "writer", """{"path":"a.txt"}""", limit.Token, forgeLegacy: true);

        Assert.Equal(
            new ApprovalRequest("writer", """{"path":"a.txt"}""", ToolSideEffect.Execute, rootDirectory), approval.Last);
    }

    [Fact]
    public async Task A_tool_result_error_flag_and_content_are_passed_back_unchanged()
    {
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, _, _) =>
            ValueTask.FromResult(new ToolResult("bad input", true)));
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(("bad input", true), BoundRoots.OutputOf(response));
    }

    [Fact]
    public async Task A_tool_that_throws_is_reported_as_an_error_with_its_exception_type()
    {
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, _, _) =>
            throw new InvalidOperationException("boom"));
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var response = await root.CallAsync(host, "echo", "{}", limit.Token);

        Assert.Equal(("InvalidOperationException: boom", true), BoundRoots.OutputOf(response));
    }

    [Fact]
    public async Task Bound_calls_without_a_usable_tool_name_are_rejected_before_resolution()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);
        var args = RawForward.Field(Encoding.UTF8.GetBytes("{}"));
        var cases = new (RawPayload Payload, string Expected)[]
        {
            // Name absent (the array stops after run id and plugin id) and nil are undecodable: the name is
            // declared non-nullable on the wire.
            (RawForward.Of(RawForward.Field(root.RunId), RawForward.Field("alpha")), "cannot be decoded"),
            (BoundRoots.BoundPayload(root.RunId, "alpha", "", "{}"), "no tool name"),
            (RawForward.Of(RawForward.Field(root.RunId), RawForward.Field("alpha"), RawForward.Field<string?>(null), args),
                "cannot be decoded"),
            (RawForward.Of(RawForward.Field(root.RunId), RawForward.Field("alpha"), RawForward.Field(5), args),
                "cannot be decoded"),
            (RawPayload.None, "no arguments"),
        };

        foreach (var (payload, expected) in cases)
        {
            var response = await host.CallClientAsync(Operation, payload, limit.Token);

            var content = BoundRoots.RefusedContent(response);
            Assert.Contains(expected, content);
        }

        await root.FinishAsync();
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Arguments_are_handed_to_the_tool_verbatim_without_normalization()
    {
        var seen = new ConcurrentQueue<string>();
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (call, _, _) =>
        {
            seen.Enqueue(call.ArgumentsJson);
            return ValueTask.FromResult(new ToolResult("ok"));
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        // The spacing of an object survives, and a JSON string literal stays a string: the typed wire carries the
        // argument bytes as-is, so nothing unwraps or re-serializes them.
        var arguments = new[] { """{"a": 1}""","\"{\\\"a\\\": 1}\"" };
        foreach (var json in arguments)
        {
            await host.CallClientAsync(Operation, HostWire.Forward(root.RunId, "alpha", "echo", json), limit.Token);
        }

        await root.FinishAsync();

        Assert.Equal(arguments, seen.ToArray());
    }

    [Fact]
    public async Task Missing_arguments_default_to_an_empty_object()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        // An absent JsonElement is the empty payload on the wire, as a worker relaying no arguments sends it.
        var payload = new HostToolCallForward(root.RunId, "alpha", "echo", HostToolCalls.ArgumentsUtf8(default(JsonElement)));
        await host.CallClientAsync(Operation, payload, limit.Token);
        await root.FinishAsync();

        Assert.Equal("{}", Assert.Single(tool.Invocations).Call.ArgumentsJson);
    }

    [Fact]
    public async Task Non_object_arguments_are_preserved_verbatim()
    {
        var seen = new ConcurrentQueue<string>();
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (call, _, _) =>
        {
            seen.Enqueue(call.ArgumentsJson);
            return ValueTask.FromResult(new ToolResult("ok"));
        });
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        foreach (var args in new[] { "[1,2]", "7", "true" })
        {
            await host.CallClientAsync(
                Operation, BoundRoots.BoundPayload(root.RunId, "alpha", "echo", args), limit.Token);
        }

        await root.FinishAsync();

        Assert.Equal(new[] { "[1,2]", "7", "true" }, seen.ToArray());
    }

    [Fact]
    public async Task Whitespace_only_arguments_fall_back_to_an_empty_object()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: tool.Resolver(), approval: Approvals.AllowAll);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        await host.CallClientAsync(Operation, HostWire.Forward(root.RunId, "alpha", "echo", " \r\n\t "), limit.Token);
        await root.FinishAsync();

        Assert.Equal("{}", Assert.Single(tool.Invocations).Call.ArgumentsJson);
    }

    [Fact]
    public async Task Malformed_bound_payloads_are_rejected_before_resolution()
    {
        var resolver = new ResolverSpy();
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host, resolver: resolver.Resolve);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var args = RawForward.Field(Encoding.UTF8.GetBytes("{}"));
        var echo = RawForward.Field("echo");
        var alpha = RawForward.Field("alpha");
        var run = RawForward.Field(root.RunId);
        var malformed = new RawPayload[]
        {
            RawForward.Of(RawForward.Field(1), RawForward.Field(2)),
            HostWire.Payload("text"),
            HostWire.Nil,
            RawForward.Of(),
            RawForward.Of(RawForward.Field<string?>(null), alpha, echo, args),
            BoundRoots.BoundPayload("   ", "alpha", "echo", "{}"),
            RawForward.Of(RawForward.Field(42), alpha, echo, args),
            RawForward.Of(RawForward.Field(true), alpha, echo, args),
            RawForward.Of(run),
            BoundRoots.BoundPayload(root.RunId, "", "echo", "{}"),
            RawForward.Of(run, RawForward.Field(7), echo, args),
        };
        foreach (var payload in malformed)
        {
            var response = await host.CallClientAsync(Operation, payload, limit.Token);
            Assert.True(BoundRoots.OutputOf(response).IsError);
        }

        await root.FinishAsync();
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Pending_approval_is_canceled_when_the_host_connection_drops_and_nothing_is_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new EventLog<(HostStatus Status, string? Detail)>();
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: tool.Resolver(),
            approval: async (_, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }

                return ApprovalDecision.Allow;
            });
        client.StatusChanged += (status, detail) => failures.Add((status, detail));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var root = await BoundRoots.StartPluginToolRootAsync(host, client, limit.Token);

        var pending = host.CallClientAsync(
            Operation, BoundRoots.BoundPayload(root.RunId, "alpha", "echo", "{}"), limit.Token);
        await entered.Task.Bounded();
        await host.DropConnectionAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.True(
            error is IOException || error is IpcConnectionClosedException,
            "Dropping the connection should fault the pending callback, but got " + error.GetType().Name);
        await canceled.Task.Bounded();
        Assert.Equal(0, tool.InvocationCount);
        var statuses = await failures.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Equal(HostStatus.Failed, client.Status);
        Assert.Contains(statuses, e => e.Status == HostStatus.Failed && e.Detail is not null);
    }
}

/// <summary>Hand-built <c>host.tool_call</c> arrays for the shapes a typed sender cannot produce: missing trailing
/// elements, wrongly typed elements and extra trailing elements.</summary>
internal static class RawForward
{
    public static byte[] Field<T>(T value) => IpcSerializer.Serialize(value);

    /// <summary>A MessagePack fixarray of the already-encoded <paramref name="fields"/>.</summary>
    public static RawPayload Of(params byte[][] fields)
    {
        Assert.InRange(fields.Length, 0, 15);
        var payload = new List<byte> { (byte)(0x90 | fields.Length) };
        foreach (var field in fields)
        {
            payload.AddRange(field);
        }

        return new RawPayload(payload.ToArray());
    }
}
