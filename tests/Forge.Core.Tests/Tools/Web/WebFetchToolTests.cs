using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Tests.Providers;
using Forge.Core.Tools.Web;
using Xunit;

namespace Forge.Core.Tests.Tools.Web;

public sealed class WebFetchToolTests
{
    private static readonly ToolContext Context = new("s", ".");

    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> PublicDns =
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static Task<ToolResult> Run(WebFetchTool tool, string url, int? maxChars = null, CancellationToken ct = default)
    {
        var args = maxChars is null ? "{\"url\":\"" + url + "\"}" : "{\"url\":\"" + url + "\",\"maxChars\":" + maxChars + "}";
        return tool.InvokeAsync(new ToolCall("1", WebFetchTool.Name, args), Context, ct).AsTask();
    }

    private static HttpResponseMessage Html(string body, string contentType = "text/html") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static WebFetchTool Create(FakeHttpMessageHandler handler, WebFetchOptions? options = null, Func<string, CancellationToken, Task<IPAddress[]>>? dns = null) =>
        new(handler.CreateClient(), dns ?? PublicDns, options);

    [Fact]
    public async Task Html_page_is_converted_with_url_and_title_header()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html("<title>T</title><h1>Head</h1><p>x</p>"));
        var result = await Run(Create(handler), "https://example.com/a");
        Assert.False(result.IsError);
        Assert.StartsWith("URL: https://example.com/a\nTitle: T\n\n# Head\n\nx", result.Content);
    }

    [Fact]
    public async Task Json_and_text_are_returned_as_is()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Html("{\"a\": <1>}", "application/json"))
            .Enqueue(() => Html("<b>raw</b>", "text/plain"));
        var tool = Create(handler);
        Assert.Contains("{\"a\": <1>}", (await Run(tool, "https://example.com/j")).Content);
        Assert.Contains("<b>raw</b>", (await Run(tool, "https://example.com/t")).Content);
    }

    [Fact]
    public async Task Binary_content_is_refused()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await Run(Create(handler), "https://example.com/i.png");
        Assert.True(result.IsError);
        Assert.Contains("binary content not supported", result.Content);
    }

    [Fact]
    public async Task Missing_content_type_with_nul_bytes_is_binary()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([65, 0, 66]) });
        var result = await Run(Create(handler), "https://example.com/x");
        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Non_http_schemes_and_garbage_are_refused_without_a_request(string url)
    {
        var handler = new FakeHttpMessageHandler();
        var result = await Run(Create(handler), url);
        Assert.True(result.IsError);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Charset_from_content_type_is_honored()
    {
        var bytes = Encoding.GetEncoding("iso-8859-1").GetBytes("café");
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new("text/plain") { CharSet = "iso-8859-1" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await Run(Create(handler), "https://example.com/c");
        Assert.Contains("café", result.Content);
    }

    [Fact]
    public async Task Unknown_charset_falls_back_to_utf8()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hé"));
            content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=no-such-charset");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await Run(Create(handler), "https://example.com/c");
        Assert.Contains("hé", result.Content);
    }

    [Fact]
    public async Task Redirects_are_followed_and_final_url_reported()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Redirect("/b"))
            .Enqueue(() => Html("<p>done</p>"));
        var result = await Run(Create(handler), "https://example.com/a");
        Assert.StartsWith("URL: https://example.com/b", result.Content);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Redirect_to_loopback_is_refused_on_the_second_hop()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(() => Redirect("http://127.0.0.1/admin"))
            .Enqueue(() => Html("secret"));
        var result = await Run(Create(handler), "https://example.com/a");
        Assert.True(result.IsError);
        Assert.Contains("refused", result.Content);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Redirect_to_non_http_scheme_is_refused()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Redirect("file:///etc/passwd"));
        var result = await Run(Create(handler), "https://example.com/a");
        Assert.True(result.IsError);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Redirect_to_host_resolving_privately_is_refused()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Redirect("http://internal.example/x"));
        var tool = Create(handler, dns: (host, _) => Task.FromResult(new[] { host == "internal.example" ? IPAddress.Parse("10.0.0.5") : IPAddress.Parse("93.184.216.34") }));
        var result = await Run(tool, "https://example.com/a");
        Assert.True(result.IsError);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task More_than_five_redirects_fail()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 7; i++)
        {
            handler.Enqueue(() => Redirect("/next"));
        }

        var result = await Run(Create(handler), "https://example.com/a");
        Assert.True(result.IsError);
        Assert.Contains("redirects", result.Content);
        Assert.Equal(6, handler.Calls);
    }

    [Fact]
    public async Task Five_redirects_are_allowed()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 5; i++)
        {
            handler.Enqueue(() => Redirect("/next"));
        }

        handler.Enqueue(() => Html("<p>ok</p>"));
        Assert.False((await Run(Create(handler), "https://example.com/a")).IsError);
    }

    [Theory]
    [InlineData("http://127.0.0.1/", true)]
    [InlineData("http://[::1]/", true)]
    [InlineData("http://10.1.2.3/", true)]
    [InlineData("http://172.16.0.1/", true)]
    [InlineData("http://172.32.0.1/", false)]
    [InlineData("http://192.168.1.1/", true)]
    [InlineData("http://169.254.169.254/", true)]
    [InlineData("http://0.0.0.0/", true)]
    [InlineData("http://[fc00::1]/", true)]
    [InlineData("http://[fe80::1]/", true)]
    [InlineData("http://[::ffff:127.0.0.1]/", true)]
    [InlineData("http://93.184.216.34/", false)]
    public async Task Ip_literals_follow_the_private_range_rules(string url, bool refused)
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html("<p>ok</p>"));
        var result = await Run(Create(handler), url);
        Assert.Equal(refused, result.IsError);
        Assert.Equal(refused ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task Hostname_resolving_to_loopback_is_refused()
    {
        var handler = new FakeHttpMessageHandler();
        var tool = Create(handler, dns: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        Assert.True((await Run(tool, "http://localtest.example/")).IsError);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AllowPrivateNetwork_lifts_the_guard()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html("<p>ok</p>"));
        var tool = Create(handler, new WebFetchOptions { AllowPrivateNetwork = () => true });
        Assert.False((await Run(tool, "http://127.0.0.1/")).IsError);
    }

    [Fact]
    public async Task Direct_connections_skip_the_pre_check_and_proxied_ones_keep_it()
    {
        var asked = 0;
        Func<string, CancellationToken, Task<IPAddress[]>> privateDns = (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
        };

        // Direct: the connect-time filter is authoritative, so the resolve-only pre-check is not run.
        var direct = new FakeHttpMessageHandler().Enqueue(() => Html("<p>ok</p>"));
        var tool = Create(direct, new WebFetchOptions { ConnectsDirectly = _ => true }, privateDns);
        Assert.False((await Run(tool, "http://intranet.example/")).IsError);
        Assert.Equal(0, asked);

        // Through a proxy: the proxy resolves the target, so the pre-check is the only guard.
        var proxied = new FakeHttpMessageHandler();
        tool = Create(proxied, new WebFetchOptions { ConnectsDirectly = _ => false }, privateDns);
        var result = await Run(tool, "http://intranet.example/");
        Assert.True(result.IsError);
        Assert.Contains("10.0.0.1", result.Content);
        Assert.Equal(0, proxied.Calls);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Connect_time_refusal_is_reported_as_the_private_network_refusal()
    {
        var handler = new ExceptionHandler(new HttpRequestException(
            "wrapped",
            new Forge.Core.Net.NetworkAddressRefusedException("rebind.example", IPAddress.Parse("192.168.1.10"))));
        var tool = new WebFetchTool(new HttpClient(handler), PublicDns, new WebFetchOptions { ConnectsDirectly = _ => true });

        var result = await Run(tool, "http://rebind.example/");
        Assert.True(result.IsError);
        Assert.Contains("refused: host 'rebind.example'", result.Content);
        Assert.Contains("192.168.1.10", result.Content);
        Assert.Contains("allow private network", result.Content);
    }

    [Fact]
    public async Task Dns_rebinding_is_stopped_at_connect_time_by_a_network_stack_client()
    {
        // The pre-check resolver sees a public address; the connection itself resolves to loopback (the rebinding case).
        var connectResolver = new Forge.Core.Tests.Net.TableResolver(new() { ["rebind.example"] = [IPAddress.Loopback] });
        using var stack = new Forge.Core.Net.NetworkStack(
            new NetworkSettings(DnsMode.System, [], null, ProxyMode.None, null, []),
            _ => connectResolver);
        using var http = new HttpClient(stack.CreateHandler(new NetworkHandlerOptions
        {
            AllowAutoRedirect = false,
            AddressFilter = a => !WebFetchTool.IsPrivate(a),
        }));
        var tool = new WebFetchTool(http, PublicDns, new WebFetchOptions { ConnectsDirectly = stack.ConnectsDirectly });

        var result = await Run(tool, "http://rebind.example:9/");
        Assert.True(result.IsError);
        Assert.Contains("refused: host 'rebind.example'", result.Content);
        Assert.Contains("127.0.0.1", result.Content);
    }

    private sealed class ExceptionHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    [Fact]
    public async Task Declared_oversize_body_is_refused()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html(new string('a', 200), "text/plain"));
        var result = await Run(Create(handler, new WebFetchOptions { MaxBodyBytes = 100 }), "https://example.com/big");
        Assert.True(result.IsError);
        Assert.Contains("larger", result.Content);
    }

    [Fact]
    public async Task Streamed_oversize_body_is_cut_off_without_content_length()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() =>
        {
            var content = new StreamContent(new NoLengthStream(new byte[300]));
            content.Headers.ContentType = new("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await Run(Create(handler, new WebFetchOptions { MaxBodyBytes = 100 }), "https://example.com/big");
        Assert.True(result.IsError);
        Assert.Contains("larger", result.Content);
    }

    [Fact]
    public async Task Body_exactly_at_the_cap_is_accepted()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html(new string('a', 100), "text/plain"));
        Assert.False((await Run(Create(handler, new WebFetchOptions { MaxBodyBytes = 100 }), "https://example.com/x")).IsError);
    }

    [Fact]
    public async Task Timeout_is_reported_as_an_error_result()
    {
        var tool = new WebFetchTool(new HttpClient(new HangingHandler()), PublicDns, new WebFetchOptions { Timeout = TimeSpan.FromMilliseconds(50) });
        var result = await Run(tool, "https://example.com/slow");
        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var tool = new WebFetchTool(new HttpClient(new HangingHandler()), PublicDns);
        var pending = Run(tool, "https://example.com/slow", ct: cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Http_error_status_is_an_error_result()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.NotFound, "nope");
        var result = await Run(Create(handler), "https://example.com/missing");
        Assert.True(result.IsError);
        Assert.Contains("404", result.Content);
    }

    [Fact]
    public async Task Network_failure_is_an_error_result_not_an_exception()
    {
        var tool = new WebFetchTool(new HttpClient(new ThrowingHandler()), PublicDns);
        var result = await Run(tool, "https://example.com/x");
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task MaxChars_truncates_with_a_marker()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(() => Html(new string('a', 500), "text/plain"));
        var result = await Run(Create(handler), "https://example.com/x", maxChars: 50);
        Assert.Contains("[truncated", result.Content);
        Assert.True(result.Content.Length < 200);
    }

    [Fact]
    public async Task Invalid_arguments_are_an_error_result()
    {
        var tool = Create(new FakeHttpMessageHandler());
        var result = await tool.InvokeAsync(new ToolCall("1", WebFetchTool.Name, "{nope"), Context, default);
        Assert.True(result.IsError);
    }

    [Fact]
    public void Tool_is_read_only()
    {
        Assert.Equal(ToolSideEffect.ReadOnly, Create(new FakeHttpMessageHandler()).SideEffect);
    }

    private sealed class NoLengthStream(byte[] data) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, Math.Min(64, data.Length - position));
            Array.Copy(data, position, buffer, offset, n);
            position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
