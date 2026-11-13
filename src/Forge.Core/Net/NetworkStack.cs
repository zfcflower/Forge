using System.Net;
using System.Net.Sockets;
using Forge.Abstractions;

namespace Forge.Core.Net;

/// <summary>
/// The dialled address was refused by <see cref="NetworkHandlerOptions.AddressFilter"/>. Thrown from the connect callback,
/// so it reaches the caller as the inner exception of the handler's own <see cref="HttpRequestException"/>.
/// </summary>
public sealed class NetworkAddressRefusedException(string host, IPAddress address)
    : HttpRequestException(HttpRequestError.ConnectionError, $"refused to connect to '{host}': its address {address} is not allowed for this connection.")
{
    public string Host { get; } = host;

    public IPAddress Address { get; } = address;

    /// <summary>The refusal anywhere in <paramref name="exception"/>'s inner-exception chain, or null.</summary>
    public static NetworkAddressRefusedException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NetworkAddressRefusedException refused)
            {
                return refused;
            }
        }

        return null;
    }
}

/// <summary>
/// The one <see cref="INetworkStack"/>: every handler it creates reads the current settings on each new connection.
/// <list type="bullet">
/// <item>Resolution: <see cref="DnsMode.System"/> → the OS; <see cref="DnsMode.Servers"/> → <see cref="DnsServerResolver"/>;
/// <see cref="DnsMode.DnsOverHttps"/> → <see cref="DohResolver"/> over a private client whose own lookups use the OS resolver
/// (the DoH server's host cannot be resolved through itself). Each <see cref="Apply"/> builds a fresh resolver, so its cache
/// starts empty.</item>
/// <item>Connecting: IPv4 addresses first, then IPv6 (when the OS has IPv6), tried one after another; every attempt but the
/// last gets an equal share of the connect timeout (at least 2 s), so one dead address does not use it all. Sockets have
/// NoDelay. A direct connection runs each address through <see cref="NetworkHandlerOptions.AddressFilter"/> before
/// dialling it — the checked address is the dialled address, so a re-resolving (rebinding) host is caught; refused
/// addresses are skipped and all refused throws <see cref="NetworkAddressRefusedException"/>.</item>
/// <item>Proxy: a live <see cref="IWebProxy"/> consulted per request. With a proxy, SocketsHttpHandler calls the connect
/// callback for the proxy's endpoint; that host is resolved through the same resolver but the address filter is not
/// applied (the proxy resolves the target; a caller that must vet the target checks it before sending). SOCKS4 (not 4a)
/// makes the handler resolve the target itself with the OS resolver.</item>
/// </list>
/// Settings that break the rules (<see cref="NetworkSettingsRules.Problems"/>) degrade instead of failing: no usable DNS
/// server or DoH URL falls back to the system resolver, an unusable manual proxy to the system proxy, bad bypass patterns
/// are ignored. Handlers never use cookies: one cookie jar shared by every provider and tool would leak between them.
/// </summary>
public sealed class NetworkStack : INetworkStack, IDisposable
{
    private static readonly TimeSpan MinAttemptTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<NetworkSettings, IDnsResolver>? resolverFactory;
    private readonly TimeProvider time;
    private readonly LiveProxy proxy;
    private readonly Lazy<HttpClient> dohClient;
    private State state;
    private int disposed;
    private int routeVersion;

    /// <param name="settings">The initial settings; null is <see cref="NetworkSettings.Default"/>.</param>
    /// <param name="resolverFactory">Replaces the built-in resolver choice (tests).</param>
    /// <param name="time">The clock of the DNS caches.</param>
    public NetworkStack(NetworkSettings? settings = null, Func<NetworkSettings, IDnsResolver>? resolverFactory = null, TimeProvider? time = null)
    {
        this.resolverFactory = resolverFactory;
        this.time = time ?? TimeProvider.System;
        proxy = new LiveProxy(this);
        dohClient = new Lazy<HttpClient>(CreateDohClient, LazyThreadSafetyMode.ExecutionAndPublication);
        state = Build(settings ?? NetworkSettings.Default);
    }

    public event EventHandler? SettingsChanged;

    public NetworkSettings Settings => Volatile.Read(ref state).Settings;

