using Forge.Ipc;
using Forge.Ipc.Protocol;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>The Worker's and the script executor's only callback exit: <see cref="HostToolCalls.CallAsync"/> turns
/// everything that is not the caller's own cancellation into a tool result the plugin can read, labelling a remote
/// error whose code says <c>denied</c> only when the caller asks for it (<c>HostCallFacade</c> does,
/// <c>ExecutorSession</c> does not). Driven over a real pipe against <see cref="FakeHost"/>.</summary>
public sealed class HostToolCallsCallbackTests
{
    private const string RunId = "run-1";
    private const string ToolName = "echo";
    private const string DidNotAnswer = "the host did not answer 'echo':";

    [Fact]
    public async Task The_host_answer_is_returned_as_the_tool_result_and_the_request_carries_the_bound_run()
    {
        await using var callback = await Callback.StartAsync();
        callback.Host.On<ToolResult>(ExtensionProtocol.HostToolCall, (_, _) => Task.FromResult(new ToolResult("ran", false)));

        var result = await CallAsync(callback.Peer, """{"path":"a.txt"}""", labelDenied: true).Bounded();

        Assert.Equal("ran", result.Content);
        Assert.False(result.IsError);
        var request = Assert.Single(callback.Host.Requests(ExtensionProtocol.HostToolCall)).Read<HostToolCallRequest>();
        Assert.Equal(RunId, request.RunId);
        Assert.Equal(ToolName, request.Name);
        Assert.Equal("""{"path":"a.txt"}""", HostWire.Text(request.ArgumentsJson));
    }

    [Fact]
    public async Task Absent_and_null_arguments_travel_as_an_empty_payload()
    {
        await using var callback = await Callback.StartAsync();
        callback.Host.On<ToolResult>(ExtensionProtocol.HostToolCall, (_, _) => Task.FromResult(new ToolResult("ok")));

        await CallAsync(callback.Peer, argumentsJson: default, labelDenied: false).Bounded();
        await CallAsync(callback.Peer, "null", labelDenied: false).Bounded();

        var requests = callback.Host.Requests(ExtensionProtocol.HostToolCall);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Empty(request.Read<HostToolCallRequest>().ArgumentsJson.ToArray()));
    }

    [Theory]
    [InlineData("approval_denied", true, "denied: IPC remote error 'approval_denied' from 'host.tool_call': not permitted")]
    [InlineData("approval_denied", false, "approval_denied: IPC remote error 'approval_denied' from 'host.tool_call': not permitted")]
    [InlineData("unbound_call", true, "unbound_call: IPC remote error 'unbound_call' from 'host.tool_call': not permitted")]
    public async Task A_remote_error_reads_denied_only_when_the_code_says_so_and_the_caller_asks(
        string code,
        bool labelDenied,
        string expected)
    {
        await using var callback = await Callback.StartAsync();
        callback.Host.On(ExtensionProtocol.HostToolCall, (_, _) => throw new FakeHostError(code, "not permitted"));

        var result = await CallAsync(callback.Peer, argumentsJson: default, labelDenied).Bounded();

        Assert.True(result.IsError);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task A_host_that_never_answers_becomes_an_error_result_instead_of_a_timeout_exception()
    {
        await using var callback = await Callback.StartAsync(TimeSpan.FromMilliseconds(300));
        callback.Host.On(ExtensionProtocol.HostToolCall, FakeHost.NeverAnswer);

        var result = await CallAsync(callback.Peer, argumentsJson: default, labelDenied: true).Bounded();

        Assert.True(result.IsError);
        Assert.StartsWith(DidNotAnswer, result.Content, StringComparison.Ordinal);
        Assert.Contains("timed out", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dropped_connection_becomes_an_error_result_instead_of_a_closed_exception()
    {
        await using var callback = await Callback.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callback.Host.On(ExtensionProtocol.HostToolCall, async (message, ct) =>
        {
            entered.TrySetResult();
            return await FakeHost.NeverAnswer(message, ct);
        });

        var call = CallAsync(callback.Peer, argumentsJson: default, labelDenied: true);
        await entered.Task.Bounded();
        await callback.Host.DropConnectionAsync();
        var result = await call.Bounded();

        Assert.True(result.IsError);
        Assert.StartsWith(DidNotAnswer, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undecodable_answer_becomes_an_error_result_instead_of_a_protocol_exception()
    {
        await using var callback = await Callback.StartAsync();
        // A number where the operation documents a ToolResult: a wire-format violation, not a business error.
        callback.Host.On(
            ExtensionProtocol.HostToolCall,
            (_, _) => Task.FromResult(HostReply.Raw(HostWire.Payload(42))));

        var result = await CallAsync(callback.Peer, argumentsJson: default, labelDenied: true).Bounded();

        Assert.True(result.IsError);
        Assert.StartsWith(DidNotAnswer, result.Content, StringComparison.Ordinal);
        Assert.False(callback.Peer.IsClosed);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_thrown_not_mapped_to_a_result()
    {
        await using var callback = await Callback.StartAsync();
        callback.Host.On(ExtensionProtocol.HostToolCall, FakeHost.NeverAnswer);
        using var caller = new CancellationTokenSource();

        var call = HostToolCalls.CallAsync(
            callback.Peer, RunId, ToolName, arguments: default, labelDenied: true, caller.Token);
        await callback.Host.WaitForRequestsAsync(ExtensionProtocol.HostToolCall, 1);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Bounded());
    }

    [Fact]
    public async Task A_blank_tool_name_is_refused_before_anything_is_sent()
    {
        await using var callback = await Callback.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => CallAsync(callback.Peer, argumentsJson: default, labelDenied: true, name: " "));

        Assert.Empty(callback.Host.Requests(ExtensionProtocol.HostToolCall));
    }

    private static Task<HostToolResult> CallAsync(
        IpcPeer peer,
        string? argumentsJson = null,
        bool labelDenied = true,
        string name = ToolName) =>
        HostToolCalls.CallAsync(
            peer,
            RunId,
            name,
            argumentsJson is null ? default : Json.Element(argumentsJson),
            labelDenied,
            CancellationToken.None);

    /// <summary>The caller side of one callback: a client peer over a real pipe, answered by a <see cref="FakeHost"/>.
    /// The peer stops the transport but does not own it, so both are disposed here.</summary>
    private sealed class Callback(FakeHost host, NamedPipeClientTransport transport, IpcPeer peer) : IAsyncDisposable
    {
        public FakeHost Host { get; } = host;

        public IpcPeer Peer { get; } = peer;

        public static async Task<Callback> StartAsync(TimeSpan? requestTimeout = null)
        {
            var host = new FakeHost();
            var accepting = host.AcceptAsync();
            var transport = new NamedPipeClientTransport(host.PipeName);
            try
            {
                using var limit = TestLimit.Create();
                await transport.StartAsync(limit.Token).ConfigureAwait(false);
                var peer = new IpcPeer(
                    transport,
                    new IpcPeerOptions { DefaultTimeout = requestTimeout ?? TestLimit.Span });
                await peer.StartAsync(limit.Token).ConfigureAwait(false);
                await accepting.Bounded().ConfigureAwait(false);
                return new Callback(host, transport, peer);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            await Host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
