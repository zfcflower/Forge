using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;
using Forge.Core.Providers.Internal;

namespace Forge.App.Providers;

/// <summary>The four things a provider row can say about itself.</summary>
internal enum ProviderStatusKind
{
    /// <summary>Has a usable credential right now.</summary>
    Ready,

    /// <summary>Will authenticate with an API key, but no key is present.</summary>
    NeedsKey,

    /// <summary>Uses a sign-in (Claude subscription, GitHub Copilot, Kimi For Coding, xAI, Meta or ChatGPT) that has not happened yet.</summary>
    NeedsSignIn,

    /// <summary>Configured, but something is wrong (no URL, an unreadable credential file).</summary>
    Error,
}

internal sealed record ProviderStatus(ProviderStatusKind Kind, LocText Detail)
{
    public static ProviderStatus Ready(LocText detail) => new(ProviderStatusKind.Ready, detail);

    public static ProviderStatus NeedsKey(LocText detail) => new(ProviderStatusKind.NeedsKey, detail);

    public static ProviderStatus NeedsSignIn(LocText detail) => new(ProviderStatusKind.NeedsSignIn, detail);

    public static ProviderStatus Error(LocText detail) => new(ProviderStatusKind.Error, detail);
}

/// <summary>
/// Metadata the provider reported for its own models after a <c>/models</c> refresh, kept in memory: it is a cache of a
/// network answer, not configuration, so it never reaches <c>settings.json</c>. Keys are provider id + model id because two
/// providers can serve the same model name with different windows.
/// </summary>
internal sealed class ProviderModelCache
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, ModelMetadata>> byProvider = new(StringComparer.Ordinal);
    private readonly string? path;
    private readonly Task loaded;

    /// <summary>
    /// A cache over one provider's <c>/models</c> answers. With a <paramref name="path"/> the answers survive a restart:
    /// the file is read on the pool at construction and rewritten after each refresh, so prices a provider reports are
    /// still there tomorrow instead of falling back to the table until somebody presses refresh. Without a path (tests,
    /// the transient editor provider) it stays in memory.
    /// </summary>
    public ProviderModelCache(string? path = null)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : path;
        loaded = this.path is null ? Task.CompletedTask : Task.Run(LoadFromDisk);
    }

    /// <summary>
    /// Completes when the file has been read (immediately for a memory-only cache). Readers that must not answer from a
    /// half-filled cache wait on it; <see cref="ProviderServices"/> uses it to drop its memo once the file has landed.
    /// </summary>
    public Task Loaded => loaded;

    public ModelMetadata? Get(string providerId, string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        lock (gate)
        {
            return byProvider.TryGetValue(providerId, out var models) && models.TryGetValue(modelId, out var metadata)
                ? metadata
                : null;
        }
    }

    public IReadOnlyList<string>? ModelsOf(string providerId)
    {
        lock (gate)
        {
            return byProvider.TryGetValue(providerId, out var models) ? [.. models.Keys] : null;
        }
    }

    public void Set(string providerId, ProviderModelList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        lock (gate)
        {
            var models = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
            // Server order first: the menu must read the way the provider listed it, metadata or not.
            foreach (var id in list.Ids)
            {
                models[id] = ModelMetadata.Unknown;
            }

            foreach (var (modelId, metadata) in list.Metadata)
            {
                models[modelId] = metadata;
            }

            byProvider[providerId] = models;
        }
    }

    private void LoadFromDisk()
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (!ProviderModelsFile.TryRead(File.ReadAllBytes(path), out var lists, out var error))
            {
                System.Diagnostics.Trace.WriteLine("[providers] " + error);
                return;
            }

            lock (gate)
            {
                // A refresh that already answered wins over the file: it is the newer answer.
                foreach (var (providerId, list) in lists)
                {
                    if (byProvider.ContainsKey(providerId))
                    {
                        continue;
                    }

                    var models = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
                    foreach (var id in list.Ids)
                    {
                        models[id] = list.Metadata.GetValueOrDefault(id) ?? ModelMetadata.Unknown;
                    }

                    byProvider[providerId] = models;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("[providers] model cache read failed: " + exception.Message);
        }
    }

    /// <summary>
    /// Writes the cache off the caller's thread. Awaiting it is part of a model-list refresh, so "the refresh finished" means
    /// "the file is on disk": a fire-and-forget write used to outlive the call and race anything that removed the data
    /// directory (a test, a profile reset). A failed write only costs the persistence, never the in-memory answers.
    /// </summary>
    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.Run(SaveNow, cancellationToken);

    /// <summary>
    /// Writes the cache on the calling thread. Used by <see cref="SaveAsync"/> on the pool, and by a caller that must have
    /// the file before continuing.
    /// </summary>
    internal void SaveNow()
    {
        if (path is null)
        {
            return;
        }

        // A save that overtakes the load would publish only what this process has fetched so far and drop every other
        // provider's entries from the file. Waiting for the read (a few milliseconds, on the caller's pool thread) keeps the
        // file a superset instead of a truncation.
        if (!loaded.IsCompleted)
        {
            try
            {
                loaded.GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("[providers] model cache load failed: " + exception.Message);
            }
        }

        try
        {
            Dictionary<string, ProviderModelList> snapshot;
            lock (gate)
            {
                snapshot = new Dictionary<string, ProviderModelList>(byProvider.Count, StringComparer.Ordinal);
                foreach (var (providerId, models) in byProvider)
                {
                    var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
                    foreach (var (modelId, entry) in models)
                    {
                        if (entry.IsKnown)
                        {
                            metadata[modelId] = entry;
                        }
                    }

                    snapshot[providerId] = new ProviderModelList([.. models.Keys], metadata);
                }
            }

            Forge.IO.AtomicFile.WriteAllBytes(path, ProviderModelsFile.Write(snapshot));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("[providers] model cache write failed: " + exception.Message);
        }
    }

    /// <summary>Reads the file on the calling thread (construction reads on the pool).</summary>
    internal void LoadNow() => LoadFromDisk();
}