    /// <summary>The proxy every handler uses; it reads the current settings on each call.</summary>
    public IWebProxy Proxy => proxy;

    public void Apply(NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var next = Build(settings);
        var previous = Interlocked.Exchange(ref state, next);
        if (!SameSettings(previous.Settings, settings))
        {
            Interlocked.Increment(ref routeVersion);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Bumped by <see cref="Apply"/> only when the settings actually differ; wrappers swap their inner handler on a new value.</summary>
    internal int RouteVersion => Volatile.Read(ref routeVersion);

    public HttpMessageHandler CreateHandler(NetworkHandlerOptions? options = null)
    {
        var chosen = options ?? new NetworkHandlerOptions();
        return new SwappableNetworkHandler(this, () => CreateInner(chosen));
    }

    private SocketsHttpHandler CreateInner(NetworkHandlerOptions chosen) =>
        new()
        {
            AllowAutoRedirect = chosen.AllowAutoRedirect,
            AutomaticDecompression = chosen.AutomaticDecompression,
            ConnectTimeout = chosen.ConnectTimeout,
            PooledConnectionLifetime = chosen.PooledConnectionLifetime,
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            ConnectCallback = (context, ct) => ConnectAsync(context, chosen, ct),
        };

    private static bool SameSettings(NetworkSettings a, NetworkSettings b) =>
        a.Dns == b.Dns
        && a.Proxy == b.Proxy
        && string.Equals(a.DohUrl, b.DohUrl, StringComparison.Ordinal)
        && string.Equals(a.ProxyUrl, b.ProxyUrl, StringComparison.Ordinal)
        && a.DnsServers.SequenceEqual(b.DnsServers)
        && a.ProxyBypass.SequenceEqual(b.ProxyBypass);

    public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Volatile.Read(ref state).Resolver.ResolveAsync(host, ct);
    }

    /// <summary>A resolver for <paramref name="candidate"/> without applying it (the settings page's test button).</summary>
    public IDnsResolver CreateResolver(NetworkSettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return resolverFactory?.Invoke(candidate) ?? BuiltInResolver(candidate);
    }

    /// <summary>Whether a request to <paramref name="destination"/> would connect directly under the current settings.</summary>
    public bool ConnectsDirectly(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return proxy.GetProxy(destination) is null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && dohClient.IsValueCreated)
        {
            dohClient.Value.Dispose();
        }
    }

    private State Build(NetworkSettings settings)
    {
        Uri? manualProxy = null;
        NetworkCredential? credentials = null;
        if (settings.Proxy == ProxyMode.Manual && NetworkSettingsRules.TryParseProxyUrl(settings.ProxyUrl, out var parsed, out var parsedCredentials))
        {
            manualProxy = parsed;
            credentials = parsedCredentials;
        }

        var bypass = settings.ProxyBypass.Where(NetworkSettingsRules.IsValidBypassPattern).ToArray();
        return new State(settings, CreateResolver(settings), manualProxy, credentials, bypass);
    }

    private IDnsResolver BuiltInResolver(NetworkSettings settings)
    {
        switch (settings.Dns)
        {
            case DnsMode.Servers:
                var servers = new List<IPEndPoint>(settings.DnsServers.Count);
                foreach (var text in settings.DnsServers)
                {
                    if (NetworkSettingsRules.TryParseServer(text, out var endpoint))
                    {
                        servers.Add(endpoint);
                    }
                }

                return servers.Count > 0 ? new DnsServerResolver(servers, cache: new DnsCache(time)) : SystemDnsResolver.Instance;
            case DnsMode.DnsOverHttps when NetworkSettingsRules.IsValidDohUrl(settings.DohUrl):
                return new DohResolver(new Uri(settings.DohUrl!.Trim()), dohClient.Value, cache: new DnsCache(time));
            default:
                return SystemDnsResolver.Instance;
        }
    }

