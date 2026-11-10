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

    /// <summary>Uses a sign-in (Claude subscription or GitHub Copilot) that has not happened yet.</summary>
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
            foreach (var (modelId, metadata) in list.Metadata)
            {
                models[modelId] = metadata;
            }

            // Keep the ids even when the server reported no metadata for them, so "N models" matches the fetched list.
            foreach (var id in list.Ids)
            {
                models.TryAdd(id, ModelMetadata.Unknown);
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

    /// <summary>The API key from the secret store, or the environment variable; <c>null</c> when neither has one.</summary>
    public string? ResolveApiKey()
    {
        var stored = secrets.Get(Definition.SecretReference);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (string.IsNullOrWhiteSpace(Definition.EnvironmentVariable))
        {
            return null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(Definition.EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    /// <summary>Where the key would come from, for the row's summary; <c>null</c> when the provider uses a sign-in.</summary>
    public bool KeyComesFromEnvironment =>
        string.IsNullOrWhiteSpace(secrets.Get(Definition.SecretReference)) &&
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
                return new CredentialedChatProvider(
                    Id,
                    Definition.CredentialKind,
                    Definition.BaseUrl,
                    credentials,
                    (key, baseUrl, style, preamble) => ChatProviderPipeline.Build(
                        ProviderFactory.Create(BuildConfig(key, baseUrl, style, preamble), http),
                        new RetryMiddleware()));

            default:
                return Missing(ProviderStrings.NoCredentialText());
        }

        IChatProvider Build(string apiKey) => ChatProviderPipeline.Build(
            ProviderFactory.Create(
                BuildConfig(apiKey, Definition.BaseUrl, ProviderAuthStyle.Default, preamble: null),
                http),
            new RetryMiddleware());

        IChatProvider Missing(LocText reason) => new MissingCredentialProvider(Id, reason);
    }

    /// <summary>
    /// The wire config this definition produces once a credential is known. Shared with the credentialed provider so the
    /// headers, protocol and preamble are decided in one place.
    /// </summary>
    internal ProviderConfig BuildConfig(
        string apiKey,
        string baseUrl,
        ProviderAuthStyle style,
        string? preamble)
    {
        Dictionary<string, string>? headers = null;
        if (Definition.Headers.Count > 0)
        {
            headers = new Dictionary<string, string>(Definition.Headers, StringComparer.OrdinalIgnoreCase);
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
            Definition.Protocol == ProviderProtocol.Anthropic
                ? ProviderFactory.AnthropicKind
                : ProviderFactory.OpenAiCompatibleKind,
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
        ProviderModelCache cache)
    {
        Providers = providers;
        Default = defaultProvider;
        StatusText = status;
        this.cache = cache;
    }

    /// <summary>The connected providers in stored order.</summary>
    public IReadOnlyList<ConnectedProvider> Providers { get; }

    /// <summary>The provider a new session starts on; <c>null</c> only when the list is empty.</summary>
    public ConnectedProvider? Default { get; }

    /// <summary>Why there is no provider (or a summary of the default one), resolved in the current UI language.</summary>
    public LocText StatusText { get; }

    public bool IsConfigured => Default is not null;

    public ProviderModelCache Models => cache;

    public static ProviderCatalog Empty(LocText status) =>
        new([], null, status, new ProviderModelCache());

    public static ProviderCatalog Create(
        IReadOnlyList<ProviderDefinition> definitions,
        string? defaultId,
        ProviderSecretStore secrets,
        ProviderCredentials credentials,
        ProviderModelCache? cache = null)
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
        return new ProviderCatalog(providers, defaultProvider, status, sharedCache);
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
    /// Metadata for a model without naming a provider: the default provider's chain first, then the first provider that
    /// serves it. The session readout only has a model name, and a name is unique enough for a display estimate.
    /// </summary>
    public ModelMetadata MetadataFor(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return ModelMetadata.Unknown;
        }

        if (Default is { } defaultProvider)
        {
            var resolved = defaultProvider.MetadataFor(modelId);
            if (resolved.IsKnown)
            {
                return resolved;
            }
        }

        foreach (var provider in Providers)
        {
            var resolved = provider.MetadataFor(modelId);
            if (resolved.IsKnown)
            {
                return resolved;
            }
        }

        return ModelMetadataResolver.Resolve(modelId, providerMetadata: null, userOverride: null);
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
