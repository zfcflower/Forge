using System.Net;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Net;

namespace Forge.App.Network;

/// <summary>
/// The <c>network</c> settings section: DNS mode (system / servers / DoH) with its server list or URL, proxy mode
/// (system / none / manual) with its URL and bypass list, inline validation, Apply (save, then
/// <see cref="INetworkStack.Apply"/>) and a resolution test with the entered (not yet applied) settings. Only the fields of
/// the selected modes are validated; a field of an unselected mode keeps its stored value when its text is invalid, so a
/// half-typed value never blocks switching modes and never reaches the file.
/// </summary>
internal sealed class NetworkSettingsSectionViewModel : ObservableObject, IDisposable
{
    public const string DefaultTestHost = "api.anthropic.com";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly NetworkSettingsStore store;
    private readonly INetworkStack stack;
    private readonly Func<NetworkSettings, string, CancellationToken, Task<IPAddress[]>> probe;
    private DnsMode dns;
    private ProxyMode proxy;
    private string serversText;
    private string dohUrl;
    private string proxyUrl;
    private string bypassText;
    private string testHost = DefaultTestHost;
    private string serversError = string.Empty;
    private string dohError = string.Empty;
    private string proxyError = string.Empty;
    private string bypassError = string.Empty;
    private string statusText = string.Empty;
    private bool showsSocks4Hint;
    private bool showsDohNameHint;
    private bool disposed;