/// <summary>
/// One provider as the rest of the app uses it: its stored definition, the credential state resolved from the secret store,
/// the environment and (for sign-ins) the local login, and a factory that produces an <see cref="IChatProvider"/>. The
/// status is cheap to read and is re-resolved when the settings page asks, so a row does not go stale after a key is pasted.
/// </summary>
internal sealed class ConnectedProvider
{
    private readonly ProviderSecretStore secrets;
    private readonly ProviderModelCache cache;
    private readonly ProviderCredentials credentials;
    private readonly ModelCatalogStore? modelCatalog;
    private readonly ModelMetadataMemo? memo;

    public ConnectedProvider(
        ProviderDefinition definition,
        ProviderSecretStore secrets,
        ProviderModelCache cache,
        ProviderCredentials credentials,
        ModelCatalogStore? modelCatalog = null,
        ModelMetadataMemo? memo = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        this.secrets = secrets;
        this.cache = cache;
        this.credentials = credentials;
        this.modelCatalog = modelCatalog;
        this.memo = memo;
    }

    public ProviderDefinition Definition { get; }

    public string Id => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public string DefaultModel => Definition.DefaultModel;

    public ProviderProtocol Protocol => Definition.Protocol;

    public ProviderCredentialKind CredentialKind => Definition.CredentialKind;

    /// <summary>The models the menu offers: what the provider was configured with, or what its last refresh returned.</summary>
    public IReadOnlyList<string> Models
    {
        get
        {
            var discovered = cache.ModelsOf(Id);
            return discovered is { Count: > 0 } ? discovered : Definition.Models;
        }
    }

    /// <summary>
    /// The API key: the environment variable wins over the stored secret, so rotating a key through the environment takes
    /// effect without editing the app, and an exported variable cannot be shadowed by an old pasted key.
    /// </summary>
    public string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(Definition.EnvironmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(Definition.EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        var stored = secrets.Get(Definition.SecretReference);
        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    /// <summary>True when the key comes from the environment, which is what the row's summary says.</summary>
    public bool KeyComesFromEnvironment =>
        !string.IsNullOrWhiteSpace(Definition.EnvironmentVariable) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Definition.EnvironmentVariable));

    public string? EnvironmentVariable => Definition.EnvironmentVariable;

    public bool HasStoredKey => secrets.Has(Definition.SecretReference);

