using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.Core.Net;
using Forge.Core.Tools.Web;
using Forge.IO;

namespace Forge.App.Web;

/// <summary>The <c>web</c> section of settings.json. API keys are deliberately not part of it.</summary>
internal sealed record WebSettings(string? SearchBackend, string SearxngUrl, bool AllowPrivateNetwork)
{
    public const string Brave = "brave";
    public const string Tavily = "tavily";
    public const string Searxng = "searxng";

    public static WebSettings Default { get; } = new(null, string.Empty, false);
}

/// <summary>
/// The <c>web</c> section of <c>settings.json</c>: <c>searchBackend</c> (<c>brave</c>, <c>tavily</c>, <c>searxng</c> or
/// absent), <c>searxngUrl</c> and <c>allowPrivateNetwork</c> (default false). Follows the file rules of
/// <see cref="Agents.AgentDefaultsStore"/>: the whole file is read as a node tree and only the <c>web</c> node is replaced,
/// so other sections and unknown fields survive; writes take <see cref="SettingsFileMutex"/> and are atomic.
/// </summary>
internal sealed class WebSettingsStore
{
    private const string SectionName = "web";
    private readonly string settingsPath;
    private readonly Lock gate = new();
    private WebSettings current = WebSettings.Default;

    public WebSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        Reload();
    }

    public WebSettings Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Re-reads the file; a missing or damaged section means defaults.</summary>
    public void Reload()
    {
        var loaded = WebSettings.Default;
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root && root[SectionName] is JsonObject web)
                {
                    loaded = Parse(web);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        lock (gate)
        {
            current = loaded;
        }
    }

    public void Save(WebSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = Normalize(value);
        using var lease = SettingsFileMutex.Acquire(settingsPath);
        var root = ProviderStore.ReadRootForEdit(settingsPath);
        var web = root[SectionName] as JsonObject ?? new JsonObject();
        if (normalized.SearchBackend is { } backend)
        {
            web["searchBackend"] = backend;
        }
        else
        {
            web.Remove("searchBackend");
        }

        web["searxngUrl"] = normalized.SearxngUrl;
        web["allowPrivateNetwork"] = normalized.AllowPrivateNetwork;
        root[SectionName] = web;
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(json));
        lock (gate)
        {
            current = normalized;
        }
    }

    private static WebSettings Parse(JsonObject web)
    {
        var backend = web["searchBackend"] is JsonValue b && b.TryGetValue<string>(out var s) ? s : null;
        var url = web["searxngUrl"] is JsonValue u && u.TryGetValue<string>(out var us) ? us : string.Empty;
        var allow = web["allowPrivateNetwork"] is JsonValue a && a.TryGetValue<bool>(out var av) && av;
        return Normalize(new WebSettings(backend, url, allow));
    }

    private static WebSettings Normalize(WebSettings value)
    {
        var backend = value.SearchBackend?.Trim().ToLowerInvariant();
        if (backend is not (WebSettings.Brave or WebSettings.Tavily or WebSettings.Searxng))
        {
            backend = null;
        }

        return new WebSettings(backend, (value.SearxngUrl ?? string.Empty).Trim(), value.AllowPrivateNetwork);
    }
}

/// <summary>
/// The shell's web tools: <c>web_fetch</c> and <c>web_search</c>, both always registered so the tool surface (and the
/// prompt cache) stays stable. Keys live in the encrypted <see cref="ProviderSecretStore"/> under fixed references, never
/// in settings.json. Settings apply live: <c>web_search</c> builds its backend from the current settings and key on every
/// call (and reports "not configured" when there is none), and <c>web_fetch</c> reads the private-network switch on every
/// fetch. With a <see cref="NetworkStack"/> both clients go through it, and the fetch client refuses private addresses at
/// connect time (after resolution, so DNS rebinding cannot slip past a resolve-only check) unless the switch allows them.
/// </summary>
internal sealed class WebServices : IDisposable
{
    public const string BraveKeyReference = "web.brave.apiKey";
    public const string TavilyKeyReference = "web.tavily.apiKey";

    private readonly HttpClient http;
    private readonly HttpClient searchHttp;
    private readonly NetworkStack? network;
    private readonly bool ownsClients;

    /// <summary>One client for both tools (tests); a client passed in stays the caller's.</summary>
    public WebServices(WebSettingsStore store, ProviderSecretStore secrets, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        Store = store;
        Secrets = secrets;
        ownsClients = http is null;
        this.http = http ?? CreateHttpClient();
        searchHttp = this.http;
    }

    /// <summary>
    /// The application's form: a fetch client (no redirects, decompression, private addresses refused at connect unless
    /// allowed) and a search client (redirects, decompression, no address filter since SearXNG is often local), both
    /// routed through <paramref name="network"/> and owned by this instance.
    /// </summary>
    public WebServices(WebSettingsStore store, ProviderSecretStore secrets, NetworkStack network)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(network);
        Store = store;
        Secrets = secrets;
        this.network = network;
        ownsClients = true;
        http = new HttpClient(network.CreateHandler(new NetworkHandlerOptions
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            AddressFilter = address => store.Current.AllowPrivateNetwork || !WebFetchTool.IsPrivate(address),
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        searchHttp = new HttpClient(network.CreateHandler(new NetworkHandlerOptions { AutomaticDecompression = DecompressionMethods.All }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public WebSettingsStore Store { get; }

    public ProviderSecretStore Secrets { get; }

    /// <summary>A client that never follows redirects itself (the fetch tool re-validates every hop) and decompresses.</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static string? KeyReference(string? backend) => backend switch
    {
        WebSettings.Brave => BraveKeyReference,
        WebSettings.Tavily => TavilyKeyReference,
        _ => null,
    };

    /// <summary>The backend for <paramref name="settings"/>, or null when it is not usable (no backend, no key, no URL).</summary>
    public IWebSearchBackend? CreateBackend(WebSettings settings, string? keyOverride = null)
    {
        var key = string.IsNullOrWhiteSpace(keyOverride) ? Secrets.Get(KeyReference(settings.SearchBackend)) : keyOverride.Trim();
        return settings.SearchBackend switch
        {
            WebSettings.Brave when key is not null => new BraveSearchBackend(searchHttp, key),
            WebSettings.Tavily when key is not null => new TavilySearchBackend(searchHttp, key),
            WebSettings.Searxng when Uri.TryCreate(settings.SearxngUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" => new SearxngSearchBackend(searchHttp, settings.SearxngUrl),
            _ => null,
        };
    }

    /// <summary>
    /// The tools to register: <c>web_fetch</c> and <c>web_search</c>, always both; the search backend is chosen per call.
    /// </summary>
    public IReadOnlyList<ITool> CreateTools()
    {
        var stack = network;
        var fetchOptions = new WebFetchOptions
        {
            AllowPrivateNetwork = () => Store.Current.AllowPrivateNetwork,
            ConnectsDirectly = stack is null ? null : stack.ConnectsDirectly,
        };
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = stack is null ? null : (host, ct) => stack.ResolveAsync(host, ct).AsTask();
        return
        [
            new WebFetchTool(http, resolve, fetchOptions),
            new WebSearchTool(() => CreateBackend(Store.Current)),
        ];
    }

    public void Dispose()
    {
        if (ownsClients)
        {
            http.Dispose();
            if (!ReferenceEquals(searchHttp, http))
            {
                searchHttp.Dispose();
            }
        }
    }
}
