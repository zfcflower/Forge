using System.Net;
using System.Net.Sockets;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Net;
using Xunit;

namespace Forge.Core.Tests.Net;

/// <summary>Tests that replace <see cref="HttpClient.DefaultProxy"/> run alone: it is process-wide.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DefaultProxyCollection
{
    public const string Name = "default-proxy";
}

[Collection(DefaultProxyCollection.Name)]
public sealed class NetworkStackProxyTests
{
    private static readonly Uri Remote = new("https://api.example.com/v1");

    private static NetworkStack Stack(NetworkSettings settings) =>
        new(settings, _ => new TableResolver([]));

    [Fact]
    public void None_is_always_direct()
    {
        using var stack = Stack(NetworkSettings.Default with { Proxy = ProxyMode.None });
        Assert.Null(stack.Proxy.GetProxy(Remote));
        Assert.True(stack.Proxy.IsBypassed(Remote));
        Assert.True(stack.ConnectsDirectly(Remote));
        Assert.Null(stack.Proxy.Credentials);
    }

    [Fact]
    public void Manual_proxies_everything_but_bypass_and_loopback()
    {
        using var stack = Stack(NetworkSettings.Default with
        {
            Proxy = ProxyMode.Manual,
            ProxyUrl = "socks5://u:p@10.0.0.9:1081",
            ProxyBypass = ["*.corp.example", "intranet", "[fd00::5]"],
        });

        Assert.Equal(new Uri("socks5://10.0.0.9:1081/"), stack.Proxy.GetProxy(Remote));
        Assert.False(stack.ConnectsDirectly(Remote));
        var credentials = stack.Proxy.Credentials!.GetCredential(new Uri("socks5://10.0.0.9:1081"), "Basic");
        Assert.Equal("u", credentials!.UserName);

        Assert.Null(stack.Proxy.GetProxy(new Uri("http://git.corp.example/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://corp.example/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://intranet:8080/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://[fd00::5]/")));
        Assert.NotNull(stack.Proxy.GetProxy(new Uri("http://evilcorp.example/")));

        // Loopback is always direct, whatever the bypass list says.
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://localhost:3000/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://127.0.0.1:3000/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://[::1]:3000/")));
        Assert.Null(stack.Proxy.GetProxy(new Uri("http://ui.localhost/")));
    }

    [Fact]
    public void Manual_without_a_valid_url_falls_back_to_the_system_proxy()
    {
        var saved = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy("http://sys.proxy:3128");
            using var stack = Stack(NetworkSettings.Default with { Proxy = ProxyMode.Manual, ProxyUrl = "nonsense" });
            Assert.Equal(new Uri("http://sys.proxy:3128/"), stack.Proxy.GetProxy(Remote));
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    [Fact]
    public void System_follows_the_default_proxy_and_its_bypass()
    {
        var saved = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy("http://sys.proxy:3128", false, [@"^https?://skip\.example(:\d+)?/?$"]);
            using var stack = Stack(NetworkSettings.Default);
            Assert.Equal(new Uri("http://sys.proxy:3128/"), stack.Proxy.GetProxy(Remote));
            Assert.Null(stack.Proxy.GetProxy(new Uri("https://skip.example/")));
            Assert.Null(stack.Proxy.GetProxy(new Uri("http://localhost/")));

            // A default proxy that echoes the destination (the "no proxy" answer of the OS proxy) means direct.
            HttpClient.DefaultProxy = new EchoProxy();
            Assert.Null(stack.Proxy.GetProxy(Remote));
            Assert.True(stack.ConnectsDirectly(Remote));
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }

    [Fact]
    public void Apply_changes_the_live_proxy_and_raises_the_event()
    {
        using var stack = Stack(NetworkSettings.Default with { Proxy = ProxyMode.None });
        var proxy = stack.Proxy;
        var raised = 0;
        stack.SettingsChanged += (_, _) => raised++;

        stack.Apply(NetworkSettings.Default with { Proxy = ProxyMode.Manual, ProxyUrl = "http://p.example:8080" });
        Assert.Same(proxy, stack.Proxy);
        Assert.Equal(new Uri("http://p.example:8080/"), proxy.GetProxy(Remote));
        Assert.Equal(ProxyMode.Manual, stack.Settings.Proxy);

        stack.Apply(NetworkSettings.Default with { Proxy = ProxyMode.None });
        Assert.Null(proxy.GetProxy(Remote));
        Assert.Equal(2, raised);
    }

    private sealed class EchoProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => destination;

        public bool IsBypassed(Uri host) => false;
    }
}

