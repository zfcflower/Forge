using System.Collections.Concurrent;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed class IpcPeerLifecycleTests
{
    [Fact]
    public async Task Transport_failure_faults_every_pending_request_with_connection_closed()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var closed = new ConcurrentQueue<Exception?>();
        peer.Closed += closed.Enqueue;

        var first = peer.RequestAsync("a", IpcEmpty.Instance, CancellationToken.None);
        var second = peer.RequestAsync("b", IpcEmpty.Instance, CancellationToken.None);
        await remote.ReceiveMessageAsync();
        await remote.ReceiveMessageAsync();

        remote.Fail(new IOException("connection reset"));

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => first.Bounded());
        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => second.Bounded());
        Assert.True(peer.IsClosed);
        Assert.IsType<IpcConnectionClosedException>(Assert.Single(closed));
    }

    [Fact]
    public async Task Transport_raising_closed_twice_raises_peer_closed_once()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var closed = new ConcurrentQueue<Exception?>();
        peer.Closed += closed.Enqueue;

        remote.Fail(new IOException("first"));
        remote.Fail(new IOException("second"));

        Assert.Single(closed);
        Assert.True(peer.IsClosed);
    }

    [Fact]
    public async Task Clean_stop_raises_closed_with_null_cause_once_and_second_stop_is_a_no_op()
    {
        var (local, _) = await LoopbackTransport.ConnectAsync();
        var peer = Peers.Create(local);
        var closed = new ConcurrentQueue<Exception?>();
        peer.Closed += closed.Enqueue;

        await peer.StopAsync(CancellationToken.None);
        await peer.StopAsync(CancellationToken.None);

        Assert.True(peer.IsClosed);
        Assert.Null(Assert.Single(closed));
        await peer.DisposeAsync();
    }

    [Fact]
    public async Task Clean_stop_faults_pending_requests_with_connection_closed()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);

        var call = peer.RequestAsync("hang", IpcEmpty.Instance, CancellationToken.None);
        await remote.ReceiveMessageAsync();
        await peer.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => call.Bounded());
    }

    [Fact]
    public async Task Dispose_faults_pending_requests_and_a_second_dispose_is_safe()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        var peer = Peers.Create(local);

        var call = peer.RequestAsync("hang", IpcEmpty.Instance, CancellationToken.None);
        await remote.ReceiveMessageAsync();
        await peer.DisposeAsync();
        await peer.DisposeAsync();

        await Assert.ThrowsAsync<IpcConnectionClosedException>(() => call.Bounded());
    }

    [Fact]
    public async Task Operations_after_close_throw_connection_closed_without_sending()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        await peer.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await peer.RequestAsync("x", IpcEmpty.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await peer.NotifyAsync("x", IpcEmpty.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await peer.RespondAsync(1, IpcEmpty.Instance, CancellationToken.None));
        await Assert.ThrowsAsync<IpcConnectionClosedException>(async () => await peer.RespondErrorAsync(1, new IpcError("c", "m"), CancellationToken.None));
        Assert.False(remote.IsConnected);
    }

    [Fact]
    public async Task Remote_disconnect_marks_the_local_peer_closed_with_a_cause()
    {
        var (local, remote) = await LoopbackTransport.ConnectAsync();
        await using var peer = Peers.Create(local);
        var causes = new ConcurrentQueue<Exception?>();
        peer.Closed += causes.Enqueue;

        remote.Fail(new IOException("gone"));

        Assert.True(peer.IsClosed);
        Assert.NotNull(Assert.Single(causes));
    }
}
