using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// The provider feature's service: the stored definitions, the encrypted secrets behind them, the in-memory model cache and
/// the credential resolvers, plus the two operations the UI drives (save, refresh a model list). AppComposition owns one
/// instance for the whole process; a runtime factory closure over <see cref="CreateProvider"/> therefore always sees the
/// current catalogue, so a provider added while a session is open is reachable without a restart.
/// </summary>
internal sealed class ProviderServices
{
    /// <summary>The product id GitHub binds a Copilot token to; must match the one used when minting it.</summary>
    internal const string CopilotIntegrationId = "copilot-chat";

    /// <summary>Advertised client version for Copilot's endpoints; a fixed literal, not the assembly version.</summary>
    internal const string CopilotEditorVersion = "Forge/1.0";

    private readonly HttpClient http;
    private ProviderCatalog catalog;

    public ProviderServices(string settingsPath, string dataDirectory, HttpClient http, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        Store = new ProviderStore(settingsPath);
        Secrets = new ProviderSecretStore(dataDirectory);
        Cache = new ProviderModelCache();
        Credentials = new ProviderCredentials(http, Secrets, time: time);
        // Warm the encrypted store on the pool: the first reader would otherwise be the settings page (UI thread) or the
        // first provider status line, and decrypting on the UI thread is exactly what the shell must not do.
        _ = Secrets.WarmAsync();
        catalog = Store.Load(Secrets, Credentials, Cache);
    }

    public ProviderStore Store { get; }

    public ProviderSecretStore Secrets { get; }

    public ProviderModelCache Cache { get; }

    public ProviderCredentials Credentials { get; }

    /// <summary>The current catalogue; replaced whole on every save, so readers never see a half-applied edit.</summary>
    public ProviderCatalog Catalog => catalog;

    /// <summary>Raised on the thread that saved or reloaded, after <see cref="Catalog"/> has been replaced.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads <c>settings.json</c>; the running sessions keep the catalogue they already captured.</summary>
    public ProviderCatalog Reload()
    {
        catalog = Store.Load(Secrets, Credentials, Cache);
        Changed?.Invoke(this, EventArgs.Empty);
        return catalog;
    }

    /// <summary>
    /// The same reload, off the caller's thread: the settings page's refresh is a user action, and reading the file plus
    /// building the catalogue is file work the UI thread must not do.
    /// </summary>
    public async Task<ProviderCatalog> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await Task.Run(
            () =>
            {
                Secrets.WarmAsync(CancellationToken.None).GetAwaiter().GetResult();
                return Store.Load(Secrets, Credentials, Cache);
            },
            cancellationToken).ConfigureAwait(true);
        catalog = loaded;
        Changed?.Invoke(this, EventArgs.Empty);
        return catalog;
    }

    /// <summary>
    /// Writes the definitions and swaps the catalogue in. The file write runs on the pool; the swap happens after it, so a
    /// failed write leaves both the file and the live catalogue untouched.
    /// </summary>
    public async Task SaveAsync(
        IReadOnlyList<ProviderDefinition> definitions,
        string? defaultId,
        int? defaultContextWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        IReadOnlyList<ProviderDefinition> frozen = [.. definitions];
        await Task.Run(() => Store.Save(frozen, defaultId, defaultContextWindow), cancellationToken).ConfigureAwait(true);
        catalog = ProviderCatalog.Create(frozen, defaultId, Secrets, Credentials, Cache, defaultContextWindow);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds the provider for one model request from the live catalogue.</summary>
    public IChatProvider CreateProvider(ModelSettings settings) => catalog.CreateProvider(settings, http);

    /// <summary>
    /// A catalogue of one, for a definition that is not saved yet (the editor's "refresh models" before the first save).
    /// It shares the secrets, the model cache and the credential resolvers, so a pasted key works immediately.
    /// </summary>
    public ConnectedProvider CreateTransient(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var transient = ProviderCatalog.Create([definition], definition.Id, Secrets, Credentials, Cache);
        return transient.Default ?? new ConnectedProvider(definition, Secrets, Cache, Credentials);
    }

    /// <summary>
    /// Asks one provider for its model list and stores the answer in the in-memory cache (ids and any metadata the server
    /// reports). The headers are the provider's own credentials, built exactly the way a chat request builds them.
    /// </summary>
    public async Task<ProviderModelList> RefreshModelsAsync(ConnectedProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var (baseUrl, headers) = await ListRequestAsync(provider, cancellationToken).ConfigureAwait(false);
        var list = await ProviderModelListing.FetchAsync(baseUrl, headers, http, cancellationToken).ConfigureAwait(false);
        Cache.Set(provider.Id, list);
        return list;
    }

    /// <summary>
    /// The URL and headers for a <c>/models</c> call. Sign-in providers resolve their credential here (a network call for
    /// Copilot's session token), which is why listing is asynchronous like a turn.
    /// </summary>
    private async Task<(string BaseUrl, IReadOnlyDictionary<string, string> Headers)> ListRequestAsync(
        ConnectedProvider provider,
        CancellationToken cancellationToken)
    {
        var definition = provider.Definition;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = definition.BaseUrl;
        switch (definition.CredentialKind)
        {
            case ProviderCredentialKind.ApiKey:
                var key = provider.ResolveApiKey() ?? string.Empty;
                if (key.Length > 0)
                {
                    if (definition.Protocol == ProviderProtocol.Anthropic)
                    {
                        headers["x-api-key"] = key;
                        headers["anthropic-version"] = AnthropicProvider.Version;
                    }
                    else
                    {
                        headers["Authorization"] = "Bearer " + key;
                    }
                }

                break;

            case ProviderCredentialKind.ClaudeSubscription:
                var subscription = await Credentials.ReadClaudeAsync(cancellationToken).ConfigureAwait(false);
                headers["Authorization"] = "Bearer " + subscription.AccessToken;
                headers["anthropic-version"] = AnthropicProvider.Version;
                headers["anthropic-beta"] = string.Join(',', ClaudeSubscriptionCredentials.Betas);
                break;

            case ProviderCredentialKind.GitHubCopilot:
                var session = await Credentials.GetCopilotSessionAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                baseUrl = session.ApiBaseUrl;
                headers["Authorization"] = "Bearer " + session.Token;
                headers["Copilot-Integration-Id"] = CopilotIntegrationId;
                headers["Editor-Version"] = CopilotEditorVersion;
                headers["Editor-Plugin-Version"] = CopilotEditorVersion;
                break;
        }

        foreach (var (name, value) in definition.Headers)
        {
            headers[name] = value;
        }

        return (baseUrl, headers);
    }

    /// <summary>The sentence the shell shows when nothing is connected, resolved in the current language.</summary>
    public LocText StatusText => catalog.StatusText;

    public bool IsConfigured => catalog.IsConfigured;
}
