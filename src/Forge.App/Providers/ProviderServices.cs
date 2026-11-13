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

    /// <param name="claudeCredentialsPath">Where the local Claude Code login is read from; <c>null</c> is the real
    /// <c>~/.claude/.credentials.json</c>. Named so a test can point at its own file.</param>
    public ProviderServices(
        string settingsPath,
        string dataDirectory,
        HttpClient http,
        TimeProvider? time = null,
        string? claudeCredentialsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        // The usage log is optional by construction: the store always exists (the settings row drives it) and the recorder writes only
        // while it is enabled, so flipping the switch takes effect on the next request without rebuilding the catalogue.
        UsageLog = new UsageLogSettingsStore(settingsPath);
        UsageRecorder = new JsonlUsageRecorder(Path.Combine(dataDirectory, "usage"), UsageLog, time);
        Store = new ProviderStore(settingsPath, usageRecorder: UsageRecorder);
        Secrets = new ProviderSecretStore(dataDirectory);
        // Both caches live beside settings.json and are read on the pool: a cold start shows unknown prices until they
        // arrive instead of blocking the window on a JSON parse.
        Cache = new ProviderModelCache(Path.Combine(dataDirectory, ProviderModelsFile.FileName));
        ModelCatalog = new ModelCatalogStore(dataDirectory, time);
        // One memo for the whole process: the chain's inputs all live here, so the invalidation points do too.
        Memo = new ModelMetadataMemo();
        ModelCatalog.Changed += (_, _) => Memo.Invalidate();
        // The provider cache also lands asynchronously; an answer resolved before it did would be remembered as "the catalog
        // knows nothing" until the next save, so the memo is dropped again when the file has been read.
        _ = InvalidateMemoWhenModelListsLoadAsync();
        Credentials = new ProviderCredentials(http, Secrets, claudeCredentialsPath, time);
        // Warm the encrypted store on the pool: the first reader would otherwise be the settings page (UI thread) or the
        // first provider status line, and decrypting on the UI thread is exactly what the shell must not do.
        _ = Secrets.WarmAsync();
        catalog = Store.Load(Secrets, Credentials, Cache, ModelCatalog);
    }

    public ProviderStore Store { get; }

    /// <summary>The <c>usageLog</c> section, shared by the settings row (writes) and the recorder (reads).</summary>
    public UsageLogSettingsStore UsageLog { get; }

    /// <summary>Where model requests are recorded while the log is on; always present, silent while it is off.</summary>
    public JsonlUsageRecorder UsageRecorder { get; }

    public ProviderSecretStore Secrets { get; }

    public ProviderModelCache Cache { get; }

    /// <summary>The fetched model catalog (<c>model-catalog.json</c>): the weakest layer of the metadata chain.</summary>
    public ModelCatalogStore ModelCatalog { get; }

    /// <summary>Resolved metadata per (provider, model); dropped whenever the catalog, a model list or settings change.</summary>
    public ModelMetadataMemo Memo { get; }

    public ProviderCredentials Credentials { get; }

    /// <summary>The current catalogue; replaced whole on every save, so readers never see a half-applied edit.</summary>
    public ProviderCatalog Catalog => catalog;

    /// <summary>Raised on the thread that saved or reloaded, after <see cref="Catalog"/> has been replaced.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-reads <c>settings.json</c>; the running sessions keep the catalogue they already captured.</summary>
    public ProviderCatalog Reload()
    {
        // A reload is the moment the account or its settings may have changed, so what this process learned about refused betas is
        // forgotten: an account that was fixed must not stay on the reduced shape until the next launch.
        ProviderDegradations.Clear();
        Memo.Invalidate();
        catalog = Store.Load(Secrets, Credentials, Cache, ModelCatalog);
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
                return Store.Load(Secrets, Credentials, Cache, ModelCatalog);
            },
            cancellationToken).ConfigureAwait(true);
        ProviderDegradations.Clear();
        Memo.Invalidate();
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
        ContextWindowDefault defaultContextWindow,
        string? modelCatalogSourceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        IReadOnlyList<ProviderDefinition> frozen = [.. definitions];
        // Null means "the page did not touch the address"; the URL is only ever changed from the catalog row.
        var sourceUrl = modelCatalogSourceUrl ?? catalog.ModelCatalogSourceUrl;
        await Task.Run(() => Store.Save(frozen, defaultId, defaultContextWindow, sourceUrl), cancellationToken).ConfigureAwait(true);
        // Re-read rather than rebuild from the arguments: the file also holds the wire claim and the usage-log switch, and a catalogue
        // assembled from the save call alone would silently drop both until the next launch.
        Memo.Invalidate();
        catalog = await Task.Run(() => Store.Load(Secrets, Credentials, Cache, ModelCatalog), cancellationToken).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves one provider's cache lifetime and swaps the catalogue in, so the next turn uses it. Only the field named here changes; every
    /// other setting is carried over from the live catalogue, which is what keeps one row's control from rewriting another row's data.
    /// </summary>
    public async Task SaveCacheTtlAsync(string providerId, string? ttl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var normalized = string.IsNullOrWhiteSpace(ttl) || ttl == "auto" ? null : ttl;
        ProviderDefinition[] definitions = [.. catalog.Providers.Select(provider =>
            string.Equals(provider.Id, providerId, StringComparison.Ordinal)
                ? provider.Definition with { PromptCacheTtl = normalized }
                : provider.Definition)];
        await SaveAsync(definitions, catalog.Default?.Id, catalog.DefaultContextWindow, catalog.ModelCatalogSourceUrl, cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task InvalidateMemoWhenModelListsLoadAsync()
    {
        try
        {
            await Cache.Loaded.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache that could not be read is the same as an empty one; the memo holding pre-load answers is still wrong.
        }

        Memo.Invalidate();
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
        var transient = ProviderCatalog.Create(
            [definition],
            definition.Id,
            Secrets,
            Credentials,
            Cache,
            // No memo: a transient definition is not saved, so a (provider, model) memo entry could not be invalidated by
            // the normal "config saved" path and would keep answering for a definition that no longer exists.
            new ProviderCatalogOptions(ModelCatalog: ModelCatalog));
        return transient.Default ?? new ConnectedProvider(definition, Secrets, Cache, Credentials, ModelCatalog);
    }

    /// <summary>
    /// The connection test for a preset that cannot list its models (Anthropic-protocol vendors): one tiny turn through the
    /// provider's own chat path. The first stream event proves the endpoint, the credential and the model id together; the
    /// reply text is discarded.
    /// </summary>
    public async Task VerifyChatAsync(ConnectedProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.DefaultModel))
        {
            throw new ProviderException(ProviderStrings.NoModelForTestText().Resolve(), retryable: false);
        }

        var chat = provider.CreateProvider(http);
        var request = new ChatRequest(
            string.Empty,
            [],
            [ChatMessage.User("ping")],
            new ModelSettings(provider.DefaultModel, ThinkingEffort.Off, MaxOutputTokens: 16, ProviderId: provider.Id));
        await foreach (var _ in chat.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
    }

    /// <summary>
    /// Asks one provider for its model list and stores the answer in the in-memory cache (ids and any metadata the server
    /// reports). The headers are the provider's own credentials, built exactly the way a chat request builds them.
    /// </summary>
    public async Task<ProviderModelList> RefreshModelsAsync(ConnectedProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var request = await ListRequestAsync(provider, cancellationToken).ConfigureAwait(false);
        var list = await ProviderModelListing
            .FetchAsync(request.BaseUrl, request.Headers, http, cancellationToken, request.Query)
            .ConfigureAwait(false);
        Cache.Set(provider.Id, list);
        // Awaited, not fire-and-forget: the refresh is only finished once the answers are also on disk for the next start.
        await Cache.SaveAsync(cancellationToken).ConfigureAwait(false);
        Memo.Invalidate();
        return list;
    }

    /// <summary>
    /// The URL and headers for a <c>/models</c> call. Sign-in providers resolve their credential here (a network call for
    /// Copilot's session token), which is why listing is asynchronous like a turn.
    /// </summary>
    private async Task<(string BaseUrl, string? Query, IReadOnlyDictionary<string, string> Headers)> ListRequestAsync(
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

            case ProviderCredentialKind.KimiCode:
                var accessToken = await Credentials.GetKimiAccessTokenAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                headers["Authorization"] = "Bearer " + accessToken;
                break;

            case ProviderCredentialKind.XAiSubscription:
                var xaiAccessToken = await Credentials.GetXAiAccessTokenAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                headers["Authorization"] = "Bearer " + xaiAccessToken;
                break;

            // Meta and ChatGPT list no models of their own kind here, but the headers are built the same way a chat request
            // builds them, so a listing call (or a connection test that reuses it) carries the same credential.
            case ProviderCredentialKind.MetaSubscription:
                var metaApiKey = await Credentials.GetMetaApiKeyAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                headers["Authorization"] = "Bearer " + metaApiKey;
                break;

            case ProviderCredentialKind.ChatGptSubscription:
                var chatGpt = await Credentials.GetChatGptTokenAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                ChatGptAuthentication.ApplyHeaders(headers, chatGpt.AccessToken, chatGpt.AccountId);
                break;
        }

        foreach (var (name, value) in definition.Headers)
        {
            headers[name] = value;
        }

        // The protocol, not the preset, decides where a listing hangs: Anthropic's lives under the version segment its
        // messages path uses, which the base deliberately does not carry.
        var (root, query) = ProviderModelListing.ListRequest(definition.Protocol, baseUrl);
        return (root, query, headers);
    }

    /// <summary>The sentence the shell shows when nothing is connected, resolved in the current language.</summary>
    public LocText StatusText => catalog.StatusText;

    public bool IsConfigured => catalog.IsConfigured;
}
