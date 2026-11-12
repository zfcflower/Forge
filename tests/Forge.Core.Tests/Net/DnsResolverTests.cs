using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Forge.Core.Net;
using Xunit;

namespace Forge.Core.Tests.Net;

public sealed class DnsServerResolverTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private static byte[] Answer(TestQuery query, string v4 = "192.0.2.10", string v6 = "2001:db8::10", uint ttl = 300) =>
        query.Type == DnsRecordType.A
            ? DnsMessage.Response(query, answers: [new TestRecord(query.Name, DnsRecordType.A, ttl, v4)])
            : DnsMessage.Response(query, answers: [new TestRecord(query.Name, DnsRecordType.Aaaa, ttl, v6)]);

    [Fact]
    public async Task Resolves_over_udp_ipv4_before_ipv6_and_caches()
    {
        using var server = new FakeDnsServer((q, _) => Answer(q));
        var resolver = new DnsServerResolver([server.EndPoint], Short);

        var addresses = await resolver.ResolveAsync("svc.test", default);
        Assert.Equal([IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")], addresses);
        Assert.Equal(2, server.UdpQueries);
        Assert.Equal(0, server.TcpQueries);

        // Cached by TTL: no second round trip, and the name is compared without case or trailing dot.
        Assert.Equal(addresses, await resolver.ResolveAsync("SVC.test.", default));
        Assert.Equal(2, server.UdpQueries);
    }

    [Fact]
    public async Task Truncated_udp_reply_is_retried_over_tcp()
    {
        using var server = new FakeDnsServer((q, overTcp) => overTcp
            ? Answer(q, v4: "192.0.2.77")
            : DnsMessage.Response(q, truncated: true));
        var resolver = new DnsServerResolver([server.EndPoint], Short);

        var addresses = await resolver.ResolveAsync("big.test", default);
        Assert.Contains(IPAddress.Parse("192.0.2.77"), addresses);
        Assert.Equal(2, server.TcpQueries);
    }

    [Fact]
    public async Task Silent_server_times_out_and_the_next_server_answers()
    {
        using var silent = new FakeDnsServer((_, _) => null);
        using var good = new FakeDnsServer((q, _) => Answer(q, v4: "192.0.2.55"));
        var resolver = new DnsServerResolver([silent.EndPoint, good.EndPoint], Short);

        var watch = Stopwatch.StartNew();
        var addresses = await resolver.ResolveAsync("slow.test", default);
        Assert.Equal(IPAddress.Parse("192.0.2.55"), addresses[0]);
        Assert.True(silent.UdpQueries >= 1);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData(DnsWire.ResponseCodeServerFailure)]
    [InlineData(DnsWire.ResponseCodeRefused)]
    public async Task Servfail_and_refused_move_on_to_the_next_server(int rcode)
    {
        using var failing = new FakeDnsServer((q, _) => DnsMessage.Response(q, rcode: rcode));
        using var good = new FakeDnsServer((q, _) => Answer(q));
        var resolver = new DnsServerResolver([failing.EndPoint, good.EndPoint], Short);

        Assert.Equal(IPAddress.Parse("192.0.2.10"), (await resolver.ResolveAsync("x.test", default))[0]);
        Assert.Equal(2, failing.UdpQueries);
        Assert.Equal(2, good.UdpQueries);
    }

    [Fact]
    public async Task Nxdomain_is_an_answer_and_stops_the_search()
    {
        using var first = new FakeDnsServer((q, _) => DnsMessage.Response(q, rcode: DnsWire.ResponseCodeNameError));
        using var second = new FakeDnsServer((q, _) => Answer(q));
        var resolver = new DnsServerResolver([first.EndPoint, second.EndPoint], Short);

        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await resolver.ResolveAsync("missing.test", default));
        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
        Assert.Equal("missing.test", error.Host);
        Assert.Equal(0, second.UdpQueries);
    }

    [Fact]
    public async Task All_servers_failing_is_try_again_and_is_not_cached()
    {
        var fail = true;
        using var server = new FakeDnsServer((q, _) => Volatile.Read(ref fail) ? DnsMessage.Response(q, rcode: DnsWire.ResponseCodeServerFailure) : Answer(q));
        var resolver = new DnsServerResolver([server.EndPoint], Short);

        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await resolver.ResolveAsync("flaky.test", default));
        Assert.Equal(SocketError.TryAgain, error.SocketErrorCode);
        Assert.Contains("response code 2", error.Message);

        Volatile.Write(ref fail, false);
        Assert.NotEmpty(await resolver.ResolveAsync("flaky.test", default));
    }

    [Fact]
    public async Task Stray_and_forged_datagrams_are_ignored()
    {
        using var server = new FakeDnsServer((q, _) => Answer(q, v4: "192.0.2.20"))
        {
            Noise = q =>
            [
                [1, 2, 3],
                // Wrong id (forged), and a reply to another name.
                DnsMessage.Response((ushort)(q.Id ^ 0x5A5A), q.Name, q.Type, answers: [new TestRecord(q.Name, DnsRecordType.A, 5, "203.0.113.66")]),
                DnsMessage.Response(q.Id, "evil.test", q.Type, answers: [new TestRecord("evil.test", DnsRecordType.A, 5, "203.0.113.67")]),
            ],
        };
        var resolver = new DnsServerResolver([server.EndPoint], TimeSpan.FromSeconds(2));

        var addresses = await resolver.ResolveAsync("real.test", default);
        Assert.Contains(IPAddress.Parse("192.0.2.20"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("203.0.113.66"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("203.0.113.67"), addresses);
    }

    [Fact]
    public async Task Tcp_reply_to_another_question_fails_over()
    {
        using var liar = new FakeDnsServer((q, overTcp) => overTcp
            ? DnsMessage.Response(q.Id, "other.test", q.Type)
            : DnsMessage.Response(q, truncated: true));
        using var good = new FakeDnsServer((q, _) => Answer(q, v4: "192.0.2.88"));
        var resolver = new DnsServerResolver([liar.EndPoint, good.EndPoint], Short);

        Assert.Equal(IPAddress.Parse("192.0.2.88"), (await resolver.ResolveAsync("t.test", default))[0]);
    }

    [Fact]
    public async Task One_family_failing_does_not_fail_the_lookup()
    {
        using var server = new FakeDnsServer((q, _) => q.Type == DnsRecordType.A
            ? Answer(q, v4: "192.0.2.31")
            : DnsMessage.Response(q, rcode: DnsWire.ResponseCodeServerFailure));
        var resolver = new DnsServerResolver([server.EndPoint], Short);

        Assert.Equal([IPAddress.Parse("192.0.2.31")], await resolver.ResolveAsync("v4only.test", default));
        // The partial answer is not cached: the next lookup asks again.
        await resolver.ResolveAsync("v4only.test", default);
        Assert.Equal(4, server.UdpQueries);
    }

    [Fact]
    public async Task Literals_and_localhost_never_reach_the_server()
    {
        using var server = new FakeDnsServer((q, _) => Answer(q));
        var resolver = new DnsServerResolver([server.EndPoint], Short);

        Assert.Equal([IPAddress.Parse("10.1.2.3")], await resolver.ResolveAsync("10.1.2.3", default));
        Assert.Equal([IPAddress.Parse("::1")], await resolver.ResolveAsync("[::1]", default));
        Assert.Contains(IPAddress.Loopback, await resolver.ResolveAsync("localhost", default));
        Assert.Contains(IPAddress.Loopback, await resolver.ResolveAsync("app.localhost", default));
        Assert.Equal(0, server.UdpQueries);
    }

    [Fact]
    public async Task Invalid_name_is_host_not_found_without_a_query()
    {
        using var server = new FakeDnsServer((q, _) => Answer(q));
        var resolver = new DnsServerResolver([server.EndPoint], Short);
        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await resolver.ResolveAsync("a..b", default));
        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
        Assert.Equal(0, server.UdpQueries);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var silent = new FakeDnsServer((_, _) => null);
        var resolver = new DnsServerResolver([silent.EndPoint], TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await resolver.ResolveAsync("hang.test", cts.Token));
    }

    [Fact]
    public void Needs_at_least_one_server() =>
        Assert.Throws<ArgumentException>(() => new DnsServerResolver([]));
}

