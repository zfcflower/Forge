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
    public async Task A_fully_qualified_working_directory_from_the_host_is_approved_and_used_verbatim()
    {
        var tool = new StubTool("echo", ToolSideEffect.Write, (_, _, _) => ValueTask.FromResult(new ToolResult("42")));
        var workingDirectory = AbsoluteDirectory("host-dir");
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) => ValueTask.FromResult(ApprovalDecision.Allow));
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            $$"""{"name":"echo","args":{"a":1},"workingDir":"{{workingDirectory}}","sessionId":"s-9","depth":2}""",
            limit.Token);

        Assert.Equal(new ToolOutput("42", false), Output(response));
        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal("echo", invocation.Call.Name);
        Assert.Equal("""{"a":1}""", invocation.Call.ArgumentsJson);
        Assert.StartsWith("hostcall-", invocation.Call.Id, StringComparison.Ordinal);
        Assert.Equal(new ToolContext("s-9", workingDirectory, 2), invocation.Context);
    }

    [Fact]
    public async Task The_carried_working_directory_session_and_depth_win_over_the_options()
    {
        var tool = new StubTool("echo");
        var workingDirectory = AbsoluteDirectory("repo/sub");
        ApprovalRequest? approved = null;
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = AbsoluteDirectory("configured") };
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (request, _) =>
            {
                approved = request;
                return ValueTask.FromResult(ApprovalDecision.Allow);
            },
            options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            $$"""{"name":"echo","args":{"a":1},"sessionId":"sess-7","workingDir":"{{workingDirectory}}","depth":4}""",
            limit.Token);

        Assert.Equal(new ToolOutput("ran echo", false), Output(response));
        Assert.Equal(workingDirectory, approved?.WorkingDirectory);
        var invocation = Assert.Single(tool.Invocations);
        Assert.Equal(new ToolContext("sess-7", workingDirectory, 4), invocation.Context);
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("sub")]
    [InlineData("C:relative")]
    [InlineData("\\relative")]
    public async Task A_working_directory_that_is_not_fully_qualified_fails_closed_before_approval_and_invocation(
        string workingDirectory)
    {
        var tool = new StubTool("echo");
        var approvals = 0;
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            });
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            $$"""{"name":"echo","args":{},"sessionId":"sess-7","workingDir":"{{workingDirectory}}","depth":1}""",
            limit.Token);

        var output = Output(response);
        Assert.True(output.IsError);
        Assert.Contains("not a fully qualified path", output.Content);
        Assert.Equal(0, approvals);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public async Task A_present_but_blank_working_directory_fails_closed_instead_of_falling_back(string workingDirJson)
    {
        var tool = new StubTool("echo");
        var approvals = 0;
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = AbsoluteDirectory("configured") };
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            },
            options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(
            Operation,
            "{\"name\":\"echo\",\"args\":{},\"workingDir\":" + workingDirJson + "}",
            limit.Token);

        var output = Output(response);
        Assert.True(output.IsError);
        Assert.Contains("working directory carried by the host is blank", output.Content);
        Assert.Equal(0, approvals);
        Assert.Equal(0, tool.InvocationCount);
    }

    [Fact]
    public async Task The_approval_request_carries_the_tool_side_effect_arguments_and_working_directory()
    {
        var tool = new StubTool("writer", ToolSideEffect.Execute);
        var workingDirectory = AbsoluteDirectory("proj");
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

        await host.CallClientAsync(
            Operation,
            $$"""{"name":"writer","args":{"path":"a.txt"},"workingDir":"{{workingDirectory}}"}""",
            limit.Token);

        Assert.Equal(new ApprovalRequest("writer", """{"path":"a.txt"}""", ToolSideEffect.Execute, workingDirectory), seen);
    }

    [Fact]
    public async Task An_absent_working_directory_falls_back_to_the_option()
    {
        var requests = new ConcurrentQueue<ApprovalRequest>();
        var tool = new StubTool("echo");
        var configured = AbsoluteDirectory("configured");
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = configured };
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

        Assert.Equal(new[] { configured }, requests.Select(request => request.WorkingDirectory).ToArray());
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("C:relative")]
    [InlineData("\\relative")]
    public async Task An_absent_working_directory_with_a_non_fully_qualified_option_fails_closed(
        string configured)
    {
        var tool = new StubTool("echo");
        var approvals = 0;
        await using var host = new FakeHost();
        var options = new ExtensionHostClientOptions { HostToolWorkingDirectory = configured };
        await using var client = ClientFactory.Create(
            host,
            resolver: name => name == "echo" ? tool : null,
            approval: (_, _) =>
            {
                Interlocked.Increment(ref approvals);
                return ValueTask.FromResult(ApprovalDecision.Allow);
            },
            options: options);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();

        var response = await host.CallClientAsync(Operation, ToolCallPayload("echo"), limit.Token);

        var output = Output(response);
        Assert.True(output.IsError);
        Assert.Contains("the fallback working directory", output.Content);
        Assert.Contains("is not a fully qualified path", output.Content);
        Assert.Equal(0, approvals);
        Assert.Equal(0, tool.InvocationCount);
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

    private static string AbsoluteDirectory(string name) =>
        Path.Combine(Path.GetTempPath(), "forge-hc", name).Replace('\\', '/');

    /// <summary>A call that carries no <c>workingDir</c> at all, as an old host sends it.</summary>
    private static string ToolCallPayload(string name) =>
        "{\"name\":\"" + name + "\",\"args\":{}}";

    private static ToolOutput Output(IpcMessage response)
    {
        var result = response.Result ?? throw new InvalidOperationException("The relay answered with an error envelope.");
        return new ToolOutput(result.GetProperty("content").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private sealed record ToolOutput(string Content, bool IsError);
}
