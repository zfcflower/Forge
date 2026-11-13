using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The <c>network</c> settings section (DNS and proxy of every outbound connection); keys are namespaced <c>network.*</c>.</summary>
internal static class NetworkStrings
{
    public const string SectionTitle = "network.section";
    public const string Hint = "network.hint";
    public const string DnsHeading = "network.dns";
    public const string DnsSystem = "network.dns.system";
    public const string DnsServers = "network.dns.servers";
    public const string DnsDoh = "network.dns.doh";
    public const string DnsServersLabel = "network.dns.servers.label";
    public const string DnsDohLabel = "network.dns.doh.label";
    public const string DnsDohHint = "network.dns.doh.hint";
    public const string ProxyHeading = "network.proxy";
    public const string ProxySystem = "network.proxy.system";
    public const string ProxyNone = "network.proxy.none";
    public const string ProxyManual = "network.proxy.manual";
    public const string ProxyUrlLabel = "network.proxy.url";
    public const string ProxyUrlHint = "network.proxy.url.hint";
    public const string ProxySocks4Hint = "network.proxy.socks4.hint";
    public const string DnsDohNameHint = "network.dns.doh.nameHint";
    public const string ProxyBypassLabel = "network.proxy.bypass";
    public const string ErrorServer = "network.error.server";
    public const string ErrorServersEmpty = "network.error.serversEmpty";
    public const string ErrorDoh = "network.error.doh";
    public const string ErrorProxy = "network.error.proxy";
    public const string ErrorBypass = "network.error.bypass";
    public const string ApplyButton = "network.apply";
    public const string TestButton = "network.test";
    public const string TestHostLabel = "network.test.host";
    public const string StatusApplied = "network.status.applied";
    public const string StatusSaveFailed = "network.status.saveFailed";
    public const string StatusInvalid = "network.status.invalid";
    public const string StatusTesting = "network.status.testing";
    public const string StatusTestOk = "network.status.testOk";
    public const string StatusTestFailed = "network.status.testFailed";
    public const string StatusTestHostMissing = "network.status.testHostMissing";
    public const string LoadProblem = "network.loadProblem";
    public const string LoadProblemTitleKey = "network.loadProblem.title";

    public static LocText LoadProblemTitle => new(LoadProblemTitleKey);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitle, "网络与代理", "Network & proxy");
        Loc.Register(
            Hint,
            "Forge 的所有外连(模型提供商、模型目录、Web 工具、HTTP 方式的 MCP 服务器)都按这里的解析与代理设置建立。应用后对新建立的连接立即生效;已打开的连接最多 5 分钟后换到新路线。",
            "Every outbound connection Forge makes (model providers, the model catalog, web tools, MCP servers over HTTP) is resolved and proxied as set here. Applying takes effect for new connections at once; open connections move to the new route within 5 minutes.");
        Loc.Register(DnsHeading, "DNS 解析", "DNS resolution");
        Loc.Register(DnsSystem, "系统解析器", "System resolver");
        Loc.Register(DnsServers, "指定 DNS 服务器", "Specific DNS servers");
        Loc.Register(DnsDoh, "DNS over HTTPS", "DNS over HTTPS");
        Loc.Register(DnsServersLabel, "服务器 IP(可带端口;每行一个或用逗号分隔,按顺序尝试)", "Server IPs (optional port; one per line or comma separated, tried in order)");
        Loc.Register(DnsDohLabel, "DoH 地址", "DoH URL");
        Loc.Register(
            DnsDohHint,
            "例如 https://1.1.1.1/dns-query。",
            "For example https://1.1.1.1/dns-query.");
        Loc.Register(
            DnsDohNameHint,
            "这个 DoH 地址用的是主机名:它本身要先由系统 DNS 解析,系统 DNS 不可用或被污染时 DoH 也无法使用。改用 IP 地址(如 https://1.1.1.1/dns-query)可避免这一步。",
            "This DoH URL uses a host name: that name is resolved by the system DNS first, so DoH fails when the system DNS is unavailable or tampered with. An IP address (e.g. https://1.1.1.1/dns-query) avoids that step.");
        Loc.Register(
            ProxySocks4Hint,
            "SOCKS4 只能传 IPv4 地址,目标主机名会在本机用系统 DNS 解析(不走上面设置的 DNS)。要让代理负责解析,请改用 socks4a:// 或 socks5://。",
            "SOCKS4 only carries IPv4 addresses, so target host names are resolved locally with the system DNS (not the DNS set above). Use socks4a:// or socks5:// to let the proxy resolve them.");
        Loc.Register(ProxyHeading, "代理", "Proxy");
        Loc.Register(ProxySystem, "系统代理", "System proxy");
        Loc.Register(ProxyNone, "不使用代理", "No proxy");
        Loc.Register(ProxyManual, "手动指定", "Manual");
        Loc.Register(ProxyUrlLabel, "代理地址", "Proxy URL");
        Loc.Register(
            ProxyUrlHint,
            "支持 http、https、socks4、socks4a、socks5,例如 socks5://127.0.0.1:1080;账号密码写成 user:pass@host。",
            "http, https, socks4, socks4a and socks5 are supported, e.g. socks5://127.0.0.1:1080; credentials go in as user:pass@host.");
        Loc.Register(
            ProxyBypassLabel,
            "不走代理的主机(每行一个,如 *.corp.example;本机地址总是直连)",
            "Hosts that bypass the proxy (one per line, e.g. *.corp.example; loopback is always direct)");
        Loc.Register(ErrorServer, "{0} 不是 IP 地址(可带端口,如 1.1.1.1 或 [2606:4700::1111]:53)", "{0} is not an IP address (a port is optional, e.g. 1.1.1.1 or [2606:4700::1111]:53)");
        Loc.Register(ErrorServersEmpty, "至少填写一个 DNS 服务器", "Enter at least one DNS server");
        Loc.Register(ErrorDoh, "DoH 地址必须是以 https:// 开头的完整地址", "The DoH URL must be a full https:// address");
        Loc.Register(
            ErrorProxy,
            "代理地址必须是包含主机的 http、https、socks4、socks4a 或 socks5 地址",
            "The proxy URL must be an http, https, socks4, socks4a or socks5 address with a host");
        Loc.Register(ErrorBypass, "{0} 不是有效的主机规则", "{0} is not a valid host pattern");
        Loc.Register(ApplyButton, "应用", "Apply");
        Loc.Register(TestButton, "测试解析", "Test resolution");
        Loc.Register(TestHostLabel, "测试主机", "Test host");
        Loc.Register(StatusApplied, "已应用并保存", "Applied and saved");
        Loc.Register(StatusSaveFailed, "保存失败:{0}", "Save failed: {0}");
        Loc.Register(StatusInvalid, "请先修正标出的问题", "Fix the marked problems first");
        Loc.Register(StatusTesting, "正在解析 {0}…", "Resolving {0}...");
        Loc.Register(StatusTestOk, "{0} 解析为 {1}", "{0} resolves to {1}");
        Loc.Register(StatusTestFailed, "解析 {0} 失败:{1}", "Resolving {0} failed: {1}");
        Loc.Register(StatusTestHostMissing, "请填写要解析的主机名", "Enter a host name to resolve");
        Loc.Register(
            LoadProblem,
            "settings.json 的 network 段有无效值,这些项已按默认处理:{0}",
            "The network section of settings.json has invalid values; they were replaced by the defaults: {0}");
        Loc.Register(LoadProblemTitleKey, "网络设置有误", "Network settings are invalid");
    }
}
