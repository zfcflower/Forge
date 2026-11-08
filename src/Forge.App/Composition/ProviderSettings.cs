using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;

namespace Forge.App.Composition;

/// <summary>
/// The <c>settings.json</c> document. Serialized through a source-generated context so the AOT build never touches
/// reflection-based JSON.
/// </summary>
internal sealed class SettingsDocument
{
    [JsonPropertyName("provider")]
    public ProviderSection? Provider { get; set; }
}

internal sealed class ProviderSection
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    /// <summary>Other model ids the same endpoint serves; the composer's model picker offers them next to <see cref="Model"/>.</summary>
    [JsonPropertyName("models")]
    public List<string>? Models { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    /// <summary>Name of the environment variable holding the key. Preferred: the key never reaches this file.</summary>
    [JsonPropertyName("apiKeyEnv")]
    public string? ApiKeyEnv { get; set; }

    /// <summary>Literal key, for setups that cannot set environment variables. Never logged, never shown.</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Per-model metadata overrides (context window and prices), the strongest source in the metadata chain. A model that
    /// is not listed falls back to what the provider reports and then to the built-in table.
    /// </summary>
    [JsonPropertyName("modelMeta")]
    public Dictionary<string, ModelMetadataSection>? ModelMeta { get; set; }
}

/// <summary>
/// One entry of <see cref="ProviderSection.ModelMeta"/>. Prices are US dollars per one million tokens, the same unit the
/// built-in table and the session readout use; a price that is absent counts as not known, so a model with only a window
/// override does not get a fake $0.00.
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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocument))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The provider the app will use, or the reason there is none. The key itself is only held in a private field and is
/// never part of <see cref="ToString"/> or of any message the UI shows.
/// </summary>
internal sealed class ProviderSnapshot
{
    private static readonly HttpClient ModelListHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(20);
    private readonly string? apiKey;

    /// <summary>Per-model overrides from <c>provider.modelMeta</c>, empty when the section lists none.</summary>
    private readonly Dictionary<string, ModelMetadata> userMetadata;