    /// <summary>
    /// The status right now. Only local state is read — no network: a provider whose token needs refreshing still counts as
    /// connected, because the refresh happens on the first turn or when the user asks for it.
    /// </summary>
    public ProviderStatus Status
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Definition.BaseUrl))
            {
                return ProviderStatus.Error(ProviderStrings.NoBaseUrlText());
            }

            return Definition.CredentialKind switch
            {
                ProviderCredentialKind.ApiKey => ResolveApiKey() is { Length: > 0 }
                    ? ProviderStatus.Ready(KeyComesFromEnvironment
                        ? ProviderStrings.ReadyEnvironmentText(Definition.EnvironmentVariable!)
                        : ProviderStrings.StoredKeyReadyText())
                    : ProviderStatus.NeedsKey(string.IsNullOrWhiteSpace(Definition.EnvironmentVariable)
                        ? ProviderStrings.NeedsKeyNoVariableText()
                        : ProviderStrings.NeedsKeyVariableText(Definition.EnvironmentVariable!)),
                ProviderCredentialKind.ClaudeSubscription => credentials.HasClaudeLogin()
                    ? ProviderStatus.Ready(credentials.ClaudeLoginSummary())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.ClaudeSignInRequiredText()),
                ProviderCredentialKind.GitHubCopilot => credentials.HasCopilotLogin(Id)
                    ? ProviderStatus.Ready(ProviderStrings.CopilotSignedInText())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.CopilotSignInRequiredText()),
                ProviderCredentialKind.KimiCode => credentials.HasKimiLogin(Id)
                    ? ProviderStatus.Ready(ProviderStrings.KimiSignedInText())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.KimiSignInRequiredText()),
                ProviderCredentialKind.XAiSubscription => credentials.HasXAiLogin(Id)
                    ? ProviderStatus.Ready(ProviderStrings.XAiSignedInText())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.XAiSignInRequiredText()),
                ProviderCredentialKind.MetaSubscription => credentials.HasMetaLogin(Id)
                    ? ProviderStatus.Ready(ProviderStrings.MetaSignedInText())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.MetaSignInRequiredText()),
                ProviderCredentialKind.ChatGptSubscription => credentials.HasChatGptLogin(Id)
                    ? ProviderStatus.Ready(ProviderStrings.ChatGptSignedInText())
                    : ProviderStatus.NeedsSignIn(ProviderStrings.ChatGptSignInRequiredText()),
                _ => ProviderStatus.Error(ProviderStrings.NoCredentialText()),
            };
        }
    }

    /// <summary>
    /// The metadata chain for one model: built-in table → the fetched catalog snapshot → what this provider reported →
    /// the user's override. A model the provider does not serve still resolves through the first two layers, because a
    /// session can be resumed with a model that was removed from the list and its window is still worth showing.
    /// </summary>
    public ModelMetadata MetadataFor(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return ModelMetadata.Unknown;
        }

        if (memo is not null && memo.TryGet(Id, modelId, out var cached))
        {
            return cached;
        }

        var metadata = ModelMetadataResolver.Resolve(
            modelId,
            modelCatalog?.View.Lookup(Definition.PresetId, modelId),
            cache.Get(Id, modelId),
            Definition.Overrides.TryGetValue(modelId, out var overridden) ? overridden : null);
        memo?.Set(Id, modelId, metadata);
        return metadata;
    }

    /// <summary>
    /// The reasoning steps this model accepts: the compiled table first, because it was checked against the providers, then
    /// the fetched catalog for a model the table does not know, then Off alone. A model nobody described never gets a step
    /// it may reject.
    /// </summary>
    public IReadOnlyList<ThinkingEffort> ThinkingStepsFor(string? modelId)
    {
        var known = ThinkingLevels.For(modelId);
        return known.Count > 1
            ? known
            : modelCatalog?.View.ReasoningStepsFor(Definition.PresetId, modelId) ?? known;
    }

    /// <summary>
    /// Builds the provider for one turn. A key provider is built immediately; a sign-in provider is built behind a
    /// <see cref="CredentialedChatProvider"/>, which resolves (and refreshes) its credential on the first stream read so a
    /// turn never blocks the UI thread on the token endpoint.
    /// </summary>
    public IChatProvider CreateProvider(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        switch (Definition.CredentialKind)
        {
            case ProviderCredentialKind.ApiKey:
                var key = ResolveApiKey();
                return key is { Length: > 0 }
                    ? Build(key)
                    : Missing(ProviderStrings.NeedsKeyNoVariableText());

            case ProviderCredentialKind.ClaudeSubscription:
            case ProviderCredentialKind.GitHubCopilot:
            case ProviderCredentialKind.KimiCode:
            case ProviderCredentialKind.XAiSubscription:
            case ProviderCredentialKind.MetaSubscription:
            case ProviderCredentialKind.ChatGptSubscription:
                return new CredentialedChatProvider(
                    Id,
                    Definition.CredentialKind,
                    Definition.BaseUrl,
                    credentials,
                    (key, baseUrl, style, preamble, extraHeaders) => ChatProviderPipeline.Build(
                        ProviderFactory.Create(BuildConfig(key, baseUrl, style, preamble, extraHeaders), http, Options),
                        new RetryMiddleware()));

            default:
                return Missing(ProviderStrings.NoCredentialText());
        }

        IChatProvider Build(string apiKey) => ChatProviderPipeline.Build(
            ProviderFactory.Create(
                BuildConfig(apiKey, Definition.BaseUrl, ProviderAuthStyle.Default, preamble: null),
                http,
                Options),
            new RetryMiddleware());

        IChatProvider Missing(LocText reason) => new MissingCredentialProvider(Id, reason);
    }

    /// <summary>The per-provider wire switches that are not part of the credential or the URL.</summary>
    internal ProviderOptions Options => new() { SendPromptCacheKey = Definition.SendPromptCacheKey };

    /// <summary>
    /// The wire config this definition produces once a credential is known. Shared with the credentialed provider so the
    /// headers, protocol and preamble are decided in one place. <paramref name="credentialHeaders"/> carries the headers that
    /// belong to the credential itself (a plan that has to be named on every request), which only its resolver can build.
    /// </summary>
    internal ProviderConfig BuildConfig(
        string apiKey,
        string baseUrl,
        ProviderAuthStyle style,
        string? preamble,
        IReadOnlyDictionary<string, string>? credentialHeaders = null)
    {
        Dictionary<string, string>? headers = null;
        if (Definition.Headers.Count > 0)
        {
            headers = new Dictionary<string, string>(Definition.Headers, StringComparer.OrdinalIgnoreCase);
        }

        // Headers that come with the credential itself: a ChatGPT plan has to be named on every request, and the caller that
        // resolved the token is the only one that knows the account id.
        if (credentialHeaders is { Count: > 0 })
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in credentialHeaders)
            {
                headers[name] = value;
            }
        }

        if (Definition.CredentialKind == ProviderCredentialKind.GitHubCopilot)
        {
            // GitHub binds the minted session token to the integration id it was exchanged under (HMAC), so every API
            // call has to name the same one; the editor version is the client identification its endpoint expects.
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["Copilot-Integration-Id"] = ProviderServices.CopilotIntegrationId;
            headers["Editor-Version"] = ProviderServices.CopilotEditorVersion;
            headers["Editor-Plugin-Version"] = ProviderServices.CopilotEditorVersion;
        }

        return new ProviderConfig(
            Id,
            // The protocol decides the wire format the Core factory builds; the fallback is Chat Completions, which is what a
            // vendor that only exposes one kind of endpoint speaks.
            Definition.Protocol switch
            {
                ProviderProtocol.Anthropic => ProviderFactory.AnthropicKind,
                ProviderProtocol.OpenAiResponses => ProviderFactory.OpenAiResponsesKind,
                _ => ProviderFactory.OpenAiCompatibleKind,
            },
            baseUrl,
            apiKey,
            headers,
            style,
            preamble);
    }

    public override string ToString() => "ConnectedProvider(" + Id + ", " + Definition.Protocol + ")";
}

