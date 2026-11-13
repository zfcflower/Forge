using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>How a connected provider gets its credential.</summary>
internal enum ProviderCredentialKind
{
    /// <summary>Nothing configured yet; the provider is listed but cannot run a turn.</summary>
    None,

    /// <summary>A key the user pasted (secret store) or an environment variable that holds one.</summary>
    ApiKey,

    /// <summary>The account already signed in to the local Claude Code CLI; usage draws on that subscription.</summary>
    ClaudeSubscription,

    /// <summary>GitHub device-code login, exchanged for a short-lived Copilot session token.</summary>
    GitHubCopilot,

    /// <summary>Kimi For Coding device-code login; the access token is a bearer token on the coding API.</summary>
    KimiCode,

    /// <summary>xAI device-code login; the access token is the bearer token the grok API takes.</summary>
    XAiSubscription,

    /// <summary>A Meta subscription rather than a key; its models are reachable through the Responses protocol only.</summary>
    MetaSubscription,

    /// <summary>A ChatGPT plan: the model list and the account header both come from the subscription.</summary>
    ChatGptSubscription,
}

/// <summary>
/// One connected provider, resolved from <c>settings.json</c> plus its preset. Immutable: editing a provider produces a
/// new definition, so a running session keeps the provider it started with until the next turn.
/// </summary>
internal sealed record ProviderDefinition
{
    public required string Id { get; init; }

    public required string PresetId { get; init; }

    public required string DisplayName { get; init; }

    public required ProviderProtocol Protocol { get; init; }

    public required string BaseUrl { get; init; }

    public required ProviderCredentialKind CredentialKind { get; init; }

    /// <summary>Name of the environment variable that may hold the key; read on every use, so exporting it works live.</summary>
    public string? EnvironmentVariable { get; init; }

    /// <summary>Reference into the secret store; <c>null</c> when the key only ever comes from the environment.</summary>
    public string? SecretReference { get; init; }

    /// <summary>The models this provider serves, in menu order; the first is the default when no other is chosen.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    public required string DefaultModel { get; init; }

