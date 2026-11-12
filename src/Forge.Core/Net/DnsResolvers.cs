using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Forge.Core.Net;

/// <summary>Turns a host name into addresses. An IP literal comes back as itself.</summary>
public interface IDnsResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>
/// A host could not be resolved. A <see cref="SocketException"/> like the one <see cref="Dns.GetHostAddressesAsync(string)"/>
/// throws (<see cref="SocketError.HostNotFound"/> for a name that does not exist, <see cref="SocketError.TryAgain"/> when no
/// server answered), with a message that says which resolver failed and why.
/// </summary>
public sealed class DnsResolutionException(string host, SocketError error, string detail) : SocketException((int)error)
{
    public string Host { get; } = host;

    public override string Message { get; } = detail;
}

/// <summary>The operating system resolver.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public static SystemDnsResolver Instance { get; } = new();

    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = NetworkSettingsRules.Unbracket(host);
        return IPAddress.TryParse(name, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// A small TTL cache of resolved hosts (bounded entry count, TTL capped). Entries with a zero TTL are not stored; when it is
/// full, expired entries go first and then the entry that expires soonest.
/// </summary>
public sealed class DnsCache(TimeProvider? time = null, int capacity = 256, TimeSpan? maxTtl = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly int capacity = Math.Max(1, capacity);
    private readonly TimeSpan maxTtl = maxTtl ?? TimeSpan.FromHours(1);
    private readonly Dictionary<string, (IPAddress[] Addresses, DateTimeOffset Expires)> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public bool TryGet(string host, out IPAddress[] addresses)
    {
        lock (gate)
        {
            if (entries.TryGetValue(host, out var entry))
            {
                if (entry.Expires > time.GetUtcNow())
                {
                    addresses = entry.Addresses;
                    return true;
                }

                entries.Remove(host);
            }
        }

        addresses = [];
        return false;
    }

    public void Set(string host, IPAddress[] addresses, uint ttlSeconds)
    {
        if (ttlSeconds == 0 || addresses.Length == 0)
        {
            return;
        }

        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var expires = time.GetUtcNow() + (ttl < maxTtl ? ttl : maxTtl);
        lock (gate)
        {
            if (entries.Count >= capacity && !entries.ContainsKey(host))
            {
                Evict();
            }

            entries[host] = (addresses, expires);
        }
    }

    private void Evict()
    {
        var now = time.GetUtcNow();
        string? soonest = null;
        var soonestExpiry = DateTimeOffset.MaxValue;
        List<string>? expired = null;
        foreach (var (key, entry) in entries)
        {
            if (entry.Expires <= now)
            {
                (expired ??= []).Add(key);
            }
            else if (entry.Expires < soonestExpiry)
            {
                soonest = key;
                soonestExpiry = entry.Expires;
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                entries.Remove(key);
            }
        }
        else if (soonest is not null)
        {
            entries.Remove(soonest);
        }
    }
}

/// <summary>
/// The shared half of the plain-DNS and DoH resolvers: IP literals and <c>localhost</c> short-circuit, the cache is
/// consulted, A and AAAA are asked concurrently, and the answer lists IPv4 addresses before IPv6 ones (one family failing
/// does not fail the lookup when the other answered). NXDOMAIN or an empty answer is <see cref="SocketError.HostNotFound"/>.
/// </summary>
public abstract class WireDnsResolver : IDnsResolver
{
    private readonly DnsCache cache;

    protected WireDnsResolver(DnsCache? cache) => this.cache = cache ?? new DnsCache();

    /// <summary>A human name for messages (<c>DNS 1.1.1.1:53</c>, <c>DoH https://…</c>).</summary>
    protected abstract string Label { get; }

    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = NetworkSettingsRules.Unbracket(host.Trim());
        if (IPAddress.TryParse(name, out var literal))
        {
            return [literal];
        }

        if (name.EndsWith('.'))
        {
            name = name[..^1];
        }

        // RFC 6761: localhost never leaves the machine, whatever the configured server says.
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];
        }

        if (cache.TryGet(name, out var cached))
        {
            return cached;
        }

        try
        {
            DnsWire.QueryLength(name);
        }
        catch (ArgumentException exception)
        {
            throw new DnsResolutionException(name, SocketError.HostNotFound, $"'{name}' is not a valid DNS name: {exception.Message}");
        }

        var v4 = QueryAsync(name, DnsRecordType.A, ct);
        var v6 = QueryAsync(name, DnsRecordType.Aaaa, ct);
        // Both are awaited before either outcome is looked at, so a failure of one never leaves the other unobserved.
        await ((Task)Task.WhenAll(v4, v6)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ct.ThrowIfCancellationRequested();
        Exception? failure = null;
        var a = Outcome(v4, ref failure);
        var aaaa = Outcome(v6, ref failure);
        var count = (a?.Addresses.Count ?? 0) + (aaaa?.Addresses.Count ?? 0);
        if (count == 0)
        {
            if (failure is DnsResolutionException resolution)
            {
                throw resolution;
            }

            if (failure is not null)
            {
                throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label}: {failure.Message}");
            }

            throw new DnsResolutionException(name, SocketError.HostNotFound, $"{Label} has no address for '{name}'.");
        }

        var result = new IPAddress[count];
        var index = 0;
        uint ttl = uint.MaxValue;
        if (a is { } four)
        {
            four.Addresses.CopyTo(result, index);
            index += four.Addresses.Count;
            if (four.Addresses.Count > 0)
            {
                ttl = Math.Min(ttl, four.Ttl);
            }
        }

        if (aaaa is { } six)
        {
            six.Addresses.CopyTo(result, index);
            if (six.Addresses.Count > 0)
            {
                ttl = Math.Min(ttl, six.Ttl);
            }
        }

        // A family that failed outright is not cached around: the next lookup asks again.
        cache.Set(name, result, failure is null ? ttl : 0);
        return result;
    }

    private static (List<IPAddress> Addresses, uint Ttl)? Outcome(Task<(List<IPAddress> Addresses, uint Ttl)> task, ref Exception? failure)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        var exception = task.Exception?.InnerException;
        if (exception is DnsResolutionException or DnsFormatException)
        {
            failure ??= exception;
            return null;
        }

        // Anything else (a bug, a cancellation that was not ours) surfaces as it was thrown.
        task.GetAwaiter().GetResult();
        return null;
    }

    /// <summary>One validated answer to the question (<paramref name="name"/>, <paramref name="type"/>).</summary>
    protected abstract Task<DnsResponse> ExchangeAsync(string name, DnsRecordType type, CancellationToken ct);

    private async Task<(List<IPAddress> Addresses, uint Ttl)> QueryAsync(string name, DnsRecordType type, CancellationToken ct)
    {
        var response = await ExchangeAsync(name, type, ct).ConfigureAwait(false);
        if (response.ResponseCode == DnsWire.ResponseCodeNameError)
        {
            return ([], 0);
        }

        if (response.ResponseCode != DnsWire.ResponseCodeNoError)
        {
            throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} answered with response code {response.ResponseCode}.");
        }

        var addresses = DnsWire.Addresses(response, name, type, out var ttl);
        return (addresses, ttl);
    }

    protected static ushort NewId() => (ushort)RandomNumberGenerator.GetInt32(0, 65536);
}

