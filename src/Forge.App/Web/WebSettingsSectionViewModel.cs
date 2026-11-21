using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Web;

/// <summary>One entry of the backend picker; <see cref="Token"/> is the <c>searchBackend</c> value (null = none), and
/// <see cref="IsUsable"/> marks the entries that can search right now with a green dot.</summary>
internal sealed record WebBackendOption(string? Token, string Label, bool IsUsable);

/// <summary>
/// The <c>web</c> settings section: search backend picker, the key (masked, written to the encrypted secret store on Save)
/// or the SearXNG URL, a one-search test and the private-network switch. The picker and the switch persist at once; the key
/// and URL need Save so a half-typed value is never written.
/// </summary>
internal sealed class WebSettingsSectionViewModel : ObservableObject, IDisposable, IReloadableSettingsSection
{
    public const string SectionId = "web";
    public const int SectionOrder = 140;

    private readonly WebServices services;
    private WebBackendOption[] options;
    private string apiKeyText = string.Empty;
    private string searxngUrl;
    private string statusText = string.Empty;
    private bool disposed;

    /// <summary>The thread the section was built on (the UI thread); a language notification from another one is not ours.</summary>
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    public WebSettingsSectionViewModel(WebServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        options = CreateOptions();
        searxngUrl = services.Store.Current.SearxngUrl;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        TestCommand = new AsyncRelayCommand(TestAsync);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public System.Windows.Input.ICommand SaveCommand { get; }

    public System.Windows.Input.ICommand TestCommand { get; }

    public IReadOnlyList<WebBackendOption> Backends => options;

    /// <summary>
    /// Recomputes the availability dots. The section is built once at startup, so a key saved later — here or on the providers
    /// page, whose DeepSeek key the backend shares — would otherwise leave a stale dot until the next launch.
    /// </summary>
    public void Reload()
    {
        options = CreateOptions();
        OnPropertyChanged(nameof(Backends));
        OnPropertyChanged(nameof(SelectedBackend));
        OnPropertyChanged(nameof(HasStoredKey));
    }

    public WebBackendOption SelectedBackend
    {
        get => options.First(o => o.Token == services.Store.Current.SearchBackend);
        set
        {
            if (value is null || value.Token == services.Store.Current.SearchBackend)
            {
                return;
            }

            if (Persist(services.Store.Current with { SearchBackend = value.Token }))
            {
                ApiKeyText = string.Empty;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsKey));
            OnPropertyChanged(nameof(KeyIsOptional));
            OnPropertyChanged(nameof(ShowsSharedKeyNote));
            OnPropertyChanged(nameof(ShowsUrl));
            OnPropertyChanged(nameof(HasStoredKey));
        }
    }

    public bool ShowsKey => services.Store.Current.SearchBackend is WebSettings.Brave or WebSettings.Tavily or WebSettings.Exa or WebSettings.Deepseek;

    /// <summary>Exa also answers with no key at all: an empty field selects its anonymous public MCP.</summary>
    public bool KeyIsOptional => services.Store.Current.SearchBackend == WebSettings.Exa;

    /// <summary>DeepSeek also takes the key its model provider already carries, so the field may stay empty.</summary>
    public bool ShowsSharedKeyNote => services.Store.Current.SearchBackend == WebSettings.Deepseek;

    public bool ShowsUrl => services.Store.Current.SearchBackend == WebSettings.Searxng;

    public bool HasStoredKey => services.Secrets.Has(WebServices.KeyReference(services.Store.Current.SearchBackend));

    public string ApiKeyText
    {
        get => apiKeyText;
        set => Set(ref apiKeyText, value ?? string.Empty);
    }

    public string SearxngUrl
    {
        get => searxngUrl;
        set => Set(ref searxngUrl, value ?? string.Empty);
    }

    public bool AllowPrivateNetwork
    {
        get => services.Store.Current.AllowPrivateNetwork;
        set
        {
            if (value != services.Store.Current.AllowPrivateNetwork)
            {
                Persist(services.Store.Current with { AllowPrivateNetwork = value });
                OnPropertyChanged();
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
        }
    }

    private bool Persist(WebSettings value)
    {
        try
        {
            services.Store.Save(value);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = Loc.Instance[WebStrings.StatusSaveFailed] + ": " + exception.Message;
            return false;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var current = services.Store.Current;
            if (ShowsUrl && !Persist(current with { SearxngUrl = searxngUrl }))
            {
                return;
            }

            if (ShowsKey && apiKeyText.Trim().Length > 0 && WebServices.KeyReference(current.SearchBackend) is { } reference)
            {
                var key = apiKeyText.Trim();
                await Task.Run(() => services.Secrets.Set(reference, key)).ConfigureAwait(true);
                ApiKeyText = string.Empty;
            }

            OnPropertyChanged(nameof(HasStoredKey));
            StatusText = Loc.Instance[WebStrings.StatusSaved];
            Reload();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = Loc.Instance[WebStrings.StatusSaveFailed] + ": " + exception.Message;
        }
    }

    private async Task TestAsync(CancellationToken ct)
    {
        var settings = services.Store.Current with { SearxngUrl = searxngUrl };
        var backend = services.CreateBackend(settings, apiKeyText);
        if (backend is null)
        {
            StatusText = Loc.Instance[WebStrings.StatusNotConfigured];
            return;
        }

        StatusText = Loc.Instance[WebStrings.StatusTesting];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var results = await backend.SearchAsync("forge", 1, timeout.Token).ConfigureAwait(true);
            StatusText = Loc.Instance[WebStrings.StatusTestOk] + ": " + results.Count;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusText = Loc.Instance[WebStrings.StatusTestFailed] + ": " + exception.Message;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A switch raised on another thread belongs to a window that is gone: the picker's options are bound
        // on the thread that built the section.
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        Reload();
    }

    private WebBackendOption[] CreateOptions() =>
    [
        new(null, Loc.Instance[WebStrings.BackendNone], false),
        new(WebSettings.Exa, "Exa", services.IsUsable(WebSettings.Exa)),
        new(WebSettings.Tavily, "Tavily", services.IsUsable(WebSettings.Tavily)),
        new(WebSettings.Deepseek, "DeepSeek", services.IsUsable(WebSettings.Deepseek)),
        new(WebSettings.Brave, "Brave Search", services.IsUsable(WebSettings.Brave)),
        new(WebSettings.Searxng, "SearXNG", services.IsUsable(WebSettings.Searxng)),
    ];
}
