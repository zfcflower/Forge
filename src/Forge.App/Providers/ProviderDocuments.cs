using System.Text.Json;
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
/// The model-metadata settings that are not per provider: the global context window and the catalog address the daily
/// refresh reads. The fetched catalog itself is not here — it is a cache of a network answer (<c>model-catalog.json</c>
/// under the data root), edited through <c>JsonNode</c> so unknown fields survive.
/// </summary>
internal sealed class ModelCatalogSectionDocument
{
    /// <summary>
    /// The context window every model uses unless it has its own override; absent means "use the catalog value". It is
    /// kept as the raw JSON value because the property has two shapes: a number is an absolute count, a string such as
    /// <c>"50%"</c> is a share of each model's own window (<see cref="ContextWindowDefault"/> owns both meanings). It may
    /// be smaller than the catalog window (to save cost or quota) or larger where the provider allows it.
    /// </summary>
    [JsonPropertyName("defaultContextWindow")]
    public JsonElement? DefaultContextWindow { get; set; }

    /// <summary>
    /// Where the daily refresh fetches the catalog from; absent means <see cref="Forge.Core.Providers.ModelCatalogFetcher.DefaultSourceUrl"/>.
    /// A mirror or an internal proxy works as long as it serves the same JSON shape.
    /// </summary>
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }
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

    /// <summary><c>openai-compatible</c>, <c>anthropic</c> or <c>openai-responses</c>.</summary>
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
/// <see cref="EnvironmentVariable"/>; the sign-in kinds carry no secret reference at all, because their bundles live in the
/// encrypted secret store keyed by provider: <c>claude-subscription</c> reuses the local Claude Code login,
/// <c>github-copilot</c> the GitHub device-flow login the app stored, <c>kimi-code</c>, <c>xai-subscription</c>,
/// <c>meta-subscription</c> and <c>chatgpt-subscription</c> the device-code logins the app stored.
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

/// <summary>
/// A Kimi For Coding sign-in as the secret store holds it: one JSON value under the reference
/// <c>kimi:&lt;providerId&gt;</c>, encrypted at rest like any pasted key. Every field is a credential, so nothing here is ever
/// written to <c>settings.json</c> or logged.
/// </summary>
internal sealed class KimiTokenDocument
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>The rotating refresh token; using it makes the stored one stale, so the store is rewritten on every refresh.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>Absolute expiry of the access token, so a restart keeps whatever lifetime is left.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// An xAI sign-in as the secret store holds it: one JSON value under the reference <c>xai:&lt;providerId&gt;</c>, encrypted at
/// rest like any pasted key. Every field is a credential, so nothing here is ever written to <c>settings.json</c> or logged.
/// </summary>
internal sealed class XAiTokenDocument
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>The refresh token; a refresh that rotates it rewrites the store, and one that omits it keeps this value.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>Absolute expiry of the access token, so a restart keeps whatever lifetime is left.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// A Meta sign-in as the secret store holds it: one JSON value under the reference <c>meta:&lt;providerId&gt;</c>, encrypted
/// at rest like any pasted key. The minted key is what the model requests use; the identity token is kept beside it so an
/// expired key is re-minted without asking the user to sign in again. Nothing here is ever written to <c>settings.json</c>.
/// </summary>
internal sealed class MetaTokenDocument
{
    /// <summary>The API key minted for this sign-in; from then on it is the bearer token every request carries.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>The OIDC identity token the key was minted from; re-minting uses it instead of a new device login.</summary>
    [JsonPropertyName("identityToken")]
    public string? IdentityToken { get; set; }

    /// <summary>Absolute expiry of the minted key, so a restart keeps whatever lifetime is left.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// A ChatGPT plan sign-in as the secret store holds it: one JSON value under the reference <c>chatgpt:&lt;providerId&gt;</c>,
/// encrypted at rest like any pasted key. The account id is not a secret but it belongs to the credential bundle — every
/// request has to name it — so it is stored here and never in <c>settings.json</c>.
/// </summary>
internal sealed class ChatGptTokenDocument
{
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    /// <summary>The refresh token; using it rotates the stored one, so the store is rewritten on every refresh.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    /// <summary>The ChatGPT account the plan is drawn from; read from the access token's <c>chatgpt_account_id</c> claim.</summary>
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    /// <summary>Absolute expiry of the access token, so a restart keeps whatever lifetime is left.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }
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
[JsonSerializable(typeof(KimiTokenDocument))]
[JsonSerializable(typeof(XAiTokenDocument))]
[JsonSerializable(typeof(MetaTokenDocument))]
[JsonSerializable(typeof(ChatGptTokenDocument))]
internal sealed partial class ProvidersJsonContext : JsonSerializerContext
{
}
