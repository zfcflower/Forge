using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Web;

/// <summary>One entry of the backend picker; <see cref="Token"/> is the <c>searchBackend</c> value (null = none).</summary>
internal sealed record WebBackendOption(string? Token, string Label);

/// <summary>
/// The <c>web</c> settings section: search backend picker, the key (masked, written to the encrypted secret store on Save)
/// or the SearXNG URL, a one-search test and the private-network switch. The picker and the switch persist at once; the key
/// and URL need Save so a half-typed value is never written.
/// </summary>
internal sealed class WebSettingsSectionViewModel : ObservableObject, IDisposable
{
    public const string SectionId = "web";
    public const int SectionOrder = 140;

    private readonly WebServices services;
    private WebBackendOption[] options = CreateOptions();
    private string apiKeyText = string.Empty;
    private string searxngUrl;
    private string statusText = string.Empty;
    private bool disposed;

    public WebSettingsSectionViewModel(WebServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        searxngUrl = services.Store.Current.SearxngUrl;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        TestCommand = new AsyncRelayCommand(TestAsync);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public System.Windows.Input.ICommand SaveCommand { get; }

    public System.Windows.Input.ICommand TestCommand { get; }

    public IReadOnlyList<WebBackendOption> Backends => options;

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
            OnPropertyChanged(nameof(ShowsUrl));
            OnPropertyChanged(nameof(HasStoredKey));
        }
    }

    public bool ShowsKey => services.Store.Current.SearchBackend is WebSettings.Brave or WebSettings.Tavily;

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
        options = CreateOptions();
        OnPropertyChanged(nameof(Backends));
        OnPropertyChanged(nameof(SelectedBackend));
    }

    private static WebBackendOption[] CreateOptions() =>
    [
        new(null, Loc.Instance[WebStrings.BackendNone]),
        new(WebSettings.Brave, "Brave Search"),
        new(WebSettings.Tavily, "Tavily"),
        new(WebSettings.Searxng, "SearXNG"),
    ];
}
