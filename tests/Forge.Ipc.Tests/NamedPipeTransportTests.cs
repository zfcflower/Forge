using System.Collections.Concurrent;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class NamedPipeTransportTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public void Validate_pipe_name_rejects_blank_and_separator_names(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => IpcPipe.ValidatePipeName(name));
    }

    [Fact]
    public void Validate_pipe_name_rejects_names_longer_than_108_characters()
    {
        Assert.Throws<ArgumentException>(() => IpcPipe.ValidatePipeName(new string('a', 109)));
    }

    [Fact]
    public void Validate_pipe_name_accepts_a_108_character_name_unchanged()
    {
        var name = new string('a', 108);

        Assert.Equal(name, IpcPipe.ValidatePipeName(name));
    }

    [Fact]
    public async Task Max_name_length_is_the_longest_name_a_real_pipe_accepts()
    {
        // The budget is derived from the platform's sockaddr_un.sun_path limit minus the temporary directory the BCL
        // keeps the socket file in, so a name of exactly this length must bind on Linux and on macOS.
        var name = TestFrames.Padded(IpcPipe.MaxNameLength);
        Assert.True(IpcPipe.MaxNameLength >= 16, $"Only {IpcPipe.MaxNameLength} characters are left for a pipe name.");

        var serverTask = IpcPipe.CreateServer(name);
        await using var client = await IpcPipe.CreateClient(name).Bounded();
        await using var server = await serverTask.Bounded();

        Assert.Equal(name, server.PipeName);
        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public void Transport_constructor_validates_the_pipe_name()
    {
        Assert.Throws<ArgumentException>(() => new NamedPipeClientTransport("bad/name"));
        Assert.Throws<ArgumentException>(() => new NamedPipeServerTransport("bad:name"));
    }

    [Fact]
    public async Task Server_without_a_client_times_out_with_timeout_exception()
    {
        var options = new IpcPipeOptions { ConnectTimeout = TestTimeouts.Short };

        var ex = await Assert.ThrowsAsync<IpcTimeoutException>(() => IpcPipe.CreateServer(TestPipes.NewName(), options).Bounded());

        Assert.Equal(TestTimeouts.Short, ex.Timeout);
    }

    [Fact]
    public async Task Send_before_connect_throws_connection_closed()
    {
        await using var client = new NamedPipeClientTransport(TestPipes.NewName());

        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () =>
            await client.SendAsync(TestFrames.Prefix([1, 2, 3]), CancellationToken.None));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Connect_round_trip_and_clean_close_over_a_real_pipe()
    {
        var name = TestPipes.NewName();
        var serverTask = IpcPipe.CreateServer(name);
        await using var client = await IpcPipe.CreateClient(name).Bounded();
        await using var server = await serverTask.Bounded();
        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);

        await using var serverPeer = Peers.Create(server);
        serverPeer.RequestReceived += async (message, ct) => await serverPeer.RespondAsync(message.Id, message.Read<EchoArgs>(), ct);
        await serverPeer.StartAsync(CancellationToken.None);
        await using var clientPeer = Peers.Create(client);
        var clientClosed = new ConcurrentQueue<Exception?>();
        clientPeer.Closed += clientClosed.Enqueue;
        var serverClosed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Closed += cause => serverClosed.TrySetResult(cause);

        var args = new EchoArgs("hé", 150);
        var result = await clientPeer.RequestAsync<EchoArgs, EchoArgs>("echo", args, CancellationToken.None).Bounded();
        Assert.Equal(args, result);

        await clientPeer.StopAsync(CancellationToken.None);

        Assert.True(clientPeer.IsClosed);
        Assert.Null(Assert.Single(clientClosed));
        Assert.False(client.IsConnected);
        Assert.NotNull(await serverClosed.Task.Bounded());
    }

    [Fact]
    public async Task Transport_stop_raises_closed_with_null_cause_once_and_second_stop_is_a_no_op()
    {
        var name = TestPipes.NewName();
        var serverTask = IpcPipe.CreateServer(name);
        await using var client = await IpcPipe.CreateClient(name).Bounded();
        await using var server = await serverTask.Bounded();
        var clientClosed = new ConcurrentQueue<Exception?>();
        client.Closed += clientClosed.Enqueue;

        await client.StopAsync(CancellationToken.None);
        await client.StopAsync(CancellationToken.None);

        Assert.False(client.IsConnected);
        Assert.Null(Assert.Single(clientClosed));
    }

    [Fact]
    public async Task Server_stop_faults_the_client_pending_request_with_connection_closed()
    {
        var name = TestPipes.NewName();
        var serverTask = IpcPipe.CreateServer(name);
        await using var client = await IpcPipe.CreateClient(name).Bounded();
        var server = await serverTask.Bounded();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverPeer = Peers.Create(server);
        serverPeer.RequestReceived += (_, _) =>
        {
            received.TrySetResult();
            return Task.CompletedTask;
        };
        await serverPeer.StartAsync(CancellationToken.None);
        await using var clientPeer = Peers.Create(client, TestTimeouts.Request);
        var clientCause = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientPeer.Closed += cause => clientCause.TrySetResult(cause);

        var call = clientPeer.RequestAsync("hang", IpcEmpty.Instance, CancellationToken.None);
        await received.Task.Bounded();
        await serverPeer.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => call.Bounded());
        Assert.NotNull(await clientCause.Task.Bounded());
        await serverPeer.DisposeAsync();
    }
}
