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

    /// <summary>Anthropic message-breakpoint cache TTL, <c>"5m"</c> or <c>"1h"</c>; null = the provider default.</summary>
    public string? PromptCacheTtl { get; init; }

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
        Protocol = Protocol == ProviderProtocol.Anthropic ? "anthropic" : "openai-compatible",
        BaseUrl = BaseUrl,
        Auth = new ProviderAuthDocument
        {
            Kind = CredentialKind switch
            {
                ProviderCredentialKind.ApiKey => "api-key",
                ProviderCredentialKind.ClaudeSubscription => "claude-subscription",
                ProviderCredentialKind.GitHubCopilot => "github-copilot",
                _ => "none",
            },
            EnvironmentVariable = EnvironmentVariable,
            SecretReference = SecretReference,
        },
        Models = [.. Models],
        DefaultModel = DefaultModel,
        Thinking = Thinking switch
        {
            ThinkingEffort.Low => "low",
            ThinkingEffort.Medium => "medium",
            ThinkingEffort.High => "high",
            _ => "off",
        },
        MaxOutputTokens = MaxOutputTokens,
        ModelMeta = Overrides.Count == 0 ? null : ToMetadataDocument(Overrides),
        SendPromptCacheKey = SendPromptCacheKey ? true : null,
        PromptCacheTtl = PromptCacheTtl,
        Headers = Headers.Count == 0 ? null : new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>A fresh entry for one preset, with the preset's protocol, URL, environment variable and seed models.</summary>
    public static ProviderDefinition FromPreset(ProviderPreset preset, string id, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var kind = preset.Auth.HasFlag(ProviderAuthOptions.ClaudeSubscription)
            ? ProviderCredentialKind.ClaudeSubscription
            : preset.Auth.HasFlag(ProviderAuthOptions.GitHubCopilot)
                ? ProviderCredentialKind.GitHubCopilot
                : ProviderCredentialKind.ApiKey;
        var models = preset.SeedModels.Count > 0 ? preset.SeedModels : [];
        return new ProviderDefinition
        {
            Id = id,
            PresetId = preset.Id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? preset.DisplayName : displayName.Trim(),
            Protocol = preset.Protocol,
            BaseUrl = preset.BaseUrl,
            CredentialKind = kind,
            EnvironmentVariable = preset.EnvironmentVariable,
            SecretReference = null,
            Models = models,
            DefaultModel = models.Count > 0 ? models[0] : string.Empty,
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
        };
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
            "anthropic" => ProviderProtocol.Anthropic,
            "openai-compatible" or "openai" or "" => preset.Protocol,
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
            "none" => ProviderCredentialKind.None,
            _ => preset.Auth switch
            {
                var auth when auth.HasFlag(ProviderAuthOptions.ClaudeSubscription) => ProviderCredentialKind.ClaudeSubscription,
                var auth when auth.HasFlag(ProviderAuthOptions.GitHubCopilot) => ProviderCredentialKind.GitHubCopilot,
                _ => ProviderCredentialKind.ApiKey,
            },
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
            Thinking = (entry.Thinking ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "low" => ThinkingEffort.Low,
                "medium" => ThinkingEffort.Medium,
                "high" => ThinkingEffort.High,
                _ => ThinkingEffort.Off,
            },
            MaxOutputTokens = entry.MaxOutputTokens is > 0 ? entry.MaxOutputTokens : null,
            Overrides = FromMetadataDocument(entry.ModelMeta),
            Headers = headers,
            SendPromptCacheKey = entry.SendPromptCacheKey ?? false,
            PromptCacheTtl = (entry.PromptCacheTtl ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "5m" => "5m",
                "1h" => "1h",
                _ => null,
            },
        };
    }

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
