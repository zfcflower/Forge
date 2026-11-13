using System.Text.Json;
using Forge.Abstractions;
using Forge.Ipc;
using Forge.Ipc.Protocol;
using Xunit;
using Sdk = Forge.Plugin.Sdk;
using static Forge.HostClient.Tests.ToolFixtures;

namespace Forge.HostClient.Tests;

public sealed class ExtensionHostToolProxyTests
{
    private static readonly string WorkingDirectory = BoundRoots.AbsoluteDir("work");
    private static readonly ToolContext Context = new("session-1", WorkingDirectory, 3);
    private const string TestToolArgumentsText = """{"text":"hello","n":2}""";

    [Fact]
    public async Task Invoke_sends_plugin_execute_with_parsed_arguments_and_the_call_context()
    {
        await using var host = new FakeHost();
        host.Answer("plugin.execute", new Sdk.ToolResult("hi", false));
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", TestToolArgumentsText), Context, limit.Token);

        Assert.Equal(new ToolResult("hi", false), result);
        var request = Assert.Single(host.Requests("plugin.execute"));
        var args = request.Read<PluginExecuteRequest>();
        Assert.Equal("alpha", args.PluginId);
        Assert.Equal("echo", args.Tool);
        var context = Assert.IsType<Sdk.ExtensionCallContext>(args.Context);
        Assert.Equal("session-1", context.SessionId);
        Assert.Equal(WorkingDirectory, context.WorkingDirectory);
        Assert.Equal(3, context.Depth);
        Assert.Equal(TestToolArgumentsText, HostWire.Text(args.ArgumentsJson));
        using var document = JsonDocument.Parse(args.ArgumentsJson);
        var sent = document.RootElement;
        Assert.Equal(JsonValueKind.Object, sent.ValueKind);
        Assert.Equal("hello", sent.GetProperty("text").GetString());
        Assert.Equal(2, sent.GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_arguments_are_sent_as_the_empty_payload(string arguments)
    {
        await using var host = new FakeHost();
        host.Answer("plugin.execute", new Sdk.ToolResult("ok", false));
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await tool.InvokeAsync(new ToolCall("c1", "echo", arguments), Context, limit.Token);

        // The empty payload is the wire form of "{}"; the host hands an empty object to the tool.
        var sent = Assert.Single(host.Requests("plugin.execute")).Read<PluginExecuteRequest>();
        Assert.True(sent.ArgumentsJson.IsEmpty);
    }

    [Fact]
    public async Task Malformed_arguments_are_rejected_locally_without_any_request()
    {
        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);
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
        host.Answer("plugin.execute", new Sdk.ToolResult("bad input", true));
        await using var client = await ClientFactory.StartAsync(host);
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
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.True(result.IsError);
        Assert.Contains("'echo'", result.Content);
        Assert.Contains("plugin 'alpha'", result.Content);
        Assert.Contains("the plugin lacks ReadFile", result.Content);
    }

    [Fact]
    public async Task A_response_without_a_payload_becomes_an_error_result()
    {
        await using var host = new FakeHost();
        host.AnswerRaw("plugin.execute", RawPayload.None);
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.True(result.IsError);
        Assert.Equal("The Extension Host returned no tool result.", result.Content);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("numeric content")]
    [InlineData("nil")]
    [InlineData("nil content")]
    public async Task A_result_that_cannot_be_decoded_becomes_an_error_result(string shape)
    {
        await using var host = new FakeHost();
        host.AnswerRaw("plugin.execute", shape switch
        {
            "text" => HostWire.Payload("just text"),
            // [5, false]: the content is a number instead of a string.
            "numeric content" => new RawPayload(new byte[] { 0x92, 0x05, 0xc2 }),
            // [nil, true]: the content member is non-nullable, so a nil content fails to decode.
            "nil content" => new RawPayload(new byte[] { 0x92, 0xc0, 0xc3 }),
            _ => HostWire.Nil,
        });
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        var result = await tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token);

        Assert.Equal(new ToolResult("The Extension Host returned a tool result that cannot be decoded.", true), result);
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
            return new Sdk.ToolResult("never", false);
        });
        await using var client = await ClientFactory.StartAsync(host);
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

            return Task.FromResult(HostReply.Of(new Sdk.ToolResult("second", false)));
        });
        var options = new ExtensionHostClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(300) };
        await using var client = await ClientFactory.StartAsync(host, options: options);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<IpcTimeoutException>(() => tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), Context, limit.Token).AsTask());
        var second = await AnswerAfterTimeoutAsync(tool, limit.Token);

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
            using var document = JsonDocument.Parse(message.Read<PluginExecuteRequest>().ArgumentsJson);
            var n = document.RootElement.GetProperty("n").GetInt32();
            return new Sdk.ToolResult($"n={n}", false);
        });
        await using var client = await ClientFactory.StartAsync(host);
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
        await using var client = await ClientFactory.StartAsync(host);
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
        await using var client = await ClientFactory.StartAsync(host);
        var tool = await RegisterEchoAsync(client, host);
        using var limit = TestLimit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(null!, Context, limit.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => tool.InvokeAsync(new ToolCall("c1", "echo", "{}"), null!, limit.Token).AsTask());
        Assert.Empty(host.Requests("plugin.execute"));
    }

    /// <summary>
    /// Calls <paramref name="tool"/> until the host answers. The 300 ms budget this class configures is the budget
    /// under test, and it caps every follow-up call as well, so one expired budget on a loaded runner says nothing
    /// about the connection: the round trip is what is awaited, with the test limit as the upper bound. Only
    /// <see cref="IpcTimeoutException"/> is retried, so a connection the timeout really broke fails the call at once
    /// (a closed peer is not running any more, and an unanswered request on a closed pipe raises its own exception).
    /// </summary>
    private static async Task<ToolResult> AnswerAfterTimeoutAsync(ITool tool, CancellationToken limit)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await tool.InvokeAsync(
                        new ToolCall($"after-timeout-{attempt}", "echo", "{}"), Context, limit);
                }
                catch (IpcTimeoutException) when (!limit.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException exception) when (limit.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The Extension Host answered no call within {TestLimit.Span.TotalSeconds:0} s after the timed-out one.",
                exception);
        }
    }
}
