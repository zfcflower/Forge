using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Net;
using Xunit;

namespace Forge.Core.Tests.Net;

/// <summary>A settings change replaces the handler's inner SocketsHttpHandler; in-flight requests finish on the old one.</summary>
public sealed class NetworkStackSwapTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static NetworkSettings NoProxy => NetworkSettings.Default with { Proxy = ProxyMode.None };

    private static NetworkSettings Other => NetworkSettings.Default with { Proxy = ProxyMode.None, ProxyBypass = ["x.test"] };

    private static NetworkSettings Via(KeepAliveServer proxy) =>
        NetworkSettings.Default with { Proxy = ProxyMode.Manual, ProxyUrl = $"http://127.0.0.1:{proxy.Port}" };

    private static NetworkStack Stack(NetworkSettings settings) =>
        new(settings, _ => new TableResolver(new() { ["site.test"] = [IPAddress.Loopback] }));

    [Fact]
    public async Task Proxy_switch_takes_effect_at_once_and_old_pooled_connections_are_not_reused()
    {
        using var direct = new KeepAliveServer();
        using var proxy = new KeepAliveServer();
        using var stack = Stack(NoProxy);
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var http = new HttpClient(handler);
        var url = $"http://site.test:{direct.Port}/x";

        await http.GetStringAsync(url);
        await http.GetStringAsync(url);
        Assert.Equal(1, direct.Connections);
        Assert.Equal(2, direct.Requests);

        stack.Apply(Via(proxy));
        await http.GetStringAsync(url);
        Assert.Equal(1, proxy.Requests);
        Assert.Equal(2, direct.Requests);

        stack.Apply(NoProxy);
        await http.GetStringAsync(url);
        Assert.Equal(1, proxy.Requests);
        Assert.Equal(3, direct.Requests);
        Assert.Equal(2, direct.Connections);
        Assert.Equal(2, handler.SwapCount);
        Assert.Equal(2, handler.DisposedInnerCount);
    }

    [Fact]
    public async Task Equal_settings_do_not_swap()
    {
        using var server = new KeepAliveServer();
        using var stack = Stack(NetworkSettings.Default with { Proxy = ProxyMode.None, DnsServers = ["1.1.1.1"], ProxyBypass = ["a.test"] });
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var http = new HttpClient(handler);
        var url = $"http://site.test:{server.Port}/";
        await http.GetStringAsync(url);
        var inner = handler.CurrentInner;

        stack.Apply(NetworkSettings.Default with { Proxy = ProxyMode.None, DnsServers = ["1.1.1.1"], ProxyBypass = ["a.test"] });
        await http.GetStringAsync(url);

        Assert.Equal(0, handler.SwapCount);
        Assert.Same(inner, handler.CurrentInner);
        Assert.Equal(1, server.Connections);

        stack.Apply(NetworkSettings.Default with { Proxy = ProxyMode.None, DnsServers = ["1.1.1.1"], ProxyBypass = ["b.test"] });
        Assert.Equal(1, handler.SwapCount);
        Assert.NotSame(inner, handler.CurrentInner);
    }

    [Fact]
    public async Task In_flight_request_finishes_after_a_swap_and_the_old_inner_is_disposed_afterwards()
    {
        using var server = new KeepAliveServer();
        using var stack = Stack(NoProxy);
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var http = new HttpClient(handler);

        var pending = http.GetStringAsync($"http://site.test:{server.Port}/hold");
        await server.HoldReached.WaitAsync(Wait);

        stack.Apply(Other);
        Assert.Equal(1, handler.SwapCount);
        Assert.Equal(0, handler.DisposedInnerCount);
        Assert.False(pending.IsCompleted);

        server.ReleaseHold();
        Assert.Equal("ok", await pending.WaitAsync(Wait));
        Assert.Equal(1, handler.DisposedInnerCount);
    }

    [Fact]
    public async Task Response_body_started_before_a_swap_can_still_be_read()
    {
        using var server = new KeepAliveServer();
        using var stack = Stack(NoProxy);
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var http = new HttpClient(handler);

        using var response = await http.GetAsync($"http://site.test:{server.Port}/stream", HttpCompletionOption.ResponseHeadersRead).WaitAsync(Wait);
        stack.Apply(Other);
        Assert.Equal(1, handler.DisposedInnerCount);

        server.ReleaseHold();
        Assert.Equal("abcd", await response.Content.ReadAsStringAsync().WaitAsync(Wait));
    }

    [Fact]
    public async Task Concurrent_requests_during_repeated_swaps_all_succeed_and_every_retired_inner_is_disposed()
    {
        using var direct = new KeepAliveServer();
        using var proxy = new KeepAliveServer();
        using var stack = Stack(NoProxy);
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var http = new HttpClient(handler);
        var url = $"http://site.test:{direct.Port}/x";

        var swapper = Task.Run(() =>
        {
            for (var i = 0; i < 60; i++)
            {
                stack.Apply(i % 2 == 0 ? Via(proxy) : NoProxy);
                Thread.Yield();
            }
        });

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                Assert.Equal("ok", await http.GetStringAsync(url));
            }
        })).ToArray();

        await Task.WhenAll(workers.Append(swapper)).WaitAsync(Wait);
        Assert.Equal(60, handler.SwapCount);
        Assert.Equal(60, handler.RetiredInnerCount);
        Assert.Equal(60, handler.DisposedInnerCount);
        Assert.Equal(320, direct.Requests + proxy.Requests);

        http.Dispose();
        Assert.Equal(61, handler.DisposedInnerCount);
    }

    [Fact]
    public async Task Dispose_unsubscribes_disposes_the_inner_and_rejects_requests()
    {
        using var stack = Stack(NoProxy);
        var handler = (SwappableNetworkHandler)stack.CreateHandler();
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        handler.Dispose();
        Assert.Equal(1, handler.DisposedInnerCount);

        stack.Apply(Other);
        Assert.Equal(0, handler.SwapCount);
        Assert.Equal(1, handler.DisposedInnerCount);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://site.test/"), CancellationToken.None));
        handler.Dispose();
    }

    [Fact]
    public void A_disposed_wrapper_is_not_kept_alive_by_the_stack()
    {
        using var stack = Stack(NoProxy);
        var weak = CreateAndDispose(stack);
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDispose(NetworkStack stack)
    {
        var handler = stack.CreateHandler();
        handler.Dispose();
        return new WeakReference(handler);
    }

    [Fact]
    public async Task Address_filter_is_still_enforced_after_a_swap()
    {
        using var server = new KeepAliveServer();
        using var stack = Stack(NoProxy);
        var checks = 0;
        var handler = (SwappableNetworkHandler)stack.CreateHandler(new NetworkHandlerOptions
        {
            AddressFilter = a =>
            {
                Interlocked.Increment(ref checks);
                return !IPAddress.IsLoopback(a);
            },
        });
        using var http = new HttpClient(handler);
        var url = $"http://site.test:{server.Port}/";

        stack.Apply(Other);
        Assert.Equal(1, handler.SwapCount);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync(url));
        Assert.NotNull(NetworkAddressRefusedException.Find(error));
        Assert.Equal(1, Volatile.Read(ref checks));
        Assert.Equal(0, server.Requests);
    }

    /// <summary>Keep-alive HTTP/1.1 server. <c>/hold</c> waits for <see cref="ReleaseHold"/> before answering; <c>/stream</c> sends two body bytes, waits, then the rest.</summary>
    private sealed class KeepAliveServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource holdReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requests;
        private int connections;

        public KeepAliveServer()
        {
            listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref requests);

        public int Connections => Volatile.Read(ref connections);

        public Task HoldReached => holdReached.Task;

        public void ReleaseHold() => gate.TrySetResult();

        public void Dispose()
        {
            stop.Cancel();
            gate.TrySetResult();
            listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
                {
                    return;
                }

                Interlocked.Increment(ref connections);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    while (!stop.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(stop.Token);
                        if (line is null)
                        {
                            return;
                        }

                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token)))
                        {
                        }

                        Interlocked.Increment(ref requests);
                        if (line.Contains("/hold", StringComparison.Ordinal))
                        {
                            holdReached.TrySetResult();
                            await gate.Task.WaitAsync(stop.Token);
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                        }
                        else if (line.Contains("/stream", StringComparison.Ordinal))
                        {
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nab");
                            await gate.Task.WaitAsync(stop.Token);
                            await WriteAsync(stream, "cd");
                        }
                        else
                        {
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                        }
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                {
                }
            }
        }

        private async Task WriteAsync(NetworkStream stream, string text) =>
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), stop.Token);
    }
}
