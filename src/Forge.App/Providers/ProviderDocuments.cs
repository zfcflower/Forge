using System.Text.Json.Serialization;

namespace Forge.App.Providers;

/// <summary>
/// The <c>settings.json</c> document. Serialized through a source-generated context so the AOT build never touches
/// reflection-based JSON. Only the sections the app reads are modelled; everything else in the file is preserved
/// verbatim by <see cref="ProviderStore"/>, which rewrites the file through <c>JsonNode</c> rather than re-serializing
/// this type.
/// </summary>
internal sealed class SettingsDocument
{
    /// <summary>The single-provider section of older builds. Read once for migration and never written again.</summary>
    [JsonPropertyName("provider")]
    public LegacyProviderSection? Provider { get; set; }

    [JsonPropertyName("providers")]
    public ProvidersSectionDocument? Providers { get; set; }

    [JsonPropertyName("modelCatalog")]
    public ModelCatalogSectionDocument? ModelCatalog { get; set; }
}

/// <summary>
/// The model-metadata settings that are not per provider. Only the global context window lives here today; a fetched catalog
/// snapshot (<c>sourceUrl</c>/<c>generatedAt</c>) will join it when that work starts, which is why the section is separate
/// from <c>providers</c> and is edited through <c>JsonNode</c> so unknown fields survive.
/// </summary>
internal sealed class ModelCatalogSectionDocument
{
    /// <summary>
    /// The context window every model uses unless it has its own override; absent means "use the catalog value". It may be
    /// smaller than the catalog window (to save cost or quota) or larger where the provider allows it.
    /// </summary>
    [JsonPropertyName("defaultContextWindow")]
    public int? DefaultContextWindow { get; set; }
}

/// <summary>
/// The pre-multi-provider configuration. Kept only so an existing file keeps working: on the first save the app writes
/// <c>providers</c> and drops this section.
/// </summary>
internal sealed class LegacyProviderSection
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("models")]
    public List<string>? Models { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("sendPromptCacheKey")]
    public bool? SendPromptCacheKey { get; set; }

    [JsonPropertyName("promptCacheTtl")]
    public string? PromptCacheTtl { get; set; }

    [JsonPropertyName("apiKeyEnv")]
    public string? ApiKeyEnv { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("modelMeta")]
    public Dictionary<string, ModelMetadataSection>? ModelMeta { get; set; }
}

/// <summary>The <c>providers</c> section: every connected provider plus which one a new session starts with.</summary>
internal sealed class ProvidersSectionDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>Id of the provider new sessions start on; absent or unknown falls back to the first item.</summary>
    [JsonPropertyName("defaultId")]
    public string? DefaultId { get; set; }

    [JsonPropertyName("items")]
    public List<ProviderEntryDocument>? Items { get; set; }
}

/// <summary>One connected provider as stored. Secrets are never here — only a reference into the secret store.</summary>
internal sealed class ProviderEntryDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The preset this entry was created from; unknown or missing means the entry is shown as a custom one.</summary>
    [JsonPropertyName("preset")]
    public string? Preset { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary><c>openai-compatible</c> or <c>anthropic</c>.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("auth")]
    public ProviderAuthDocument? Auth { get; set; }

    [JsonPropertyName("models")]
    public List<string>? Models { get; set; }

    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("modelMeta")]
    public Dictionary<string, ModelMetadataSection>? ModelMeta { get; set; }

    /// <summary>
    /// OpenAI-compatible endpoints: send <c>prompt_cache_key</c> (the session lineage id). Off by default because strict
    /// endpoints reject unknown fields; never sent to DeepSeek.
    /// </summary>
    [JsonPropertyName("sendPromptCacheKey")]
    public bool? SendPromptCacheKey { get; set; }

    /// <summary>Anthropic: message-breakpoint cache TTL, <c>"5m"</c> (default) or <c>"1h"</c>; config file only.</summary>
    [JsonPropertyName("promptCacheTtl")]
    public string? PromptCacheTtl { get; set; }

    /// <summary>Extra request headers for a gateway that needs them (never a credential; those live in <see cref="Auth"/>).</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// How this provider authenticates. <c>api-key</c> uses <see cref="SecretReference"/> when present and otherwise reads
/// <see cref="EnvironmentVariable"/>; <c>claude-subscription</c> reuses the local Claude Code login and
/// <c>github-copilot</c> the GitHub device-flow login the app stored.
/// </summary>
internal sealed class ProviderAuthDocument
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("envVar")]
    public string? EnvironmentVariable { get; set; }

    [JsonPropertyName("secretRef")]
    public string? SecretReference { get; set; }
}

/// <summary>
/// One entry of a provider's per-model metadata override. Prices are US dollars per one million tokens; an absent field
/// is unknown rather than zero.
/// </summary>
internal sealed class ModelMetadataSection
{
    [JsonPropertyName("contextWindow")]
    public int? ContextWindow { get; set; }

    [JsonPropertyName("input")]
    public double? Input { get; set; }

    [JsonPropertyName("output")]
    public double? Output { get; set; }

    [JsonPropertyName("cacheRead")]
    public double? CacheRead { get; set; }

    [JsonPropertyName("cacheWrite")]
    public double? CacheWrite { get; set; }
}

/// <summary>The encrypted credential store. Names, references and nonces are not secret; the payloads are.</summary>
internal sealed class SecretsDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("entries")]
    public Dictionary<string, SecretEntryDocument>? Entries { get; set; }
}

/// <summary>One AES-GCM payload: base64 nonce, tag and ciphertext.</summary>
internal sealed class SecretEntryDocument
{
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(ProvidersSectionDocument))]
[JsonSerializable(typeof(ProviderEntryDocument))]
[JsonSerializable(typeof(SecretsDocument))]
internal sealed partial class ProvidersJsonContext : JsonSerializerContext
{
}