    /// <param name="probe">Resolves a host with candidate settings without applying them.</param>
    public NetworkSettingsSectionViewModel(
        NetworkSettingsStore store,
        INetworkStack stack,
        Func<NetworkSettings, string, CancellationToken, Task<IPAddress[]>> probe)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(probe);
        this.store = store;
        this.stack = stack;
        this.probe = probe;
        var current = store.Current;
        dns = current.Dns;
        proxy = current.Proxy;
        serversText = string.Join(Environment.NewLine, current.DnsServers);
        dohUrl = current.DohUrl ?? string.Empty;
        proxyUrl = current.ProxyUrl ?? string.Empty;
        bypassText = string.Join(Environment.NewLine, current.ProxyBypass);
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), onError: ReportSaveFailure);
        TestCommand = new AsyncRelayCommand(TestAsync, onError: ReportTestFailure);
        Validate();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public System.Windows.Input.ICommand ApplyCommand { get; }

    public System.Windows.Input.ICommand TestCommand { get; }

    public bool IsDnsSystem
    {
        get => dns == DnsMode.System;
        set => SetDns(DnsMode.System, value);
    }

    public bool IsDnsServers
    {
        get => dns == DnsMode.Servers;
        set => SetDns(DnsMode.Servers, value);
    }

    public bool IsDnsDoh
    {
        get => dns == DnsMode.DnsOverHttps;
        set => SetDns(DnsMode.DnsOverHttps, value);
    }

    public bool IsProxySystem
    {
        get => proxy == ProxyMode.System;
        set => SetProxy(ProxyMode.System, value);
    }

    public bool IsProxyNone
    {
        get => proxy == ProxyMode.None;
        set => SetProxy(ProxyMode.None, value);
    }

    public bool IsProxyManual
    {
        get => proxy == ProxyMode.Manual;
        set => SetProxy(ProxyMode.Manual, value);
    }

    public string ServersText
    {
        get => serversText;
        set => SetText(ref serversText, value);
    }

    public string DohUrl
    {
        get => dohUrl;
        set => SetText(ref dohUrl, value);
    }

    public string ProxyUrl
    {
        get => proxyUrl;
        set => SetText(ref proxyUrl, value);
    }

    public string BypassText
    {
        get => bypassText;
        set => SetText(ref bypassText, value);
    }

    public string TestHost
    {
        get => testHost;
        set => Set(ref testHost, value ?? string.Empty);
    }

    public string ServersError
    {
        get => serversError;
        private set
        {
            if (Set(ref serversError, value))
            {
                OnPropertyChanged(nameof(HasServersError));
            }
        }
    }

    public bool HasServersError => serversError.Length > 0;

    public string DohError
    {
        get => dohError;
        private set
        {
            if (Set(ref dohError, value))
            {
                OnPropertyChanged(nameof(HasDohError));
            }
        }
    }

    public bool HasDohError => dohError.Length > 0;

    public string ProxyError
    {
        get => proxyError;
        private set
        {
            if (Set(ref proxyError, value))
            {
                OnPropertyChanged(nameof(HasProxyError));
            }
        }
    }

    public bool HasProxyError => proxyError.Length > 0;

    public string BypassError
    {
        get => bypassError;
        private set
        {
            if (Set(ref bypassError, value))
            {
                OnPropertyChanged(nameof(HasBypassError));
            }
        }
    }

    public bool HasBypassError => bypassError.Length > 0;

    /// <summary>
    /// A valid manual <c>socks4://</c> proxy is entered: SOCKS4 carries only an IPv4 address, so the target name is resolved
    /// locally with the system DNS (not the DNS chosen above); socks4a/socks5 hand the name to the proxy.
    /// </summary>
    public bool ShowsSocks4Hint
    {
        get => showsSocks4Hint;
        private set => Set(ref showsSocks4Hint, value);
    }

    /// <summary>
    /// A valid DoH URL whose host is a name rather than an IP literal is entered: that name is resolved by the system DNS
    /// before any DoH query, so an IP literal avoids depending on it.
    /// </summary>
    public bool ShowsDohNameHint
    {
        get => showsDohNameHint;
        private set => Set(ref showsDohNameHint, value);
    }

    /// <summary>Whether the entered values are valid for the selected modes (Apply and Test need it).</summary>
    public bool IsValid => !HasServersError && !HasDohError && !HasProxyError && !HasBypassError;

    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    /// <summary>The problem of the last load (invalid values replaced by defaults), or empty.</summary>
    public string LoadProblemText =>
        store.LoadProblem is { } problem ? Loc.Instance.Format(NetworkStrings.LoadProblem, problem) : string.Empty;

    public bool HasLoadProblem => store.LoadProblem is not null;

    /// <summary>The settings the fields describe now, or null with the error properties set when they are invalid.</summary>
    public NetworkSettings? BuildCandidate()
    {
        Validate();
        if (!IsValid)
        {
            return null;
        }

        var stored = store.Current;
        var servers = NetworkSettingsRules.SplitList(serversText);
        var bypass = NetworkSettingsRules.SplitList(bypassText);
        var doh = Blank(dohUrl);
        var proxyText = Blank(proxyUrl);
        return new NetworkSettings(
            dns,
            servers.All(static s => NetworkSettingsRules.TryParseServer(s, out _)) ? servers : stored.DnsServers,
            doh is null || NetworkSettingsRules.IsValidDohUrl(doh) ? doh : stored.DohUrl,
            proxy,
            proxyText is null || NetworkSettingsRules.TryParseProxyUrl(proxyText, out _, out _) ? proxyText : stored.ProxyUrl,
            bypass.All(NetworkSettingsRules.IsValidBypassPattern) ? bypass : stored.ProxyBypass);
    }

    /// <summary>Saves the entered settings and applies them to the stack; reports through <see cref="StatusText"/>.</summary>
    public async Task ApplyAsync()
    {
        if (BuildCandidate() is not { } candidate)
        {
            StatusText = Loc.Instance[NetworkStrings.StatusInvalid];
            return;
        }

        try
        {
            await Task.Run(() => store.Save(candidate)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ReportSaveFailure(exception);
            return;
        }

        stack.Apply(store.Current);
        OnPropertyChanged(nameof(LoadProblemText));
        OnPropertyChanged(nameof(HasLoadProblem));
        StatusText = Loc.Instance[NetworkStrings.StatusApplied];
    }

    /// <summary>Resolves <see cref="TestHost"/> with the entered settings (not applied).</summary>
    public async Task TestAsync(CancellationToken ct)
    {
        var host = testHost.Trim();
        if (host.Length == 0)
        {
            StatusText = Loc.Instance[NetworkStrings.StatusTestHostMissing];
            return;
        }

        if (BuildCandidate() is not { } candidate)
        {
            StatusText = Loc.Instance[NetworkStrings.StatusInvalid];
            return;
        }

        StatusText = Loc.Instance.Format(NetworkStrings.StatusTesting, host);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var addresses = await probe(candidate, host, timeout.Token).ConfigureAwait(true);
            StatusText = Loc.Instance.Format(NetworkStrings.StatusTestOk, host, string.Join(", ", addresses.Select(static a => a.ToString())));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            StatusText = Loc.Instance.Format(NetworkStrings.StatusTestFailed, host, new TimeoutException().Message);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            StatusText = Loc.Instance.Format(NetworkStrings.StatusTestFailed, host, exception.Message);
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
        }
    }

    private void SetDns(DnsMode mode, bool selected)
    {
        if (!selected || dns == mode)
        {
            return;
        }

        dns = mode;
        OnPropertyChanged(nameof(IsDnsSystem));
        OnPropertyChanged(nameof(IsDnsServers));
        OnPropertyChanged(nameof(IsDnsDoh));
        Validate();
    }

    private void SetProxy(ProxyMode mode, bool selected)
    {
        if (!selected || proxy == mode)
        {
            return;
        }

        proxy = mode;
        OnPropertyChanged(nameof(IsProxySystem));
        OnPropertyChanged(nameof(IsProxyNone));
        OnPropertyChanged(nameof(IsProxyManual));
        Validate();
    }

    private void SetText(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value ?? string.Empty, name))
        {
            Validate();
        }
    }

    private void Validate()
    {
        var loc = Loc.Instance;
        var servers = NetworkSettingsRules.SplitList(serversText);
        var badServer = servers.FirstOrDefault(static s => !NetworkSettingsRules.TryParseServer(s, out _));
        ServersError = dns != DnsMode.Servers ? string.Empty
            : badServer is not null ? loc.Format(NetworkStrings.ErrorServer, badServer)
            : servers.Count == 0 ? loc[NetworkStrings.ErrorServersEmpty]
            : string.Empty;

        DohError = dns == DnsMode.DnsOverHttps && !NetworkSettingsRules.IsValidDohUrl(Blank(dohUrl))
            ? loc[NetworkStrings.ErrorDoh]
            : string.Empty;

        ProxyError = proxy == ProxyMode.Manual && !NetworkSettingsRules.TryParseProxyUrl(Blank(proxyUrl), out _, out _)
            ? loc[NetworkStrings.ErrorProxy]
            : string.Empty;

        var badPattern = proxy == ProxyMode.Manual
            ? NetworkSettingsRules.SplitList(bypassText).FirstOrDefault(static p => !NetworkSettingsRules.IsValidBypassPattern(p))
            : null;
        BypassError = badPattern is null ? string.Empty : loc.Format(NetworkStrings.ErrorBypass, badPattern);
        OnPropertyChanged(nameof(IsValid));

        ShowsSocks4Hint = proxy == ProxyMode.Manual
            && NetworkSettingsRules.TryParseProxyUrl(Blank(proxyUrl), out var parsedProxy, out _)
            && string.Equals(parsedProxy.Scheme, "socks4", StringComparison.Ordinal);
        ShowsDohNameHint = dns == DnsMode.DnsOverHttps
            && Blank(dohUrl) is { } doh
            && NetworkSettingsRules.IsValidDohUrl(doh)
            && Uri.TryCreate(doh, UriKind.Absolute, out var dohUri)
            && dohUri.HostNameType == UriHostNameType.Dns;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void ReportSaveFailure(Exception exception) =>
        StatusText = Loc.Instance.Format(NetworkStrings.StatusSaveFailed, exception.Message);

    private void ReportTestFailure(Exception exception) =>
        StatusText = Loc.Instance.Format(NetworkStrings.StatusTestFailed, testHost.Trim(), exception.Message);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Validate();
        OnPropertyChanged(nameof(LoadProblemText));
    }
}
