using System.Net;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Network;
using Forge.Core.Net;
using Xunit;

namespace Forge.App.Tests.Network;

public sealed class NetworkSettingsStoreTests : IDisposable
{
    private readonly string dir = TestTempFiles.NewDirectory("forge-net-");


    private string SettingsPath => Path.Combine(dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_file_and_missing_section_are_defaults_without_a_problem()
    {
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.Equal(NetworkSettings.Default.Dns, store.Current.Dns);
        Assert.Equal(NetworkSettings.Default.Proxy, store.Current.Proxy);
        Assert.Null(store.LoadProblem);

        File.WriteAllText(SettingsPath, """{"web":{}}""");
        store.Reload();
        Assert.Null(store.LoadProblem);
    }

    [Fact]
    public void Round_trip_keeps_other_sections_and_unknown_fields()
    {
        File.WriteAllText(SettingsPath, """{"preferences":{"language":"en"},"network":{"future":"keep"}}""");
        var store = new NetworkSettingsStore(SettingsPath);
        var value = new NetworkSettings(
            DnsMode.Servers,
            [" 1.1.1.1 ", "[2606:4700::1111]:53", "1.1.1.1"],
            "https://dns.example/dns-query",
            ProxyMode.Manual,
            " socks5://u:p@127.0.0.1:1080 ",
            ["*.corp.example", ""]);
        store.Save(value);

        Assert.Equal(["1.1.1.1", "[2606:4700::1111]:53"], store.Current.DnsServers);
        var reloaded = new NetworkSettingsStore(SettingsPath);
        Assert.Null(reloaded.LoadProblem);
        Assert.Equal(DnsMode.Servers, reloaded.Current.Dns);
        Assert.Equal(["1.1.1.1", "[2606:4700::1111]:53"], reloaded.Current.DnsServers);
        Assert.Equal("https://dns.example/dns-query", reloaded.Current.DohUrl);
        Assert.Equal(ProxyMode.Manual, reloaded.Current.Proxy);
        Assert.Equal("socks5://u:p@127.0.0.1:1080", reloaded.Current.ProxyUrl);
        Assert.Equal(["*.corp.example"], reloaded.Current.ProxyBypass);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("en", (string?)root["preferences"]!["language"]);
        Assert.Equal("keep", (string?)root["network"]!["future"]);
        Assert.Equal("servers", (string?)root["network"]!["dns"]);
        Assert.Equal("manual", (string?)root["network"]!["proxy"]);
    }

    [Theory]
    [InlineData(DnsMode.System, "system")]
    [InlineData(DnsMode.Servers, "servers")]
    [InlineData(DnsMode.DnsOverHttps, "doh")]
    public void Dns_tokens(DnsMode mode, string token)
    {
        Assert.Equal(token, NetworkSettingsStore.DnsToken(mode));
        File.WriteAllText(SettingsPath, $$$"""{"network":{"dns":"{{{token.ToUpperInvariant()}}}","dnsServers":["9.9.9.9"],"dohUrl":"https://9.9.9.9/dns-query"}}""");
        Assert.Equal(mode, new NetworkSettingsStore(SettingsPath).Current.Dns);
    }

    [Fact]
    public void Removing_a_url_removes_the_field()
    {
        var store = new NetworkSettingsStore(SettingsPath);
        store.Save(NetworkSettings.Default with { ProxyUrl = "http://p:1", DohUrl = "https://d/q" });
        store.Save(NetworkSettings.Default);
        var network = JsonNode.Parse(File.ReadAllText(SettingsPath))!["network"]!.AsObject();
        Assert.False(network.ContainsKey("proxyUrl"));
        Assert.False(network.ContainsKey("dohUrl"));
    }

    [Fact]
    public void Invalid_values_fall_back_per_field_and_are_reported()
    {
        File.WriteAllText(SettingsPath, """
            {"network":{
              "dns":"servers",
              "dnsServers":["dns.google", 5, "8.8.8.8"],
              "dohUrl":"http://insecure/dns",
              "proxy":"pac",
              "proxyUrl":"ftp://p",
              "proxyBypass":["ok.example", "bad pattern*"]
            }}
            """);
        var store = new NetworkSettingsStore(SettingsPath);

        Assert.Equal(DnsMode.Servers, store.Current.Dns);
        Assert.Equal(["8.8.8.8"], store.Current.DnsServers);
        Assert.Null(store.Current.DohUrl);
        Assert.Equal(ProxyMode.System, store.Current.Proxy);
        Assert.Null(store.Current.ProxyUrl);
        Assert.Equal(["ok.example"], store.Current.ProxyBypass);
        var problem = store.LoadProblem!;
        foreach (var fragment in new[] { "dns.google", "5", "dohUrl", "proxy: \"pac\"", "proxyUrl", "bad pattern*" })
        {
            Assert.Contains(fragment, problem);
        }

        // The repaired settings are what the stack is built from: they pass the rules.
        Assert.Empty(NetworkSettingsRules.Problems(store.Current));
    }

    [Fact]
    public void A_mode_without_its_value_falls_back_to_system()
    {
        File.WriteAllText(SettingsPath, """{"network":{"dns":"doh","proxy":"manual","dnsServers":"1.1.1.1"}}""");
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.Equal(DnsMode.System, store.Current.Dns);
        Assert.Equal(ProxyMode.System, store.Current.Proxy);
        Assert.Contains("doh", store.LoadProblem);
        Assert.Contains("manual", store.LoadProblem);
        Assert.Contains("dnsServers", store.LoadProblem);

        File.WriteAllText(SettingsPath, """{"network":{"dns":"servers","dnsServers":[]}}""");
        store.Reload();
        Assert.Equal(DnsMode.System, store.Current.Dns);
        Assert.NotNull(store.LoadProblem);
    }

    [Fact]
    public void A_section_that_is_not_an_object_is_reported()
    {
        File.WriteAllText(SettingsPath, """{"network":"fast"}""");
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.Equal(DnsMode.System, store.Current.Dns);
        Assert.Contains("network", store.LoadProblem);
    }

    [Fact]
    public void Corrupt_file_is_silent_defaults_and_save_refuses_to_overwrite_it()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.Equal(DnsMode.System, store.Current.Dns);
        Assert.Null(store.LoadProblem);
        Assert.Throws<InvalidOperationException>(() => store.Save(NetworkSettings.Default));
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Save_refuses_invalid_settings_and_writes_nothing()
    {
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.Throws<ArgumentException>(() => store.Save(NetworkSettings.Default with { Dns = DnsMode.Servers }));
        Assert.Throws<ArgumentException>(() => store.Save(NetworkSettings.Default with { ProxyUrl = "nope" }));
        Assert.Throws<ArgumentException>(() => store.Save(NetworkSettings.Default with { ProxyBypass = ["a b"] }));
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(DnsMode.System, store.Current.Dns);
    }

    [Fact]
    public void Successful_save_clears_the_load_problem()
    {
        File.WriteAllText(SettingsPath, """{"network":{"dns":"bogus"}}""");
        var store = new NetworkSettingsStore(SettingsPath);
        Assert.NotNull(store.LoadProblem);
        store.Save(NetworkSettings.Default);
        Assert.Null(store.LoadProblem);
        Assert.Null(new NetworkSettingsStore(SettingsPath).LoadProblem);
    }
}

[Collection("forge-app-ui")]
public sealed class NetworkSettingsSectionViewModelTests : IDisposable
{
    private readonly string dir = TestTempFiles.NewDirectory("forge-netvm-");
    private readonly List<(NetworkSettings Settings, string Host)> probes = [];


