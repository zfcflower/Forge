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

    public ConnectedProvider(
        ProviderDefinition definition,
        ProviderSecretStore secrets,
        ProviderModelCache cache,
        ProviderCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        this.secrets = secrets;
        this.cache = cache;
        this.credentials = credentials;
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
    /// The metadata chain for one model: built-in table → what this provider reported → the user's override. A model the
    /// provider does not serve still resolves through the table, because a session can be resumed with a model that was
    /// removed from the list and its window is still worth showing.
    /// </summary>
    public ModelMetadata MetadataFor(string? modelId) =>
        ModelMetadataResolver.Resolve(
            modelId,
            cache.Get(Id, modelId),
            modelId is not null && Definition.Overrides.TryGetValue(modelId, out var overridden) ? overridden : null);

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
/// Every connected provider plus the default one. Immutable: editing the list produces a new catalogue, which the
/// composition root swaps in; a running session keeps the instance it captured, so a half-saved edit cannot change the
/// provider under an in-flight turn.
/// </summary>
internal sealed class ProviderCatalog
{
    private readonly ProviderModelCache cache;

    private ProviderCatalog(
        IReadOnlyList<ConnectedProvider> providers,
        ConnectedProvider? defaultProvider,
        LocText status,
        ProviderModelCache cache,
        int? defaultContextWindow)
    {
        Providers = providers;
        Default = defaultProvider;
        StatusText = status;
        this.cache = cache;
        DefaultContextWindow = defaultContextWindow is > 0 ? defaultContextWindow : null;
    }

    /// <summary>The connected providers in stored order.</summary>
    public IReadOnlyList<ConnectedProvider> Providers { get; }

    /// <summary>The provider a new session starts on; <c>null</c> only when the list is empty.</summary>
    public ConnectedProvider? Default { get; }

    /// <summary>Why there is no provider (or a summary of the default one), resolved in the current UI language.</summary>
    public LocText StatusText { get; }

    public bool IsConfigured => Default is not null;

    public ProviderModelCache Models => cache;

    /// <summary>
    /// The context window every model uses when it has no per-model override, or <c>null</c> to use the catalog value.
    /// It sits above the catalog (and what the provider reports) and below a per-model override, matching the reference
    /// product's "default uses the catalog limit, a global value overrides it, each model can still differ".
    /// </summary>
    public int? DefaultContextWindow { get; }

    public static ProviderCatalog Empty(LocText status) =>
        new([], null, status, new ProviderModelCache(), defaultContextWindow: null);

    public static ProviderCatalog Create(
        IReadOnlyList<ProviderDefinition> definitions,
        string? defaultId,
        ProviderSecretStore secrets,
        ProviderCredentials credentials,
        ProviderModelCache? cache = null,
        int? defaultContextWindow = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentials);

        var sharedCache = cache ?? new ProviderModelCache();
        var sharedCredentials = credentials;
        var providers = new List<ConnectedProvider>(definitions.Count);
        foreach (var definition in definitions)
        {
            providers.Add(new ConnectedProvider(definition, secrets, sharedCache, sharedCredentials));
        }

        var defaultProvider = providers.FirstOrDefault(provider =>
                                  string.Equals(provider.Id, defaultId, StringComparison.Ordinal)) ??
                              providers.FirstOrDefault();
        var status = defaultProvider is null
            ? ProviderStrings.SectionMissingText()
            : ProviderStrings.StoreReady(defaultProvider.DisplayName + " · " + defaultProvider.DefaultModel);
        return new ProviderCatalog(providers, defaultProvider, status, sharedCache, defaultContextWindow);
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

        var table = ModelMetadataResolver.Resolve(modelId, providerMetadata: null, userOverride: null);
        return DefaultContextWindow is { } window ? table with { ContextWindow = window } : table;
    }

    /// <summary>
    /// Applies the global context window over the resolved metadata. A per-model override that names a window wins; one that
    /// only carries prices does not, so setting prices does not silently drop the global limit.
    /// </summary>
    private ModelMetadata WithDefaultWindow(ConnectedProvider provider, ModelMetadata resolved, string modelId)
    {
        if (DefaultContextWindow is not { } window)
        {
            return resolved;
        }

        var overriddenWindow = provider.Definition.Overrides.TryGetValue(modelId, out var overridden)
            ? overridden.ContextWindow
            : null;
        return overriddenWindow is > 0 ? resolved : resolved with { ContextWindow = window };
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
