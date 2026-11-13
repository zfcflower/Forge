using System.Net;

namespace Forge.Abstractions;

/// <summary>How host names are resolved for every outbound connection Forge makes.</summary>
public enum DnsMode
{
    /// <summary>The operating system resolver.</summary>
    System,

    /// <summary>Plain DNS (UDP, TCP on truncation) to <see cref="NetworkSettings.DnsServers"/>, tried in order.</summary>
    Servers,

    /// <summary>DNS over HTTPS (RFC 8484 wire format) to <see cref="NetworkSettings.DohUrl"/>.</summary>
    DnsOverHttps,
}

/// <summary>Which proxy outbound HTTP connections go through.</summary>
public enum ProxyMode
{
    /// <summary>The operating system / environment proxy (<see cref="HttpClient.DefaultProxy"/>).</summary>
    System,

    /// <summary>Direct connections.</summary>
    None,

    /// <summary><see cref="NetworkSettings.ProxyUrl"/>: <c>http://</c>, <c>https://</c>, <c>socks4://</c>, <c>socks4a://</c> or <c>socks5://</c>.</summary>
    Manual,
}

/// <summary>
/// The <c>network</c> section of <c>settings.json</c>. <paramref name="DnsServers"/> are IP literals with an optional port
/// (<c>1.1.1.1</c>, <c>[2606:4700::1111]:53</c>); <paramref name="ProxyBypass"/> are host patterns that go direct
/// (<c>localhost</c>, <c>*.corp.example</c>). Loopback always goes direct.
/// </summary>
public sealed record NetworkSettings(
    DnsMode Dns,
    IReadOnlyList<string> DnsServers,
    string? DohUrl,
    ProxyMode Proxy,
    string? ProxyUrl,
    IReadOnlyList<string> ProxyBypass)
{
    public static NetworkSettings Default { get; } = new(DnsMode.System, [], null, ProxyMode.System, null, []);
}

/// <summary>Per-handler choices of an <see cref="INetworkStack"/> handler; the network settings themselves are shared.</summary>
public sealed record NetworkHandlerOptions
{
    public bool AllowAutoRedirect { get; init; } = true;

    public DecompressionMethods AutomaticDecompression { get; init; } = DecompressionMethods.None;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Consulted on every direct connection with the address about to be dialled (after resolution, so a host that
    /// re-resolves to another address is checked again); false refuses the connection with an <see cref="HttpRequestException"/>.
    /// Not consulted when the connection goes through a proxy (the proxy resolves the target).
    /// </summary>
    public Func<IPAddress, bool>? AddressFilter { get; init; }
}

/// <summary>
/// The one place outbound HTTP connections are made: providers, model listing and the models.dev catalog, web tools, MCP
/// over HTTP, automations. A handler reads the current <see cref="Settings"/> on every new connection (resolver and
/// proxy), so a settings change applies without rebuilding clients: a change that alters the route swaps the handler's
/// inner connection pool, new requests open fresh connections at once, and requests already in flight finish on the old one.
/// </summary>
public interface INetworkStack
{
    NetworkSettings Settings { get; }

    /// <summary>Replaces the settings (already validated); raises <see cref="SettingsChanged"/>.</summary>
    void Apply(NetworkSettings settings);

    event EventHandler? SettingsChanged;

    /// <summary>A new handler routed through the current settings. The caller owns it (usually via its <see cref="HttpClient"/>).</summary>
    HttpMessageHandler CreateHandler(NetworkHandlerOptions? options = null);

    /// <summary>Resolves <paramref name="host"/> with the configured resolver; an IP literal comes back as itself.</summary>
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}