    public ThinkingEffort Thinking { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>The user's per-model metadata override, the strongest source in the metadata chain.</summary>
    public required IReadOnlyDictionary<string, ModelMetadata> Overrides { get; init; }

    /// <summary>OpenAI-compatible: send <c>prompt_cache_key</c>; never for DeepSeek (the provider itself refuses it).</summary>
    public bool SendPromptCacheKey { get; init; }

    /// <summary>
    /// The prompt-cache lifetime to ask for — <c>"auto"</c> (default), <c>"5m"</c> or <c>"1h"</c>; null reads as <c>auto</c>.
    /// </summary>
    public string? PromptCacheTtl { get; init; }

    /// <summary>
    /// Whether this provider has a cache lifetime to choose at all: Anthropic takes breakpoints with a lifetime and OpenAI takes a
    /// retention, while every other vendor caches on its own terms with nothing to set. The settings row shows the control only here.
    /// </summary>
    public bool SupportsPromptCacheTtl =>
        Protocol == ProviderProtocol.Anthropic || Preset.CacheWiring is ProviderCacheWiring.OpenAi;

    /// <summary>Extra request headers for a gateway with its own scheme (never a credential).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ProviderPreset Preset => ProviderPresets.FindOrDefault(PresetId);

    public ModelSettings DefaultModelSettings => new(DefaultModel, Thinking, MaxOutputTokens, Id);

    /// <summary>
    /// The definition as stored. The secret reference is kept; the secret itself was never part of the definition.
    /// </summary>
    public ProviderEntryDocument ToDocument() => new()
    {
        Id = Id,
        Preset = PresetId,
        Name = DisplayName,
        Protocol = Protocol switch
        {
            ProviderProtocol.Anthropic => ProviderFactory.AnthropicKind,
            ProviderProtocol.OpenAiResponses => ProviderFactory.OpenAiResponsesKind,
            _ => ProviderFactory.OpenAiCompatibleKind,
        },
        BaseUrl = BaseUrl,
        Auth = new ProviderAuthDocument
        {
            Kind = CredentialKind switch
            {
                ProviderCredentialKind.ApiKey => "api-key",
                ProviderCredentialKind.ClaudeSubscription => "claude-subscription",
                ProviderCredentialKind.GitHubCopilot => "github-copilot",
                ProviderCredentialKind.KimiCode => "kimi-code",
                ProviderCredentialKind.XAiSubscription => "xai-subscription",
                ProviderCredentialKind.MetaSubscription => "meta-subscription",
                ProviderCredentialKind.ChatGptSubscription => "chatgpt-subscription",
                _ => "none",
            },
            EnvironmentVariable = EnvironmentVariable,
            SecretReference = SecretReference,
        },
        Models = [.. Models],
        DefaultModel = DefaultModel,
        Thinking = ThinkingEfforts.Name(Thinking),
        MaxOutputTokens = MaxOutputTokens,
        ModelMeta = Overrides.Count == 0 ? null : ToMetadataDocument(Overrides),
        SendPromptCacheKey = SendPromptCacheKey ? true : null,
        PromptCacheTtl = PromptCacheTtl,
        Headers = Headers.Count == 0 ? null : new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// The credential a fresh entry starts with when the caller does not name one: the first sign-in the preset offers, which
    /// is what makes a subscription the default path for a vendor that also takes a key (Anthropic, Kimi For Coding, Meta,
    /// xAI and OpenAI all work that way; GitHub Copilot's sign-in comes before its key for the same reason).
    /// </summary>
    private static ProviderCredentialKind PresetCredentialKind(ProviderAuthOptions auth) => auth switch
    {
        var value when value.HasFlag(ProviderAuthOptions.ClaudeSubscription) => ProviderCredentialKind.ClaudeSubscription,
        var value when value.HasFlag(ProviderAuthOptions.GitHubCopilot) => ProviderCredentialKind.GitHubCopilot,
        var value when value.HasFlag(ProviderAuthOptions.KimiCode) => ProviderCredentialKind.KimiCode,
        var value when value.HasFlag(ProviderAuthOptions.XAiSubscription) => ProviderCredentialKind.XAiSubscription,
        var value when value.HasFlag(ProviderAuthOptions.MetaSubscription) => ProviderCredentialKind.MetaSubscription,
        var value when value.HasFlag(ProviderAuthOptions.ChatGptSubscription) => ProviderCredentialKind.ChatGptSubscription,
        _ => ProviderCredentialKind.ApiKey,
    };

    /// <summary>
    /// True for the kinds that come from a sign-in rather than a key, which is what decides whether the preset's subscription
    /// endpoint applies. A preset that has none (Anthropic, Kimi For Coding, GitHub Copilot, xAI, Meta) is reached at the
    /// same address either way, so only OpenAI's plan actually moves.
    /// </summary>
    private static bool IsSubscription(ProviderCredentialKind kind) => kind is
        ProviderCredentialKind.ClaudeSubscription or
        ProviderCredentialKind.GitHubCopilot or
        ProviderCredentialKind.KimiCode or
        ProviderCredentialKind.XAiSubscription or
        ProviderCredentialKind.MetaSubscription or
        ProviderCredentialKind.ChatGptSubscription;

    /// <summary>
    /// A fresh entry for one preset. <paramref name="credentialKind"/> is the way in the caller means — the editor names a
    /// key, the login dialog names the sign-in it just completed — and it decides the endpoint as well as the credential: a
    /// subscription kind on a preset with a subscription endpoint takes that endpoint, its protocol and its seed models.
    /// Left <c>null</c> it follows the preset's own priority chain, so a caller that does not care keeps the old behaviour.
    /// </summary>
    public static ProviderDefinition FromPreset(
        ProviderPreset preset,
        string id,
        string? displayName = null,
        ProviderCredentialKind? credentialKind = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var kind = credentialKind ?? PresetCredentialKind(preset.Auth);
        var subscription = IsSubscription(kind) ? preset.Subscription : null;
        var seedModels = subscription is null ? preset.SeedModels : subscription.SeedModels;
        var models = seedModels.Count > 0 ? seedModels : [];
        return new ProviderDefinition
        {
            Id = id,
            PresetId = preset.Id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? preset.DisplayName : displayName.Trim(),
            Protocol = subscription?.Protocol ?? preset.Protocol,
            BaseUrl = subscription?.BaseUrl ?? preset.BaseUrl,
            CredentialKind = kind,
            EnvironmentVariable = preset.EnvironmentVariable,
            SecretReference = null,
            Models = models,
            DefaultModel = PreferredDefaultModel(preset.Id, models),
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// The model a fresh definition starts on. Anthropic's listing is newest-first across every family, so "the first id"
    /// would move a fresh Claude sign-in onto whichever family shipped last; its default is the newest Opus instead, which
    /// is where both official clients' <c>Default</c> row lands. Every other preset keeps the first id, because that is what
    /// its seeds and its listing order mean.
    /// </summary>
    /// <param name="presetId">The preset the definition was built from.</param>
    /// <param name="models">The models the definition starts with, in the order they should be offered.</param>
    public static string PreferredDefaultModel(string presetId, IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            return string.Empty;
        }

        if (!string.Equals(presetId, ProviderPresets.AnthropicId, StringComparison.Ordinal))
        {
            return models[0];
        }

        foreach (var model in models)
        {
            if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return models[0];
    }

    /// <summary>
    /// Reads one stored entry. A missing field falls back to the preset, an id is never empty, and a model list that
    /// disagrees with the chosen default puts the default first so the menu and the session agree.
    /// </summary>
    public static ProviderDefinition FromDocument(ProviderEntryDocument entry, string fallbackId)
    {
        var preset = ProviderPresets.FindOrDefault(entry.Preset);
        var id = string.IsNullOrWhiteSpace(entry.Id) ? fallbackId : entry.Id!.Trim();
        var protocol = (entry.Protocol ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ProviderFactory.AnthropicKind => ProviderProtocol.Anthropic,
            ProviderFactory.OpenAiResponsesKind => ProviderProtocol.OpenAiResponses,
            ProviderFactory.OpenAiCompatibleKind or "openai" or "" => preset.Protocol,
            _ => preset.Protocol,
        };

        var baseUrl = (entry.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0)
        {
            baseUrl = preset.BaseUrl;
        }

        var credentialKind = (entry.Auth?.Kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "api-key" or "apikey" or "env" or "secret" => ProviderCredentialKind.ApiKey,
            "claude-subscription" => ProviderCredentialKind.ClaudeSubscription,
            "github-copilot" => ProviderCredentialKind.GitHubCopilot,
            "kimi-code" => ProviderCredentialKind.KimiCode,
            "xai-subscription" => ProviderCredentialKind.XAiSubscription,
            "meta-subscription" => ProviderCredentialKind.MetaSubscription,
            "chatgpt-subscription" => ProviderCredentialKind.ChatGptSubscription,
            "none" => ProviderCredentialKind.None,
            _ => PresetCredentialKind(preset.Auth),
        };

        var models = new List<string>();
        foreach (var name in entry.Models ?? [])
        {
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > 0 && !models.Contains(trimmed, StringComparer.Ordinal))
            {
                models.Add(trimmed);
            }
        }

        if (models.Count == 0)
        {
            models.AddRange(preset.SeedModels);
        }

        var defaultModel = (entry.DefaultModel ?? string.Empty).Trim();
        if (defaultModel.Length == 0)
        {
            defaultModel = models.Count > 0 ? models[0] : string.Empty;
        }
        else if (!models.Contains(defaultModel, StringComparer.Ordinal))
        {
            models.Insert(0, defaultModel);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entry.Headers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
            {
                headers[name.Trim()] = value.Trim();
            }
        }

        return new ProviderDefinition
        {
            Id = id,
            PresetId = entry.Preset ?? preset.Id,
            DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? preset.DisplayName : entry.Name!.Trim(),
            Protocol = protocol,
            BaseUrl = baseUrl,
            CredentialKind = credentialKind,
            EnvironmentVariable = string.IsNullOrWhiteSpace(entry.Auth?.EnvironmentVariable)
                ? null
                : entry.Auth!.EnvironmentVariable!.Trim(),
            SecretReference = string.IsNullOrWhiteSpace(entry.Auth?.SecretReference)
                ? null
                : entry.Auth!.SecretReference!.Trim(),
            Models = models,
            DefaultModel = defaultModel,
            Thinking = ThinkingEfforts.TryParse(entry.Thinking, out var thinking) ? thinking : ThinkingEffort.Off,
            MaxOutputTokens = entry.MaxOutputTokens is > 0 ? entry.MaxOutputTokens : null,
            Overrides = FromMetadataDocument(entry.ModelMeta),
            Headers = headers,
            SendPromptCacheKey = entry.SendPromptCacheKey ?? false,
            PromptCacheTtl = ProviderDefinition.ParsePromptCacheTtl(entry.PromptCacheTtl),
        };
    }

    /// <summary>
    /// <c>promptCacheTtl</c>: <c>"auto"</c> (default — short lifetime until the session pauses, then the hour), <c>"5m"</c>,
    /// <c>"1h"</c>. Anything else reads as <c>auto</c>, which is the product default rather than a silent off switch.
    /// </summary>
    public static string? ParsePromptCacheTtl(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "5m" => "5m",
            "1h" => "1h",
            "auto" => "auto",
            _ => null,
        };

    /// <summary>
    /// The metadata overrides as resolved values: a window needs a positive number, and a price entry only counts when at
    /// least an input or an output price is present — a missing cache price is a genuine 0 for that path.
    /// </summary>
    internal static Dictionary<string, ModelMetadata> FromMetadataDocument(Dictionary<string, ModelMetadataSection>? overrides)
    {
        var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
        foreach (var (modelId, section) in overrides ?? [])
        {
            var id = (modelId ?? string.Empty).Trim();
            if (id.Length == 0 || section is null)
            {
                continue;
            }

            var window = section.ContextWindow is > 0 ? section.ContextWindow : null;
            var pricing = section.Input is not null || section.Output is not null
                ? new ModelPricing(section.Input ?? 0, section.Output ?? 0, section.CacheRead ?? 0, section.CacheWrite ?? 0)
                : null;
            var entry = new ModelMetadata(window, pricing);
            if (entry.IsKnown)
            {
                metadata[id] = entry;
            }
        }

        return metadata;
    }

    internal static Dictionary<string, ModelMetadataSection> ToMetadataDocument(IReadOnlyDictionary<string, ModelMetadata> overrides)
    {
        var document = new Dictionary<string, ModelMetadataSection>(StringComparer.Ordinal);
        foreach (var (modelId, metadata) in overrides)
        {
            if (!metadata.IsKnown)
            {
                continue;
            }

            document[modelId] = new ModelMetadataSection
            {
                ContextWindow = metadata.ContextWindow,
                Input = metadata.Pricing?.Input,
                Output = metadata.Pricing?.Output,
                CacheRead = metadata.Pricing?.CacheRead,
                CacheWrite = metadata.Pricing?.CacheWrite,
            };
        }

        return document;
    }
}