    /// <summary>The DoH transport: the live proxy, but the OS resolver for the DoH host itself.</summary>
    private HttpClient CreateDohClient() =>
        new(new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, NetworkHandlerOptions options, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var host = NetworkSettingsRules.Unbracket(endpoint.Host);
        var filter = IsDirect(context, host, endpoint.Port) ? options.AddressFilter : null;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Volatile.Read(ref state).Resolver.ResolveAsync(host, ct).ConfigureAwait(false);

        var candidates = Order(addresses, filter, host);
        var attemptTimeout = candidates.Count > 1
            ? TimeSpan.FromTicks(Math.Max(MinAttemptTimeout.Ticks, options.ConnectTimeout.Ticks / candidates.Count))
            : Timeout.InfiniteTimeSpan;
        Exception? last = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var address = candidates[i];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (i < candidates.Count - 1 && attemptTimeout != Timeout.InfiniteTimeSpan)
            {
                attempt.CancelAfter(attemptTimeout);
            }

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = exception;
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            $"could not connect to {host}:{endpoint.Port}: {last?.Message}",
            last);
    }

    /// <summary>IPv4 first, then IPv6 (dropped when the OS has no IPv6); the filter removes refused addresses.</summary>
    private static List<IPAddress> Order(IPAddress[] addresses, Func<IPAddress, bool>? filter, string host)
    {
        var result = new List<IPAddress>(addresses.Length);
        IPAddress? refused = null;
        foreach (var family in (ReadOnlySpan<AddressFamily>)[AddressFamily.InterNetwork, AddressFamily.InterNetworkV6])
        {
            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            {
                continue;
            }

            foreach (var address in addresses)
            {
                if (address.AddressFamily != family)
                {
                    continue;
                }

                if (filter is not null && !filter(address))
                {
                    refused ??= address;
                    continue;
                }

                result.Add(address);
            }
        }

        if (result.Count == 0)
        {
            if (refused is not null)
            {
                throw new NetworkAddressRefusedException(host, refused);
            }

            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"'{host}' has no usable address.");
        }

        return result;
    }

    /// <summary>
    /// The handler dials either the request's own host or a proxy. The connection is direct when the endpoint it asks for
    /// is the request target itself. An https request through an http proxy first sends a CONNECT whose request URI is the
    /// proxy, so CONNECT is never direct.
    /// </summary>
    private static bool IsDirect(SocketsHttpConnectionContext context, string host, int port)
    {
        var request = context.InitialRequestMessage;
        var target = request.RequestUri;
        return target is not null
            && request.Method != HttpMethod.Connect
            && target.Port == port
            && string.Equals(NetworkSettingsRules.Unbracket(target.IdnHost), host, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record State(NetworkSettings Settings, IDnsResolver Resolver, Uri? ManualProxy, NetworkCredential? Credentials, string[] Bypass);

    /// <summary>The proxy choice, re-read from the stack's current settings on every call.</summary>
    private sealed class LiveProxy(NetworkStack owner) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get
            {
                var current = Volatile.Read(ref owner.state);
                return current.Settings.Proxy switch
                {
                    ProxyMode.Manual when current.ManualProxy is not null => current.Credentials,
                    ProxyMode.None => null,
                    _ => HttpClient.DefaultProxy.Credentials,
                };
            }

            set
            {
                // The credentials come from the settings (manual URL) or the OS; a handler cannot override them.
            }
        }

        public Uri? GetProxy(Uri destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            var current = Volatile.Read(ref owner.state);
            var host = destination.IdnHost;
            if (NetworkSettingsRules.IsLoopbackHost(host))
            {
                return null;
            }

            switch (current.Settings.Proxy)
            {
                case ProxyMode.None:
                    return null;
                case ProxyMode.Manual when current.ManualProxy is { } manual:
                    foreach (var pattern in current.Bypass)
                    {
                        if (NetworkSettingsRules.MatchesBypass(host, pattern))
                        {
                            return null;
                        }
                    }

                    return manual;
                default:
                    // The OS/environment proxy: a WebProxy answers "bypassed" by echoing the destination back.
                    var system = HttpClient.DefaultProxy;
                    if (system.IsBypassed(destination))
                    {
                        return null;
                    }

                    var chosen = system.GetProxy(destination);
                    return chosen is null || chosen == destination ? null : chosen;
            }
        }

        public bool IsBypassed(Uri host) => GetProxy(host) is null;
    }
}
