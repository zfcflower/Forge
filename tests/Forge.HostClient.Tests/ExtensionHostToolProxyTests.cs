using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostToolProxyTests
{
    private static readonly string WorkingDirectory = BoundRoots.AbsoluteDir("work");
    private static readonly ToolContext Context = new("session-1", WorkingDirectory, 3);

    [Fact]
    public async Task Invoke_sends_plugin_execute_with_parsed_arguments_and_the_call_context()
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => Task.FromResult(Json.Element("""{"content":"hi","isError":false}""")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", """{"text":"hello","n":2}"""), Context, limit.Token);

        Assert.Equal(new ToolResult("hi", false), result);
        var request = Assert.Single(host.Requests("plugin.execute"));
        var args = request.Arguments!.Value;
        Assert.Equal("alpha", args.GetProperty("pluginId").GetString());
        Assert.Equal("echo", args.GetProperty("tool").GetString());
        Assert.Equal("session-1", args.GetProperty("sessionId").GetString());
        Assert.Equal(WorkingDirectory, args.GetProperty("workingDir").GetString());
        Assert.Equal(3, args.GetProperty("depth").GetInt32());
        var sent = args.GetProperty("args");
        Assert.Equal(JsonValueKind.Object, sent.ValueKind);
        Assert.Equal("hello", sent.GetProperty("text").GetString());
        Assert.Equal(2, sent.GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_arguments_are_sent_as_an_empty_object(string arguments)
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => Task.FromResult(Json.Element("""{"content":"ok","isError":false}""")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await tool.InvokeAsync(new ToolCall("c1", "echo", arguments), Context, limit.Token);

        var sent = Assert.Single(host.Requests("plugin.execute")).Arguments!.Value.GetProperty("args");
        Assert.Equal(JsonValueKind.Object, sent.ValueKind);
        Assert.Empty(sent.EnumerateObject());
    }

    [Fact]
    public async Task Malformed_arguments_are_rejected_locally_without_any_request()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{not json"), Context, limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("echo", result.Content);
        Assert.Contains("not valid JSON", result.Content);
        Assert.Empty(host.Requests("plugin.execute"));
    }

    [Fact]
    public async Task Error_flag_and_content_from_the_host_are_returned_unchanged()
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => Task.FromResult(Json.Element("""{"content":"bad input","isError":true}""")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.Equal(new ToolResult("bad input", true), result);
    }

    [Fact]
    public async Task A_remote_error_envelope_becomes_an_error_result_naming_plugin_and_tool()
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => throw new FakeHostError("denied", "the plugin lacks ReadFile"));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("'echo'", result.Content);
        Assert.Contains("plugin 'alpha'", result.Content);
        Assert.Contains("the plugin lacks ReadFile", result.Content);
    }

    [Fact]
    public async Task A_result_that_is_not_an_object_becomes_an_error_result()
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => Task.FromResult(Json.Element("\"just text\"")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.True(result.IsError);
        Assert.Equal("The Extension Host returned no tool result.", result.Content);
    }

    [Fact]
    public async Task A_malformed_result_object_becomes_an_error_result()
    {
        await using var host = new FakeHost();
        host.On("plugin.execute", (_, _) => Task.FromResult(Json.Element("""{"content":5,"isError":false}""")));
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_OperationCanceledException()
    {
        await using var host = new FakeHost();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.execute", async (_, ct) =>
        {
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json.Element("{}");
        });
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var cancel = new CancellationTokenSource();

        var invocation = tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, cancel.Token).AsTask();
        await received.Task.Bounded();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.Bounded());
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Host_that_never_answers_times_out_with_IpcTimeoutException_and_the_connection_stays_usable()
    {
        await using var host = new FakeHost();
        var attempts = 0;
        host.On("plugin.execute", (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return FakeHost.NeverAnswer(default!, ct);
            }

            return Task.FromResult(Json.Element("""{"content":"second","isError":false}"""));
        });
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(300) };
        await using var client = ClientFactory.Create(host, options: options);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<IpcTimeoutException>(() => tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token).AsTask());
        var second = await tool.InvokeAsync(new ToolCall("c2", "echo", "{}"), Context, limit.Token);

        Assert.Equal(new ToolResult("second", false), second);
        Assert.Equal(HostStatus.Running, client.Status);
    }

    [Fact]
    public async Task Sixteen_concurrent_calls_each_receive_their_own_result()
    {
        await using var host = new FakeHost();
        const int callers = 16;
        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.execute", async (message, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == callers)
            {
                gate.TrySetResult();
            }

            await gate.Task.WaitAsync(ct);
            var n = message.Arguments!.Value.GetProperty("args").GetProperty("n").GetInt32();
            return Json.Element($$"""{"content":"n={{n}}","isError":false}""");
        });
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var invocations = Enumerable.Range(0, callers)
            .Select(n => tool.InvokeAsync(new ToolCall($"c{n}", "echo", $$"""{"n":{{n}}}"""), Context, limit.Token).AsTask())
            .ToArray();
        var results = await Task.WhenAll(invocations).Bounded();

        for (var n = 0; n < callers; n++)
        {
            Assert.Equal(new ToolResult($"n={n}", false), results[n]);
        }

        Assert.Equal(callers, host.Requests("plugin.execute").Count);
    }

    [Fact]
    public async Task Invoking_a_proxy_after_the_client_stopped_fails_without_a_request()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();
        await client.StopAsync(limit.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token).AsTask());

        Assert.Empty(host.Requests("plugin.execute"));
        Assert.Empty(client.Tools);
    }

    [Fact]
    public async Task Invoke_rejects_null_arguments()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(null!, Context, limit.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), null!, limit.Token).AsTask());
        Assert.Empty(host.Requests("plugin.execute"));
    }
}