/// <summary>A provider whose credential is missing: every turn fails with the sentence the settings row shows.</summary>
internal sealed class MissingCredentialProvider(string id, LocText reason) : IChatProvider
{
    public string Id => id;

    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct) =>
        throw new ProviderException(reason.Resolve(), retryable: false);
}

/// <summary>
/// The non-provider settings a catalogue is built with: the global context window (an absolute count or a share of each
/// model's own window), the catalog address, and the fetched catalog layer itself. Grouped so
/// <see cref="ProviderCatalog.Create"/> stays readable as providers grow.
/// </summary>
internal sealed record ProviderCatalogOptions(
    ContextWindowDefault DefaultContextWindow = default,
    string? ModelCatalogSourceUrl = null,
    ModelCatalogStore? ModelCatalog = null,
    ModelMetadataMemo? Memo = null);

/// <summary>
/// Every connected provider plus the default one. Immutable: editing the list produces a new catalogue, which the
/// composition root swaps in; a running session keeps the instance it captured, so a half-saved edit cannot change the
/// provider under an in-flight turn.
/// </summary>
internal sealed class ProviderCatalog
{
    private readonly ProviderModelCache cache;
    private readonly ModelCatalogStore? modelCatalog;

    private ProviderCatalog(
        IReadOnlyList<ConnectedProvider> providers,
        ConnectedProvider? defaultProvider,
        LocText status,
        ProviderModelCache cache,
        ProviderCatalogOptions? options = null)
    {
        Providers = providers;
        Default = defaultProvider;
        StatusText = status;
        this.cache = cache;
        modelCatalog = options?.ModelCatalog;
        DefaultContextWindow = options?.DefaultContextWindow ?? ContextWindowDefault.Empty;
        ModelCatalogSourceUrl = string.IsNullOrWhiteSpace(options?.ModelCatalogSourceUrl)
            ? ModelCatalogFetcher.DefaultSourceUrl
            : options.ModelCatalogSourceUrl!.Trim();
    }