public sealed class DohResolverTests
{
    private static readonly Uri Url = new("https://doh.test/dns-query");

    private sealed class DohHandler(Func<HttpRequestMessage, byte[], Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request, body));
            }

            return await respond(request, body).WaitAsync(cancellationToken);
        }
    }

    private static HttpResponseMessage Message(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task Posts_wire_queries_and_decodes_the_answers()
    {
        var handler = new DohHandler((_, body) =>
        {
            var q = DnsMessage.ParseQuery(body);
            return Task.FromResult(Message(q.Type == DnsRecordType.A
                ? DnsMessage.Response(q, answers:
                [
                    new TestRecord(q.Name, DnsRecordType.CName, 60, "edge.test"),
                    new TestRecord("edge.test", DnsRecordType.A, 60, "192.0.2.44"),
                ])
                : DnsMessage.Response(q)));
        });
        using var http = new HttpClient(handler);
        var resolver = new DohResolver(Url, http);

        Assert.Equal([IPAddress.Parse("192.0.2.44")], await resolver.ResolveAsync("www.test", default));
        Assert.Equal(2, handler.Requests.Count);
        foreach (var (request, body) in handler.Requests)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Url, request.RequestUri);
            Assert.Equal("application/dns-message", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/dns-message");
            Assert.Equal(0, DnsMessage.ParseQuery(body).Id);
        }

        // Cached.
        await resolver.ResolveAsync("www.test", default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Http_error_is_try_again()
    {
        using var http = new HttpClient(new DohHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, http).ResolveAsync("x.test", default));
        Assert.Equal(SocketError.TryAgain, error.SocketErrorCode);
        Assert.Contains("502", error.Message);
    }

    [Fact]
    public async Task Reply_to_another_question_or_garbage_is_refused()
    {
        using var other = new HttpClient(new DohHandler((_, body) =>
        {
            var q = DnsMessage.ParseQuery(body);
            return Task.FromResult(Message(DnsMessage.Response(0, "evil.test", q.Type, answers: [new TestRecord("evil.test", DnsRecordType.A, 5, "203.0.113.1")])));
        }));
        await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, other).ResolveAsync("x.test", default));

        using var garbage = new HttpClient(new DohHandler((_, _) => Task.FromResult(Message([1, 2, 3]))));
        await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, garbage).ResolveAsync("x.test", default));
    }

    [Fact]
    public async Task Oversized_reply_is_refused()
    {
        using var http = new HttpClient(new DohHandler((_, _) => Task.FromResult(Message(new byte[70_000]))));
        await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, http).ResolveAsync("x.test", default));
    }

    [Fact]
    public async Task Transport_failure_and_timeout_are_try_again()
    {
        using var broken = new HttpClient(new DohHandler((_, _) => throw new HttpRequestException("connection refused")));
        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, broken).ResolveAsync("x.test", default));
        Assert.Contains("connection refused", error.Message);

        using var release = new CancellationTokenSource();
        using var hanging = new HttpClient(new DohHandler(async (_, _) =>
        {
            await Task.Delay(Timeout.Infinite, release.Token);
            return null!;
        }));
        var watch = Stopwatch.StartNew();
        error = await Assert.ThrowsAsync<DnsResolutionException>(async () =>
            await new DohResolver(Url, hanging, TimeSpan.FromMilliseconds(200)).ResolveAsync("x.test", default));
        Assert.Equal(SocketError.TryAgain, error.SocketErrorCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        release.Cancel();
    }

    [Fact]
    public async Task Nxdomain_is_host_not_found()
    {
        using var http = new HttpClient(new DohHandler((_, body) =>
            Task.FromResult(Message(DnsMessage.Response(DnsMessage.ParseQuery(body), rcode: DnsWire.ResponseCodeNameError)))));
        var error = await Assert.ThrowsAsync<DnsResolutionException>(async () => await new DohResolver(Url, http).ResolveAsync("gone.test", default));
        Assert.Equal(SocketError.HostNotFound, error.SocketErrorCode);
    }

    [Fact]
    public void Only_https_urls_are_accepted()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new DohResolver(new Uri("http://doh.test/dns-query"), http));
    }
}