    private string SettingsPath => Path.Combine(dir, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<IPAddress[]> Probe(NetworkSettings settings, string host, CancellationToken ct)
    {
        probes.Add((settings, host));
        return host == "fail.test"
            ? Task.FromException<IPAddress[]>(new DnsResolutionException(host, System.Net.Sockets.SocketError.HostNotFound, "no such host"))
            : Task.FromResult(new[] { IPAddress.Parse("192.0.2.5"), IPAddress.Parse("2001:db8::5") });
    }

    private (NetworkSettingsSectionViewModel Vm, NetworkSettingsStore Store, NetworkStack Stack) Create()
    {
        var store = new NetworkSettingsStore(SettingsPath);
        var stack = new NetworkStack(store.Current);
        return (new NetworkSettingsSectionViewModel(store, stack, Probe), store, stack);
    }

    [Fact]
    public void Loads_the_stored_settings_into_the_fields()
    {
        new NetworkSettingsStore(SettingsPath).Save(new NetworkSettings(DnsMode.Servers, ["1.1.1.1", "8.8.8.8"], null, ProxyMode.Manual, "http://p:8080", ["*.corp"]));
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            Assert.True(vm.IsDnsServers);
            Assert.False(vm.IsDnsSystem);
            Assert.True(vm.IsProxyManual);
            Assert.Equal(["1.1.1.1", "8.8.8.8"], NetworkSettingsRules.SplitList(vm.ServersText));
            Assert.Equal("http://p:8080", vm.ProxyUrl);
            Assert.Equal("*.corp", vm.BypassText);
            Assert.True(vm.IsValid);
            Assert.False(vm.HasLoadProblem);
        }
    }

    [Fact]
    public void Only_the_selected_modes_are_validated()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            vm.ServersText = "dns.google";
            vm.DohUrl = "http://insecure";
            vm.ProxyUrl = "nope";
            vm.BypassText = "a*b";
            Assert.True(vm.IsValid);
            Assert.False(vm.HasServersError || vm.HasDohError || vm.HasProxyError || vm.HasBypassError);

