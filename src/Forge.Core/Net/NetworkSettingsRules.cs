using System.Globalization;
using System.Net;
using Forge.Abstractions;

namespace Forge.Core.Net;

/// <summary>
/// The validation and matching rules of <see cref="NetworkSettings"/>, shared by the settings store, the settings page and
/// <see cref="NetworkStack"/>: DNS servers are IP literals with an optional port, the DoH URL is https, the proxy URL is
/// http/https/socks4/socks4a/socks5, and the bypass list holds host patterns.
/// </summary>
public static class NetworkSettingsRules
{
    public const int DefaultDnsPort = 53;
    public const int DefaultSocksPort = 1080;

    private static readonly char[] ListSeparators = ['\r', '\n', ',', ';', ' ', '\t'];

    /// <summary>The proxy schemes SocketsHttpHandler speaks natively.</summary>
    public static IReadOnlyList<string> ProxySchemes { get; } = ["http", "https", "socks4", "socks4a", "socks5"];

    /// <summary>Splits a list typed one per line or comma/semicolon/space separated; trims, drops empties and duplicates.</summary>
    public static IReadOnlyList<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var part in text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!result.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(part);
            }
        }

        return result;
    }

    /// <summary>
    /// A DNS server: <c>1.1.1.1</c>, <c>1.1.1.1:5353</c>, <c>2606:4700::1111</c> or <c>[2606:4700::1111]:53</c>. IPv4 must be
    /// four dotted decimals (<see cref="IPAddress.TryParse(string, out IPAddress)"/> alone accepts <c>1</c>); the port is
    /// 1–65535 and defaults to 53. Host names are refused: resolving the resolver would need a resolver.
    /// </summary>
    public static bool TryParseServer(string? text, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        string addressText;
        string? portText = null;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            addressText = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                portText = rest[1..];
            }

            if (!addressText.Contains(':'))
            {
                return false;
            }
        }
        else
        {
            var colons = value.Count(static c => c == ':');
            if (colons == 1)
            {
                var split = value.IndexOf(':');
                addressText = value[..split];
                portText = value[(split + 1)..];
            }
            else
            {
                addressText = value;
            }
        }

        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IsDottedQuad(addressText))
        {
            return false;
        }

        var port = DefaultDnsPort;
        if (portText is not null
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    /// <summary>An absolute <c>https</c> URL with a host (RFC 8484 requires TLS).</summary>
    public static bool IsValidDohUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// A proxy URL in one of <see cref="ProxySchemes"/> with a host. <paramref name="proxy"/> is the URL without user info,
    /// path or query, with the scheme's default port filled in (1080 for SOCKS); <paramref name="credentials"/> comes
    /// from <c>user:password@</c> (percent-decoded) or is null.
    /// </summary>
    public static bool TryParseProxyUrl(string? text, out Uri proxy, out NetworkCredential? credentials)
    {
        proxy = null!;
        credentials = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
            || !ProxySchemes.Contains(uri.Scheme)
            || uri.Host.Length == 0
            || (uri.AbsolutePath.Length > 1)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        var port = uri.IsDefaultPort || uri.Port < 0
            ? uri.Scheme switch
            {
                "http" => 80,
                "https" => 443,
                _ => DefaultSocksPort,
            }
            : uri.Port;
        proxy = new UriBuilder(uri.Scheme, uri.Host, port).Uri;
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            var user = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            var password = colon < 0 ? string.Empty : Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
            credentials = new NetworkCredential(user, password);
        }

        return true;
    }

    /// <summary>
    /// A bypass pattern: <c>*</c> (everything), <c>*.example.com</c> or <c>.example.com</c> (the domain and every
    /// subdomain), or an exact host / IP literal. Letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, <c>:</c>, brackets and a
    /// leading <c>*</c> only.
    /// </summary>
    public static bool IsValidBypassPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var body = pattern.Trim();
        if (body == "*")
        {
            return true;
        }

        if (body.StartsWith("*.", StringComparison.Ordinal))
        {
            body = body[2..];
        }
        else if (body.StartsWith('.'))
        {
            body = body[1..];
        }

        if (body.Length == 0)
        {
            return false;
        }

        foreach (var c in body)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '[' or ']'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="host"/> matches the bypass <paramref name="pattern"/> (case-insensitive).</summary>
    public static bool MatchesBypass(string host, string pattern)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(pattern);
        var target = Unbracket(host).TrimEnd('.');
        var rule = pattern.Trim();
        if (rule == "*")
        {
            return true;
        }

        string? suffix = rule.StartsWith("*.", StringComparison.Ordinal) ? rule[2..]
            : rule.StartsWith('.') ? rule[1..]
            : null;
        if (suffix is null)
        {
            return string.Equals(target, Unbracket(rule), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(target, suffix, StringComparison.OrdinalIgnoreCase)
            || (target.Length > suffix.Length
                && target.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && target[target.Length - suffix.Length - 1] == '.');
    }

    /// <summary><c>localhost</c>, <c>*.localhost</c> and loopback IP literals: always connected to directly.</summary>
    public static bool IsLoopbackHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var target = Unbracket(host).TrimEnd('.');
        if (string.Equals(target, "localhost", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(target, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>Every rule violation of <paramref name="settings"/>, as field names (empty when valid).</summary>
    public static IReadOnlyList<string> Problems(NetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        if (settings.DnsServers.Any(static s => !TryParseServer(s, out _))
            || (settings.Dns == DnsMode.Servers && settings.DnsServers.Count == 0))
        {
            problems.Add("dnsServers");
        }

        if ((settings.DohUrl is not null && !IsValidDohUrl(settings.DohUrl))
            || (settings.Dns == DnsMode.DnsOverHttps && settings.DohUrl is null))
        {
            problems.Add("dohUrl");
        }

        if ((settings.ProxyUrl is not null && !TryParseProxyUrl(settings.ProxyUrl, out _, out _))
            || (settings.Proxy == ProxyMode.Manual && settings.ProxyUrl is null))
        {
            problems.Add("proxyUrl");
        }

        if (settings.ProxyBypass.Any(static p => !IsValidBypassPattern(p)))
        {
            problems.Add("proxyBypass");
        }

        return problems;
    }

    public static string Unbracket(string host) =>
        host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static bool IsDottedQuad(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }
}
