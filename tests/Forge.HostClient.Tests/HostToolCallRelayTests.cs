using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests;

public sealed class HostToolCallRelayTests
{
    private const string Operation = "host.tool_call";

    [Fact]
    public async Task Without_a_resolver_the_call_is_answered_as_an_unknown_tool()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(new ToolOutput("Unknown tool 'echo'.", true), Output(response));
    }

    [Fact]
    public async Task A_resolver_that_finds_no_tool_reports_unknown_and_never_reaches_approval()
    {
        var approvals = new ConcurrentQueue<ApprovalRequest>();
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: _ => null,
            approval: (request, _) =>
            {
                approvals.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("missing"), limit.Token);

        Assert.Equal(new ToolOutput("Unknown tool 'missing'.", true), Output(response));
        Assert.Empty(approvals);
    }

    [Fact]
    public async Task A_resolver_without_an_approval_path_fails_closed_and_never_invokes_the_tool()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host, resolver: name => name == "echo" ? tool : null);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        var output = Output(response);
        Assert.True(output.IsError);
        Assert.Contains("'echo' was not run", output.Content);
        Assert.Contains("no approval path", output.Content);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task A_denied_approval_is_an_error_and_the_tool_is_not_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Deny));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(new ToolOutput("The main program denied 'echo'.", true), Output(response));
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task An_approval_path_that_throws_is_an_error_and_the_tool_is_not_invoked()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write);
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => throw new InvalidOperationException("policy store offline"));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(
            new ToolOutput("'echo' was not run: the approval path failed: policy store offline", true),
            Output(response));
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
        await using var client = ClientFactory.Create(
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
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(new ToolOutput("done", false), Output(response));
        Assert.Equal(new[] { "resolve:echo", "approve:echo", "invoke" }, order.ToArray());
    }

    [Fact]
    public async Task An_approved_call_runs_the_tool_with_the_arguments_and_context_from_the_host()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write, (_, _, _) => ValueTask.FromResult(new ToolResult("42")));
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            """{"name":"echo","args":{"a":1},"workingDir":"C:/host-dir","sessionId":"s-9","depth":2}""",
            limit.Token);

        Assert.Equal(new ToolOutput("42", false), Output(response));
        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal("echo", invocation.Call.Name);
        Assert.Equal("""{"a":1}""", invocation.Call.ArgumentsJson);
        Assert.StartsWith("hostcall-", invocation.Call.Id, StringComparison.Ordinal);
        Assert.Equal(new ToolContext("s-9", "C:/host-dir", 2), invocation.Context);
    }

    [Fact]
    public async Task The_approval_request_carries_the_tool_side_effect_arguments_and_working_directory()
    {
        var tool = new StubTool("writer", ToolSideEffect.Execute);
        ApprovalRequest? seen = null;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "writer" ? tool : null,
            approval: (request, _) =>
            {
                seen = request;
                return ValueTask.FromResult(ApprovalDecision.Deny);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await host.CallClientAsync(Operation, """{"name":"writer","args":{"path":"a.txt"},"workingDir":"C:/proj"}""", limit.Token);

        Assert.Equal(new ApprovalRequest("writer", """{"path":"a.txt"}""", ToolSideEffect.Execute, "C:/proj"), seen);
    }

    [Fact]
    public async Task Working_directory_falls_back_to_the_option_and_then_to_the_process_directory()
    {
        var requests = new ConcurrentQueue<ApprovalRequest>();
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = "C:/configured" };
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (request, _) =>
            {
                requests.Enqueue(request);
                return ValueTask.FromResult(ApprovalDecision.Deny);
            },
            options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);
        await host.CallClientAsync(Operation, ToolCallPayload("echo", workingDir: "  "), limit.Token);

        var directories = requests.Select(request => request.WorkingDirectory).ToArray();
        Assert.Equal(new[] { "C:/configured", "C:/configured" }, directories);
    }

    [Fact]
    public async Task Without_a_working_directory_option_the_process_directory_is_used()
    {
        ApprovalRequest? seen = null;
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (request, _) =>
            {
                seen = request;
                return ValueTask.FromResult(ApprovalDecision.Deny);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(Environment.CurrentDirectory, seen?.WorkingDirectory);
    }

    [Fact]
    public async Task A_tool_result_error_flag_and_content_are_passed_back_unchanged()
    {
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, _, _) =>
            ValueTask.FromResult(new ToolResult("bad input", true)));
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(new ToolOutput("bad input", true), Output(response));
    }

    [Fact]
    public async Task A_tool_that_throws_is_reported_as_an_error_with_its_exception_type()
    {
        var tool = new StubTool("echo", ToolSideEffect.ReadOnly, (_, _, _) =>
            throw new InvalidOperationException("boom"));
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        Assert.Equal(new ToolOutput("InvalidOperationException: boom", true), Output(response));
    }

    [Theory]
    [InlineData("""{"args":{}}""")]
    [InlineData("""{"name":"","args":{}}""")]
    [InlineData("""{"name":null,"args":{}}""")]
    [InlineData("""{"name":5}""")]
    [InlineData("""[1]""")]
    public async Task A_call_without_a_tool_name_is_rejected_before_resolution(string payload)
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

        var response = await host.CallClientAsync(Operation, payload, limit.Token);

        Assert.Equal(new ToolOutput("The host.tool_call request has no tool name.", true), Output(response));
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task Arguments_sent_as_a_JSON_string_are_normalized_to_the_object()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await host.CallClientAsync(Operation, """{"name":"echo","args":"{\"a\": 1}"}""", limit.Token);

        Assert.Equal("""{"a": 1}""", Assert.Single(tool.Invocations).Call.ArgumentsJson);
    }

    [Fact]
    public async Task Missing_arguments_default_to_an_empty_object()
    {
        var tool = new StubTool("echo");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        await host.CallClientAsync(Operation, """{"name":"echo"}""", limit.Token);

        Assert.Equal("{}", Assert.Single(tool.Invocations).Call.ArgumentsJson);
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
            resolver: name => name == "echo" ? tool : null,
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

        var pending = host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);
        await entered.Task.Bounded();
        await host.DropConnectionAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => pending);
        await canceled.Task.Bounded();
        Assert.Equal(0, tool.InvocationCount);
        var statuses = await failures.UntilAsync(seen => seen.Any(e => e.Status == HostStatus.Failed));
        Assert.Equal(HostStatus.Failed, client.Status);
        Assert.Contains(statuses, e => e.Status == HostStatus.Failed && e.Detail is not null);
    }

    private static string ToolCallPayload(string name, string? workingDir = null)
    {
        var location = workingDir is null ? string.Empty : ",\"workingDir\":\"" + workingDir + "\"";
        return "{\"name\":\"" + name + "\",\"args\":{}" + location + "}";
    }

    private static ToolOutput Output(IpcMessage response)
    {
        var result = response.Result ?? throw new InvalidOperationException("The relay answered with an error envelope.");
        return new ToolOutput(result.GetProperty("content").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private sealed record ToolOutput(string Content, bool IsError);
}