public sealed class NetworkStackConnectTests
{
    [Fact]
    public async Task Resolution_goes_through_the_configured_resolver_and_live_changes_apply()
    {
        var first = new TableResolver(new() { ["svc.test"] = [IPAddress.Parse("192.0.2.1")] });
        var second = new TableResolver(new() { ["svc.test"] = [IPAddress.Parse("192.0.2.2")] });
        using var stack = new NetworkStack(NetworkSettings.Default, s => s.Dns == DnsMode.System ? first : second);

        Assert.Equal([IPAddress.Parse("192.0.2.1")], await stack.ResolveAsync("svc.test", default));
        stack.Apply(NetworkSettings.Default with { Dns = DnsMode.Servers, DnsServers = ["1.1.1.1"] });
        Assert.Equal([IPAddress.Parse("192.0.2.2")], await stack.ResolveAsync("svc.test", default));
        Assert.Same(second, stack.CreateResolver(stack.Settings));
    }

    [Fact]
    public void Built_in_resolvers_follow_the_mode_and_fall_back_on_invalid_settings()
    {
        using var stack = new NetworkStack();
        Assert.Same(SystemDnsResolver.Instance, stack.CreateResolver(NetworkSettings.Default));
        Assert.IsType<DnsServerResolver>(stack.CreateResolver(NetworkSettings.Default with { Dns = DnsMode.Servers, DnsServers = ["9.9.9.9"] }));
        Assert.IsType<DohResolver>(stack.CreateResolver(NetworkSettings.Default with { Dns = DnsMode.DnsOverHttps, DohUrl = "https://9.9.9.9/dns-query" }));
        Assert.Same(SystemDnsResolver.Instance, stack.CreateResolver(NetworkSettings.Default with { Dns = DnsMode.Servers, DnsServers = ["dns.google"] }));
        Assert.Same(SystemDnsResolver.Instance, stack.CreateResolver(NetworkSettings.Default with { Dns = DnsMode.DnsOverHttps, DohUrl = "http://x" }));
    }

    [Fact]
    public async Task Handler_dials_the_address_the_resolver_returns()
    {
        using var server = new TinyHttpServer();
        var resolver = new TableResolver(new() { ["app.test"] = [IPAddress.Loopback] });
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => resolver);
        using var http = new HttpClient(stack.CreateHandler());