    /// <summary>Metadata the provider reported for its own models; replaced whole after a model-list refresh.</summary>
    private IReadOnlyDictionary<string, ModelMetadata> discoveredMetadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);

    private ProviderSnapshot(
        string kind,
        string baseUrl,
        string model,
        ThinkingEffort thinking,
        int? maxOutputTokens,
        string? apiKey,
        string? apiKeyEnvironmentVariable,
        bool keyFromEnvironment,
        IReadOnlyList<string> models,
        Dictionary<string, ModelMetadata> userMetadata)
    {
        Kind = kind;
        Models = models;
        BaseUrl = baseUrl;
        Model = model;
        Thinking = thinking;
        MaxOutputTokens = maxOutputTokens;
        this.apiKey = apiKey;
        this.userMetadata = userMetadata;
        ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
        KeyFromEnvironment = keyFromEnvironment;
    }

    public string Kind { get; }

    public string BaseUrl { get; }

    public string Model { get; }

    public ThinkingEffort Thinking { get; }

    public int? MaxOutputTokens { get; }

    /// <summary>The configured <see cref="Model"/> first, then the extra <c>models</c> of the section, without duplicates.</summary>
    public IReadOnlyList<string> Models { get; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(apiKey);
    public bool SupportsModelDiscovery => string.Equals(Kind, ProviderFactory.OpenAiCompatibleKind, StringComparison.Ordinal);

    private string? ApiKeyEnvironmentVariable { get; }

    /// <summary>Where the key came from, for the settings panel; the key itself is never exposed.</summary>
    public bool KeyFromEnvironment { get; }

    public ModelSettings ModelSettings => new(Model, Thinking, MaxOutputTokens);

    public string Summary => Kind + " · " + Model;

    /// <summary>Builds the provider the agent loop streams from. The HttpClient must already have an infinite timeout.</summary>
    public IChatProvider CreateProvider(HttpClient httpClient) =>
        new RetryingChatProvider(
            ProviderFactory.Create(new ProviderConfig("forge", Kind, BaseUrl, apiKey), httpClient));

    public Task<IReadOnlyList<string>> FetchModelsAsync(CancellationToken cancellationToken) =>
        FetchModelsAsync(ModelListHttpClient, cancellationToken);

    internal async Task<IReadOnlyList<string>> FetchModelsAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!SupportsModelDiscovery)
        {
            throw new NotSupportedException("Model discovery is only supported for OpenAI-compatible providers.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var setup = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
                ? "Configure provider.apiKey or provider.apiKeyEnv"
                : "Set the '" + ApiKeyEnvironmentVariable + "' environment variable";
            throw new InvalidOperationException(setup + " before refreshing models.");
        }

        var baseUri = new Uri(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ModelListTimeout);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The model list response must contain a 'data' array.");
            }

            var models = new List<string>();
            var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    var name = id.GetString()!.Trim();
                    if (!models.Contains(name, StringComparer.Ordinal))
                    {
                        models.Add(name);
                    }

                    if (ReadMetadata(item) is { IsKnown: true } reported)
                    {
                        metadata[name] = reported;
                    }
                }
            }

            discoveredMetadata = metadata;
            return models;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while fetching the provider model list.", exception);
        }
    }

    /// <summary>
    /// Metadata a provider reports for one model. Only fields that actually exist are read, because the shape differs per
    /// gateway: a context window as <c>context_length</c>, <c>max_context_length</c> or <c>top_provider.context_length</c>,
    /// and per-token prices under <c>pricing</c> (OpenRouter's shape), which are scaled to the per-million unit the rest of
    /// the app uses. A model whose response carries none of this simply reports nothing.
    /// </summary>
    private static ModelMetadata? ReadMetadata(JsonElement item)
    {
        var window = PositiveInt(item, "context_length")
            ?? PositiveInt(item, "max_context_length")
            ?? (item.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object
                ? PositiveInt(top, "context_length")
                : null);

        ModelPricing? pricing = null;
        if (item.TryGetProperty("pricing", out var prices) && prices.ValueKind == JsonValueKind.Object)
        {
            var input = PerMillion(prices, "prompt");
            var output = PerMillion(prices, "completion");
            if (input is not null || output is not null)
            {
                pricing = new ModelPricing(
                    input ?? 0,
                    output ?? 0,
                    PerMillion(prices, "input_cache_read") ?? 0,
                    PerMillion(prices, "input_cache_write") ?? 0);
            }
        }

        if (window is null && pricing is null)
        {
            return null;
        }

        return new ModelMetadata(window, pricing);
    }

    private static int? PositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) && number > 0 => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) && parsed > 0 => parsed,
            _ => null,
        };
    }

    /// <summary>A per-token price from the model list, expressed per one million tokens.</summary>
    private static double? PerMillion(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        var perToken = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => double.NaN,
        };

        return double.IsFinite(perToken) && perToken >= 0 ? perToken * 1_000_000d : null;
    }

    public override string ToString() => "ProviderSnapshot(" + Kind + ", " + BaseUrl + ", " + Model + ")";

    internal static ProviderSnapshot FromSection(ProviderSection section)
    {
        var kind = (section.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind.Length == 0)
        {
            throw new FormatException("provider.kind is missing.");
        }

        if (!string.Equals(kind, ProviderFactory.OpenAiCompatibleKind, StringComparison.Ordinal) &&
            !string.Equals(kind, ProviderFactory.AnthropicKind, StringComparison.Ordinal))
        {
            throw new FormatException(
                "provider.kind must be '" + ProviderFactory.OpenAiCompatibleKind + "' or '" +
                ProviderFactory.AnthropicKind + "', but is '" + kind + "'.");
        }

        var baseUrl = (section.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new FormatException("provider.baseUrl must be an absolute URL.");
        }

        var model = (section.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            throw new FormatException("provider.model is missing.");
        }

        var thinking = (section.Thinking ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" => ThinkingEffort.Off,
            "low" => ThinkingEffort.Low,
            "medium" => ThinkingEffort.Medium,
            "high" => ThinkingEffort.High,
            _ => throw new FormatException("provider.thinking must be off, low, medium or high."),
        };

        if (section.MaxOutputTokens is <= 0)
        {
            throw new FormatException("provider.maxOutputTokens must be a positive number.");
        }

        string? key = null;
        var fromEnvironment = false;
        var keyVariable = (section.ApiKeyEnv ?? string.Empty).Trim();
        if (keyVariable.Length > 0)
        {
            key = Environment.GetEnvironmentVariable(keyVariable);
            fromEnvironment = true;
        }
        else if (!string.IsNullOrWhiteSpace(section.ApiKey))
        {
            key = section.ApiKey.Trim();
        }

        var models = new List<string> { model };
        foreach (var extra in section.Models ?? [])
        {
            var name = (extra ?? string.Empty).Trim();
            if (name.Length > 0 && !models.Contains(name, StringComparer.Ordinal))
            {
                models.Add(name);
            }
        }

        return new ProviderSnapshot(
            kind,
            baseUrl,
            model,
            thinking,
            section.MaxOutputTokens,
            key,
            keyVariable,
            fromEnvironment,
            models,
            ReadUserMetadata(section.ModelMeta));
    }

    /// <summary>
    /// <c>provider.modelMeta</c> as resolved metadata: a window needs a positive value, and a price entry only counts when
    /// at least an input or an output price is present (a missing one is 0, which is right for a cache path a provider does
    /// not bill).
    /// </summary>
    private static Dictionary<string, ModelMetadata> ReadUserMetadata(Dictionary<string, ModelMetadataSection>? overrides)
    {
        var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
        if (overrides is null)
        {
            return metadata;
        }

        foreach (var (modelId, section) in overrides)
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

    /// <summary>
    /// What is known about one model: the built-in table, then what the provider reported for its own models, then the
    /// user's <c>provider.modelMeta</c> override — merged field by field, so a window from one source can sit next to a
    /// price from another and anything unknown stays unknown.
    /// </summary>
    public ModelMetadata MetadataFor(string? modelId)
    {
        var discovered = modelId is not null && discoveredMetadata.TryGetValue(modelId, out var reported)
            ? reported
            : null;
        var user = modelId is not null && userMetadata.TryGetValue(modelId, out var overridden)
            ? overridden
            : null;
        return ModelMetadataResolver.Resolve(modelId, discovered, user);
    }
}