    /// <summary>The connected providers in stored order.</summary>
    public IReadOnlyList<ConnectedProvider> Providers { get; }

    /// <summary>The provider a new session starts on; <c>null</c> only when the list is empty.</summary>
    public ConnectedProvider? Default { get; }

    /// <summary>Why there is no provider (or a summary of the default one), resolved in the current UI language.</summary>
    public LocText StatusText { get; }

    public bool IsConfigured => Default is not null;

    public ProviderModelCache Models => cache;

    /// <summary>The fetched catalog layer (null in tests that do not build one).</summary>
    public ModelCatalogStore? ModelCatalog => modelCatalog;

    /// <summary>
    /// The context window every model uses when it has no per-model override, or <see cref="ContextWindowDefault.Empty"/> to
    /// use each model's catalog value. An absolute count sits above the catalog (and what the provider reports) and below a
    /// per-model override, matching the reference product's "default uses the catalog limit, a global value overrides it,
    /// each model can still differ"; a share scales whatever window those layers already found.
    /// </summary>
    public ContextWindowDefault DefaultContextWindow { get; }

    /// <summary>The catalog address the daily refresh fetches; the default when the user never changed it.</summary>
    public string ModelCatalogSourceUrl { get; }

    /// <summary>
    /// No connected provider. The model catalog layer still rides along, so the same chain answers whether or not anything
    /// is configured (the settings page's model table reads it either way).
    /// </summary>
    public static ProviderCatalog Empty(LocText status, ProviderCatalogOptions? options = null) =>
        new([], null, status, new ProviderModelCache(), options);

    public static ProviderCatalog Create(
        IReadOnlyList<ProviderDefinition> definitions,
        string? defaultId,
        ProviderSecretStore secrets,
        ProviderCredentials credentials,
        ProviderModelCache? cache = null,
        ProviderCatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentials);

        var sharedCache = cache ?? new ProviderModelCache();
        var sharedCredentials = credentials;
        var providers = new List<ConnectedProvider>(definitions.Count);
        foreach (var definition in definitions)
        {
            providers.Add(new ConnectedProvider(
                definition,
                secrets,
                sharedCache,
                sharedCredentials,
                options?.ModelCatalog,
                options?.Memo));
        }

