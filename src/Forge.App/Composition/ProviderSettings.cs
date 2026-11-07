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

    private ProviderSnapshot(
        string kind,
        string baseUrl,
        string model,
        ThinkingEffort thinking,
        int? maxOutputTokens,
        string? apiKey,
        string? apiKeyEnvironmentVariable,
        bool keyFromEnvironment,
        IReadOnlyList<string> models)
    {
        Kind = kind;
        Models = models;
        BaseUrl = baseUrl;
        Model = model;
        Thinking = thinking;
        MaxOutputTokens = maxOutputTokens;
        this.apiKey = apiKey;
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
                }
            }

            return models;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while fetching the provider model list.", exception);
        }
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

        return new ProviderSnapshot(kind, baseUrl, model, thinking, section.MaxOutputTokens, key, keyVariable, fromEnvironment, models);
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