/// <summary>
/// Plain DNS to a list of servers, tried in order: UDP with a random query id (and the OS's random source port), a reply
/// accepted only when it carries our id and our question; a truncated reply is asked again over TCP. A server that times
/// out (<see cref="Timeout"/>, per server and transport), refuses, fails (SERVFAIL/REFUSED) or sends garbage moves on to the
/// next one; NXDOMAIN is an answer and stops there.
/// </summary>
public sealed class DnsServerResolver : WireDnsResolver
{
    private const int UdpBufferSize = 4096;
    private const int MaxTcpMessage = 65535;
    private readonly IPEndPoint[] servers;

    public DnsServerResolver(IReadOnlyList<IPEndPoint> servers, TimeSpan? timeout = null, DnsCache? cache = null)
        : base(cache)
    {
        ArgumentNullException.ThrowIfNull(servers);
        if (servers.Count == 0)
        {
            throw new ArgumentException("At least one DNS server is required.", nameof(servers));
        }

        this.servers = [.. servers];
        Timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public TimeSpan Timeout { get; }

    protected override string Label => "DNS " + string.Join(", ", servers.Select(static s => s.ToString()));

    protected override async Task<DnsResponse> ExchangeAsync(string name, DnsRecordType type, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var server in servers)
        {
            try
            {
                var response = await UdpAsync(server, name, type, ct).ConfigureAwait(false);
                if (response.Truncated)
                {
                    response = await TcpAsync(server, name, type, ct).ConfigureAwait(false);
                }

                if (response.ResponseCode is DnsWire.ResponseCodeServerFailure or DnsWire.ResponseCodeRefused)
                {
                    last = new IOException($"{server} answered with response code {response.ResponseCode}");
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"{server} did not answer within {Timeout.TotalSeconds:0.#} s");
            }
            catch (Exception exception) when (exception is SocketException or IOException or DnsFormatException)
            {
                last = new IOException($"{server}: {exception.Message}", exception);
            }
        }

        throw new DnsResolutionException(name, SocketError.TryAgain, $"No DNS server answered for '{name}' ({last?.Message}).");
    }