        var body = await http.GetStringAsync($"http://app.test:{server.Port}/hello");
        Assert.Equal("ok", body);
        Assert.Contains("app.test", resolver.Asked);
        Assert.StartsWith("GET /hello ", server.LastRequestLine);
    }

    [Fact]
    public async Task Address_filter_refuses_a_host_that_resolves_to_a_refused_address()
    {
        using var server = new TinyHttpServer();
        var resolver = new TableResolver(new() { ["rebind.test"] = [IPAddress.Loopback] });
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => resolver);
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions { AddressFilter = a => !IPAddress.IsLoopback(a) }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"http://rebind.test:{server.Port}/"));
        var refused = NetworkAddressRefusedException.Find(error);
        Assert.NotNull(refused);
        Assert.Equal("rebind.test", refused!.Host);
        Assert.Equal(IPAddress.Loopback, refused.Address);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task Address_filter_skips_refused_addresses_and_dials_the_allowed_one()
    {
        using var server = new TinyHttpServer();
        // 127.0.0.2 is refused; 127.0.0.1 (where the server listens) is allowed.
        var resolver = new TableResolver(new() { ["mixed.test"] = [IPAddress.Parse("127.0.0.2"), IPAddress.Loopback] });
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => resolver);
        var seen = new List<IPAddress>();
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions
        {
            AddressFilter = a =>
            {
                lock (seen)
                {
                    seen.Add(a);
                }

                return a.Equals(IPAddress.Loopback);
            },
        }));

        Assert.Equal("ok", await http.GetStringAsync($"http://mixed.test:{server.Port}/"));
        Assert.Contains(IPAddress.Parse("127.0.0.2"), seen);
    }

    [Fact]
    public async Task Ip_literal_is_filtered_without_resolution()
    {
        var resolver = new TableResolver([]);
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => resolver);
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions { AddressFilter = _ => false }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:9/"));
        Assert.Equal(IPAddress.Loopback, NetworkAddressRefusedException.Find(error)!.Address);
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task Resolution_failure_surfaces_as_a_request_failure()
    {
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => new TableResolver([]));
        using var http = new HttpClient(stack.CreateHandler());

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://nowhere.test/"));
        Assert.Null(NetworkAddressRefusedException.Find(error));
        Assert.IsType<DnsResolutionException>(error.InnerException);
    }

    [Fact]
    public async Task Proxy_connection_is_not_filtered_and_the_target_is_not_resolved_locally()
    {
        using var proxy = new TinyHttpServer();
        var resolver = new TableResolver([]);
        using var stack = new NetworkStack(
            NetworkSettings.Default with { Proxy = ProxyMode.Manual, ProxyUrl = $"http://127.0.0.1:{proxy.Port}" },
            _ => resolver);
        // The filter refuses everything: a direct connection could not be made at all.
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions { AddressFilter = _ => false }));

        Assert.Equal("ok", await http.GetStringAsync("http://far-away.test/page"));
        Assert.Equal("GET http://far-away.test/page HTTP/1.1", proxy.LastRequestLine);
        Assert.Empty(resolver.Asked);
    }

    [Fact]
    public async Task Proxy_change_applies_to_the_next_connection_of_an_existing_client()
    {
        using var proxy = new TinyHttpServer();
        using var direct = new TinyHttpServer();
        var resolver = new TableResolver(new() { ["site.test"] = [IPAddress.Loopback] });
        using var stack = new NetworkStack(NetworkSettings.Default with { Proxy = ProxyMode.None }, _ => resolver);
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions { PooledConnectionLifetime = TimeSpan.Zero }));
        var url = $"http://site.test:{direct.Port}/";

        await http.GetStringAsync(url);
        Assert.Equal(1, direct.Requests);

        stack.Apply(NetworkSettings.Default with { Proxy = ProxyMode.Manual, ProxyUrl = $"http://127.0.0.1:{proxy.Port}" });
        await http.GetStringAsync(url);
        Assert.Equal(1, proxy.Requests);
        Assert.Equal(1, direct.Requests);
    }

    /// <summary>Answers every HTTP/1.1 request with 200 "ok" and closes the connection.</summary>
    private sealed class TinyHttpServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private int requests;
        private string? lastRequestLine;

        public TinyHttpServer()
        {
            listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref requests);

        public string? LastRequestLine => Volatile.Read(ref lastRequestLine);

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }

        private async Task LoopAsync()
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

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                            var line = await reader.ReadLineAsync(stop.Token);
                            if (line is null)
                            {
                                return;
                            }

                            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token)))
                            {
                            }

                            Volatile.Write(ref lastRequestLine, line);
                            Interlocked.Increment(ref requests);
                            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                            await stream.WriteAsync(response, stop.Token);
                        }
                        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                        {
                        }
                    }
                });
            }
        }
    }
}