        var defaultProvider = providers.FirstOrDefault(provider =>
                                  string.Equals(provider.Id, defaultId, StringComparison.Ordinal)) ??
                              providers.FirstOrDefault();
        var status = defaultProvider is null
            ? ProviderStrings.SectionMissingText()
            : ProviderStrings.StoreReady(defaultProvider.DisplayName + " · " + defaultProvider.DefaultModel);
        return new ProviderCatalog(providers, defaultProvider, status, sharedCache, options);
    }

    /// <summary>The provider with this id, or <c>null</c>; a missing id means the caller wants the default.</summary>
    public ConnectedProvider? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }

        foreach (var provider in Providers)
        {
            if (string.Equals(provider.Id, id, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>The default model settings (provider + model + effort), or an empty model when nothing is configured.</summary>
    public ModelSettings DefaultModelSettings => Default?.Definition.DefaultModelSettings ?? new ModelSettings(string.Empty);

    /// <summary>
    /// Metadata for a model in the context of the provider that serves it. The session readout knows both, so the same
    /// model id on two providers is not priced with the wrong provider's prices; without a provider id the default one is
    /// asked first, then the first provider whose chain knows the model.
    /// </summary>
    public ModelMetadata MetadataFor(string? modelId, string? providerId = null)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return ModelMetadata.Unknown;
        }

        if (!string.IsNullOrWhiteSpace(providerId) && Find(providerId) is { } chosen)
        {
            return WithDefaultWindow(chosen, chosen.MetadataFor(modelId), modelId);
        }

        if (Default is { } defaultProvider)
        {
            var resolved = WithDefaultWindow(defaultProvider, defaultProvider.MetadataFor(modelId), modelId);
            if (resolved.IsKnown)
            {
                return resolved;
            }
        }

        foreach (var provider in Providers)
        {
            var resolved = WithDefaultWindow(provider, provider.MetadataFor(modelId), modelId);
            if (resolved.IsKnown)
            {
                return resolved;
            }
        }

        var table = ModelMetadataResolver.Resolve(modelId, modelCatalog?.View.Lookup(null, modelId), providerMetadata: null, userOverride: null);
        var effective = DefaultContextWindow.Resolve(table.ContextWindow);
        return effective == table.ContextWindow ? table : table with { ContextWindow = effective };
    }

    /// <summary>
    /// The reasoning steps for a model, in the context of the provider that serves it (a model id can be served by two
    /// providers with different step maps). Same order as <see cref="MetadataFor"/>: the provider that actually serves the
    /// model first, then the default one, then any provider that knows it, then the compiled table.
    /// </summary>
    public IReadOnlyList<ThinkingEffort> ThinkingStepsFor(string? modelId, string? providerId = null)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && Find(providerId) is { } chosen)
        {
            return chosen.ThinkingStepsFor(modelId);
        }

        if (Default is { } defaultProvider)
        {
            var steps = defaultProvider.ThinkingStepsFor(modelId);
            if (steps.Count > 1)
            {
                return steps;
            }
        }

        foreach (var provider in Providers)
        {
            var steps = provider.ThinkingStepsFor(modelId);
            if (steps.Count > 1)
            {
                return steps;
            }
        }

        return ThinkingLevels.For(modelId);
    }

    /// <summary>
    /// Applies the global context window over the resolved metadata. An absolute count wins over a per-model override that
    /// only carries prices (setting prices must not silently drop the global limit) and loses to one that names a window,
    /// exactly as before. A share scales the window the chain already found — catalog snapshot, provider report or a
    /// per-model override — and leaves the model unknown when none of them knows one.
    /// </summary>
    private ModelMetadata WithDefaultWindow(ConnectedProvider provider, ModelMetadata resolved, string modelId)
    {
        if (DefaultContextWindow.IsEmpty)
        {
            return resolved;
        }

        // An absolute count replaces the window outright, except where the user already named one for this model.
        if (DefaultContextWindow.Tokens is { } tokens)
        {
            var overriddenWindow = provider.Definition.Overrides.TryGetValue(modelId, out var overridden)
                ? overridden.ContextWindow
                : null;
            return overriddenWindow is > 0 || resolved.ContextWindow == tokens
                ? resolved
                : resolved with { ContextWindow = tokens };
        }

        var scaled = DefaultContextWindow.Resolve(resolved.ContextWindow);
        return scaled == resolved.ContextWindow ? resolved : resolved with { ContextWindow = scaled };
    }

    /// <summary>The provider behind a model request: its own <see cref="ModelSettings.ProviderId"/>, else the default.</summary>
    public IChatProvider CreateProvider(ModelSettings settings, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var provider = Find(settings.ProviderId);
        if (provider is null)
        {
            Trace.TraceWarning("[providers] unknown provider id in a model request; falling back to the default");
            provider = Default;
        }

        return provider?.CreateProvider(http) ?? MissingCredentialProviderForNoProvider();
    }

    private static IChatProvider MissingCredentialProviderForNoProvider() =>
        new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText());
}