    private async Task<DnsResponse> UdpAsync(IPEndPoint server, string name, DnsRecordType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var id = NewId();
        var query = DnsWire.EncodeQuery(id, name, type);
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, timeout.Token).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, timeout.Token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(UdpBufferSize);
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(0, UdpBufferSize), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                DnsResponse response;
                try
                {
                    response = DnsWire.Decode(buffer.AsSpan(0, received));
                }
                catch (DnsFormatException)
                {
                    // A stray or forged datagram is ignored; the real answer may still arrive before the timeout.
                    continue;
                }

                if (DnsWire.IsAnswerTo(response, id, name, type))
                {
                    return response;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<DnsResponse> TcpAsync(IPEndPoint server, string name, DnsRecordType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var id = NewId();
        var length = DnsWire.QueryLength(name);
        var query = new byte[length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)length);
        DnsWire.WriteQuery(query.AsSpan(2), id, name, type);
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(server, timeout.Token).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(query, timeout.Token).ConfigureAwait(false);
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, timeout.Token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (size is < DnsWire.HeaderLength or > MaxTcpMessage)
        {
            throw new DnsFormatException("The TCP DNS reply has an impossible length.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), timeout.Token).ConfigureAwait(false);
            var response = DnsWire.Decode(buffer.AsSpan(0, size));
            return DnsWire.IsAnswerTo(response, id, name, type)
                ? response
                : throw new DnsFormatException("The TCP DNS reply does not answer the query.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// DNS over HTTPS (RFC 8484): the wire query is POSTed as <c>application/dns-message</c> with id 0 (the RFC's cache-friendly
/// choice; TLS authenticates the server) and the reply must answer that question. The <see cref="HttpClient"/> handed in
/// must resolve the DoH server's own host some other way (the system resolver), or the lookup would recurse.
/// </summary>
public sealed class DohResolver : WireDnsResolver
{
    private const int MaxResponseBytes = 65535;
    private readonly Uri url;
    private readonly HttpClient http;

    public DohResolver(Uri url, HttpClient http, TimeSpan? timeout = null, DnsCache? cache = null)
        : base(cache)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(http);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A DoH URL must be https.", nameof(url));
        }

        this.url = url;
        this.http = http;
        Timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public TimeSpan Timeout { get; }

    protected override string Label => "DoH " + url;

    protected override async Task<DnsResponse> ExchangeAsync(string name, DnsRecordType type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var content = new ByteArrayContent(DnsWire.EncodeQuery(0, name, type));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.ParseAdd("application/dns-message");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} returned an oversized reply.");
            }

            var body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var decoded = DnsWire.Decode(body);
            return DnsWire.IsAnswerTo(decoded, 0, name, type)
                ? decoded
                : throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} returned a reply to another question.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} did not answer within {Timeout.TotalSeconds:0.#} s.");
        }
        catch (HttpRequestException exception)
        {
            throw new DnsResolutionException(name, SocketError.TryAgain, $"{Label} request failed: {exception.Message}");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxResponseBytes)
                {
                    throw new DnsFormatException("The DoH reply is larger than a DNS message can be.");
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }
}
