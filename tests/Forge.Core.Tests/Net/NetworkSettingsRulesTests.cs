using System.Net;
using Forge.Abstractions;
using Forge.Core.Net;
using Xunit;

namespace Forge.Core.Tests.Net;

public sealed class NetworkSettingsRulesTests
{
    [Fact]
    public void SplitList_accepts_lines_commas_semicolons_and_spaces_and_dedups()
    {
        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "[::1]:53"], NetworkSettingsRules.SplitList(" 1.1.1.1\r\n8.8.8.8, 9.9.9.9;[::1]:53 \t1.1.1.1"));
        Assert.Empty(NetworkSettingsRules.SplitList(null));
        Assert.Empty(NetworkSettingsRules.SplitList(" \n , "));
        Assert.Equal(["A.test"], NetworkSettingsRules.SplitList("A.test a.TEST"));
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1", 53)]
    [InlineData(" 1.1.1.1:5353 ", "1.1.1.1", 5353)]
    [InlineData("2606:4700::1111", "2606:4700::1111", 53)]
    [InlineData("[2606:4700::1111]:853", "2606:4700::1111", 853)]
    [InlineData("[::1]", "::1", 53)]
    public void Valid_servers_parse(string text, string address, int port)
    {
        Assert.True(NetworkSettingsRules.TryParseServer(text, out var endpoint));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), port), endpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("dns.google")]
    [InlineData("1")]
    [InlineData("1.1.1")]
    [InlineData("1.1.1.1:0")]
    [InlineData("1.1.1.1:65536")]
    [InlineData("1.1.1.1:x")]
    [InlineData("1.1.1.1:-5")]
    [InlineData("[1.1.1.1]:53")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("256.1.1.1")]
    public void Invalid_servers_are_refused(string? text) =>
        Assert.False(NetworkSettingsRules.TryParseServer(text, out _));

    [Theory]
    [InlineData("https://1.1.1.1/dns-query", true)]
    [InlineData("https://dns.example/dns-query", true)]
    [InlineData("http://1.1.1.1/dns-query", false)]
    [InlineData("https://user:pw@dns.example/dns-query", false)]
    [InlineData("dns.example/dns-query", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Doh_urls(string? text, bool valid) =>
        Assert.Equal(valid, NetworkSettingsRules.IsValidDohUrl(text));

    [Theory]
    [InlineData("http://proxy.corp:8080", "http://proxy.corp:8080/")]
    [InlineData("http://proxy.corp", "http://proxy.corp/")]
    [InlineData("https://proxy.corp", "https://proxy.corp/")]
    [InlineData("socks5://127.0.0.1", "socks5://127.0.0.1:1080/")]
    [InlineData("socks4a://p:9050/", "socks4a://p:9050/")]
    [InlineData("SOCKS5://P.Example:1081", "socks5://p.example:1081/")]
    public void Proxy_urls_parse_with_default_ports(string text, string expected)
    {
        Assert.True(NetworkSettingsRules.TryParseProxyUrl(text, out var proxy, out var credentials));
        Assert.Equal(new Uri(expected), proxy);
        Assert.Null(credentials);
    }

    [Fact]
    public void Proxy_credentials_come_from_the_user_info_and_leave_the_url()
    {
        Assert.True(NetworkSettingsRules.TryParseProxyUrl("http://al%40ice:p%3Ass@proxy.corp:3128", out var proxy, out var credentials));
        Assert.Equal(new Uri("http://proxy.corp:3128/"), proxy);
        Assert.Equal("al@ice", credentials!.UserName);
        Assert.Equal("p:ss", credentials.Password);
        Assert.DoesNotContain("@", proxy.ToString());
    }

    [Theory]
    [InlineData("ftp://proxy.corp")]
    [InlineData("proxy.corp:8080")]
    [InlineData("http://proxy.corp/path")]
    [InlineData("http://proxy.corp/?q=1")]
    [InlineData("http://proxy.corp/#x")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_proxy_urls_are_refused(string? text) =>
        Assert.False(NetworkSettingsRules.TryParseProxyUrl(text, out _, out _));

    [Theory]
    [InlineData("*", true)]
    [InlineData("*.corp.example", true)]
    [InlineData(".corp.example", true)]
    [InlineData("intranet", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("*.", false)]
    [InlineData(".", false)]
    [InlineData("a*b", false)]
    [InlineData("http://x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Bypass_patterns(string? pattern, bool valid) =>
        Assert.Equal(valid, NetworkSettingsRules.IsValidBypassPattern(pattern));

    [Theory]
    [InlineData("any.host", "*", true)]
    [InlineData("corp.example", "*.corp.example", true)]
    [InlineData("a.b.corp.example", "*.corp.example", true)]
    [InlineData("A.CORP.example.", ".corp.example", true)]
    [InlineData("evilcorp.example", "*.corp.example", false)]
    [InlineData("corp.example.evil", "*.corp.example", false)]
    [InlineData("intranet", "intranet", true)]
    [InlineData("intranet2", "intranet", false)]
    [InlineData("[fe80::1]", "[fe80::1]", true)]
    [InlineData("fe80::1", "[FE80::1]", true)]
    public void Bypass_matching(string host, string pattern, bool matches) =>
        Assert.Equal(matches, NetworkSettingsRules.MatchesBypass(host, pattern));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST.", true)]
    [InlineData("app.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.8.9.10", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost.example", false)]
    [InlineData("10.0.0.1", false)]
    public void Loopback_hosts(string host, bool loopback) =>
        Assert.Equal(loopback, NetworkSettingsRules.IsLoopbackHost(host));

    [Fact]
    public void Problems_name_each_invalid_field_and_mode_requirements()
    {
        Assert.Empty(NetworkSettingsRules.Problems(NetworkSettings.Default));
        Assert.Empty(NetworkSettingsRules.Problems(new NetworkSettings(DnsMode.Servers, ["1.1.1.1"], null, ProxyMode.Manual, "socks5://p:1", ["*.corp"])));

        Assert.Equal(["dnsServers"], NetworkSettingsRules.Problems(NetworkSettings.Default with { Dns = DnsMode.Servers }));
        Assert.Equal(["dohUrl"], NetworkSettingsRules.Problems(NetworkSettings.Default with { Dns = DnsMode.DnsOverHttps }));
        Assert.Equal(["proxyUrl"], NetworkSettingsRules.Problems(NetworkSettings.Default with { Proxy = ProxyMode.Manual }));

        // Invalid stored values are problems even when their mode is not selected (they would be written to the file).
        Assert.Equal(
            ["dnsServers", "dohUrl", "proxyUrl", "proxyBypass"],
            NetworkSettingsRules.Problems(new NetworkSettings(DnsMode.System, ["dns.google"], "http://x/dns", ProxyMode.System, "ftp://p", ["a b*"])));
    }
}