            vm.IsDnsServers = true;
            Assert.True(vm.HasServersError);
            Assert.Contains("dns.google", vm.ServersError);
            Assert.False(vm.IsValid);

            vm.ServersText = "";
            Assert.True(vm.HasServersError);

            vm.ServersText = "1.1.1.1, [::1]:5353";
            Assert.False(vm.HasServersError);

            vm.IsDnsDoh = true;
            Assert.False(vm.HasServersError);
            Assert.True(vm.HasDohError);
            vm.DohUrl = "https://1.1.1.1/dns-query";
            Assert.False(vm.HasDohError);

            vm.IsProxyManual = true;
            Assert.True(vm.HasProxyError);
            Assert.True(vm.HasBypassError);
            Assert.Contains("a*b", vm.BypassError);
            vm.ProxyUrl = "socks5://127.0.0.1:1080";
            vm.BypassText = "*.corp\nintranet";
            Assert.True(vm.IsValid);
        }
    }

    [Fact]
    public void Radio_setters_ignore_false_and_notify_the_whole_group()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            var changed = PropertyChangeLog.For(vm);

            vm.IsDnsServers = false;
            Assert.True(vm.IsDnsSystem);
            Assert.Empty(changed);

            vm.IsProxyNone = true;
            Assert.Contains(nameof(vm.IsProxySystem), changed);
            Assert.Contains(nameof(vm.IsProxyNone), changed);
            Assert.Contains(nameof(vm.IsProxyManual), changed);
            Assert.False(vm.IsProxySystem);
        }
    }

    [Fact]
    public async Task Apply_saves_and_applies_to_the_stack()
    {
        var (vm, store, stack) = Create();
        using (vm)
        using (stack)
        {
            var applied = 0;
            stack.SettingsChanged += (_, _) => applied++;
            vm.IsDnsServers = true;
            vm.ServersText = "9.9.9.9\n149.112.112.112";
            vm.IsProxyManual = true;
            vm.ProxyUrl = "http://proxy.example:3128";
            vm.BypassText = "*.corp";

            await vm.ApplyAsync();

            Assert.Equal(1, applied);
            Assert.Equal(DnsMode.Servers, stack.Settings.Dns);
            Assert.Equal(["9.9.9.9", "149.112.112.112"], stack.Settings.DnsServers);
            Assert.Equal(ProxyMode.Manual, stack.Settings.Proxy);
            Assert.Equal(new Uri("http://proxy.example:3128/"), stack.Proxy.GetProxy(new Uri("https://api.example.com/")));
            var reloaded = new NetworkSettingsStore(SettingsPath).Current;
            Assert.Equal(DnsMode.Servers, reloaded.Dns);
            Assert.Equal(["*.corp"], reloaded.ProxyBypass);
            Assert.Equal(store.Current.ProxyUrl, stack.Settings.ProxyUrl);
            Assert.NotEmpty(vm.StatusText);
        }
    }

    [Fact]
    public async Task Apply_with_invalid_fields_changes_nothing()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            var applied = 0;
            stack.SettingsChanged += (_, _) => applied++;
            vm.IsProxyManual = true;
            vm.ProxyUrl = "not a proxy";

            await vm.ApplyAsync();

            Assert.Equal(0, applied);
            Assert.Equal(ProxyMode.System, stack.Settings.Proxy);
            Assert.False(File.Exists(SettingsPath));
            Assert.NotEmpty(vm.StatusText);
        }
    }

    [Fact]
    public async Task Invalid_text_in_an_unselected_mode_keeps_the_stored_value()
    {
        new NetworkSettingsStore(SettingsPath).Save(NetworkSettings.Default with { DnsServers = ["1.1.1.1"], ProxyUrl = "http://p:1" });
        var (vm, store, stack) = Create();
        using (vm)
        using (stack)
        {
            vm.ServersText = "garbage";
            vm.ProxyUrl = "also garbage";
            await vm.ApplyAsync();

            Assert.Equal(["1.1.1.1"], store.Current.DnsServers);
            Assert.Equal("http://p:1", store.Current.ProxyUrl);
            Assert.Equal(DnsMode.System, store.Current.Dns);
        }
    }

    [Fact]
    public async Task Save_failure_is_reported_and_the_stack_is_left_alone()
    {
        File.WriteAllText(SettingsPath, "[1, 2]");
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            var applied = 0;
            stack.SettingsChanged += (_, _) => applied++;
            vm.IsProxyNone = true;
            await vm.ApplyAsync();

            Assert.Equal(0, applied);
            Assert.Equal(ProxyMode.System, stack.Settings.Proxy);
            Assert.NotEmpty(vm.StatusText);
            Assert.Equal("[1, 2]", File.ReadAllText(SettingsPath));
        }
    }

    [Fact]
    public async Task Test_resolves_with_the_entered_settings_without_applying_them()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            vm.IsDnsDoh = true;
            vm.DohUrl = "https://1.1.1.1/dns-query";
            vm.TestHost = " example.org ";

            await vm.TestAsync(default);

            var (settings, host) = Assert.Single(probes);
            Assert.Equal("example.org", host);
            Assert.Equal(DnsMode.DnsOverHttps, settings.Dns);
            Assert.Equal("https://1.1.1.1/dns-query", settings.DohUrl);
            Assert.Equal(DnsMode.System, stack.Settings.Dns);
            Assert.False(File.Exists(SettingsPath));
            Assert.Contains("192.0.2.5", vm.StatusText);
            Assert.Contains("2001:db8::5", vm.StatusText);
        }
    }

    [Fact]
    public async Task Test_reports_failures_missing_host_and_invalid_fields_without_probing()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            vm.TestHost = "fail.test";
            await vm.TestAsync(default);
            Assert.Contains("no such host", vm.StatusText);
            Assert.Single(probes);

            vm.TestHost = "  ";
            await vm.TestAsync(default);
            Assert.Single(probes);
            Assert.NotEmpty(vm.StatusText);

            vm.TestHost = "ok.test";
            vm.IsDnsServers = true;
            vm.ServersText = "nope";
            await vm.TestAsync(default);
            Assert.Single(probes);
        }
    }

    [Fact]
    public void Load_problem_is_shown_on_the_page()
    {
        File.WriteAllText(SettingsPath, """{"network":{"proxy":"pac"}}""");
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            Assert.True(vm.HasLoadProblem);
            Assert.Contains("pac", vm.LoadProblemText);
        }
    }

    [Fact]
    public void Socks4_hint_follows_the_scheme_of_a_valid_manual_proxy()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            var changed = PropertyChangeLog.For(vm);

            // Not in manual mode: a socks4 URL typed in the hidden field gives no hint.
            vm.ProxyUrl = "socks4://127.0.0.1:1080";
            Assert.False(vm.ShowsSocks4Hint);

            vm.IsProxyManual = true;
            Assert.True(vm.ShowsSocks4Hint);
            Assert.Contains(nameof(vm.ShowsSocks4Hint), changed);

            vm.ProxyUrl = "SOCKS4://proxy.local";
            Assert.True(vm.ShowsSocks4Hint);

            foreach (var other in new[] { "socks4a://127.0.0.1:1080", "socks5://127.0.0.1:1080", "http://p:8080", "socks4://", "socks4", "" })
            {
                vm.ProxyUrl = other;
                Assert.False(vm.ShowsSocks4Hint, other);
            }

            vm.ProxyUrl = "socks4://127.0.0.1:1080";
            Assert.True(vm.ShowsSocks4Hint);
            vm.IsProxyNone = true;
            Assert.False(vm.ShowsSocks4Hint);
        }
    }

    [Fact]
    public void Doh_hint_appears_only_for_a_valid_url_whose_host_is_a_name()
    {
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            vm.DohUrl = "https://dns.google/dns-query";
            Assert.False(vm.ShowsDohNameHint);

            vm.IsDnsDoh = true;
            Assert.True(vm.ShowsDohNameHint);

            foreach (var literal in new[] { "https://1.1.1.1/dns-query", "https://[2606:4700:4700::1111]/dns-query" })
            {
                vm.DohUrl = literal;
                Assert.False(vm.ShowsDohNameHint, literal);
            }

            // Invalid URLs show their error instead of the hint.
            foreach (var invalid in new[] { "http://dns.google/dns-query", "dns.google", "" })
            {
                vm.DohUrl = invalid;
                Assert.False(vm.ShowsDohNameHint, invalid);
                Assert.True(vm.HasDohError, invalid);
            }

            vm.DohUrl = "https://cloudflare-dns.com/dns-query";
            Assert.True(vm.ShowsDohNameHint);
            vm.IsDnsServers = true;
            Assert.False(vm.ShowsDohNameHint);
        }
    }

    [Fact]
    public void Hints_are_computed_for_stored_settings_on_load()
    {
        new NetworkSettingsStore(SettingsPath).Save(
            new NetworkSettings(DnsMode.DnsOverHttps, [], "https://dns.google/dns-query", ProxyMode.Manual, "socks4://10.0.0.1:1080", []));
        var (vm, _, stack) = Create();
        using (vm)
        using (stack)
        {
            Assert.True(vm.ShowsDohNameHint);
            Assert.True(vm.ShowsSocks4Hint);
        }
    }
}