public sealed class DnsCacheTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Entries_expire_with_their_ttl()
    {
        var time = new ManualTime();
        var cache = new DnsCache(time);
        cache.Set("a.test", [IPAddress.Loopback], 30);

        Assert.True(cache.TryGet("A.TEST", out var hit));
        Assert.Equal([IPAddress.Loopback], hit);

        time.Now = time.Now.AddSeconds(31);
        Assert.False(cache.TryGet("a.test", out _));
    }

    [Fact]
    public void Zero_ttl_is_not_cached_and_huge_ttl_is_capped()
    {
        var time = new ManualTime();
        var cache = new DnsCache(time, maxTtl: TimeSpan.FromMinutes(1));
        cache.Set("zero.test", [IPAddress.Loopback], 0);
        Assert.False(cache.TryGet("zero.test", out _));

        cache.Set("long.test", [IPAddress.Loopback], uint.MaxValue);
        time.Now = time.Now.AddMinutes(2);
        Assert.False(cache.TryGet("long.test", out _));
    }

    [Fact]
    public void Capacity_is_bounded()
    {
        var time = new ManualTime();
        var cache = new DnsCache(time, capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            cache.Set($"h{i}.test", [IPAddress.Loopback], (uint)(100 + i));
        }

        Assert.True(cache.Count <= 3);
        // The longest-lived entry survives the evictions.
        Assert.True(cache.TryGet("h9.test", out _));
    }
}