/// <summary>Result of reading <c>settings.json</c>: either a usable provider or the sentence that says why there is none.</summary>
internal sealed record ProviderSettings
{
    /// <summary>A status that is already a string (a test, or a caller that rendered its own); it cannot follow a language switch.</summary>
    public ProviderSettings(ProviderSnapshot? provider, string status)
        : this(provider, LocText.Literal(status))
    {
    }

    public ProviderSettings(ProviderSnapshot? provider, LocText status)
    {
        Provider = provider;
        StatusText = status;
        Status = status.Resolve();
    }

    public ProviderSnapshot? Provider { get; }

    /// <summary>The reason there is no provider (or the summary of the one there is), kept as a key so the status bar
    /// and the settings page re-render it after a language switch.</summary>
    public LocText StatusText { get; }

    /// <summary><see cref="StatusText"/> in the language of the load, for callers that store a plain string.</summary>
    public string Status { get; }

    /// <summary>Model metadata for one model; unknown when no provider is configured at all.</summary>
    public ModelMetadata MetadataFor(string? modelId) => Provider?.MetadataFor(modelId) ?? ModelMetadata.Unknown;

    public bool IsConfigured => Provider is not null;

    public static ProviderSettings Load(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return new ProviderSettings(null, ShellStrings.ProviderSettingsMissing());
        }

        SettingsDocument? document;
        try
        {
            using var stream = File.OpenRead(settingsPath);
            document = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProviderSettings(null, ShellStrings.ProviderSettingsUnreadable(exception.Message));
        }

        if (document?.Provider is not { } section)
        {
            return new ProviderSettings(null, ShellStrings.ProviderSectionMissing());
        }

        try
        {
            var snapshot = ProviderSnapshot.FromSection(section);
            LocText status = snapshot.HasApiKey
                ? ShellStrings.ProviderReady(snapshot.Summary)
                : ShellStrings.ProviderNoKey(snapshot.Summary);
            return new ProviderSettings(snapshot, status);
        }
        catch (FormatException exception)
        {
            return new ProviderSettings(null, ShellStrings.ProviderInvalid(exception.Message));
        }
    }
}
