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
    private readonly string? apiKey;

    private ProviderSnapshot(
        string kind,
        string baseUrl,
        string model,
        ThinkingEffort thinking,
        int? maxOutputTokens,
        string? apiKey,
        bool keyFromEnvironment)
    {
        Kind = kind;
        BaseUrl = baseUrl;
        Model = model;
        Thinking = thinking;
        MaxOutputTokens = maxOutputTokens;
        this.apiKey = apiKey;
        KeyFromEnvironment = keyFromEnvironment;
    }

    public string Kind { get; }

    public string BaseUrl { get; }

    public string Model { get; }

    public ThinkingEffort Thinking { get; }

    public int? MaxOutputTokens { get; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>Where the key came from, for the settings panel; the key itself is never exposed.</summary>
    public bool KeyFromEnvironment { get; }

    public ModelSettings ModelSettings => new(Model, Thinking, MaxOutputTokens);

    public string Summary => Kind + " · " + Model;

    /// <summary>Builds the provider the agent loop streams from. The HttpClient must already have an infinite timeout.</summary>
    public IChatProvider CreateProvider(HttpClient httpClient) =>
        new RetryingChatProvider(
            ProviderFactory.Create(new ProviderConfig("forge", Kind, BaseUrl, apiKey), httpClient));

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

        return new ProviderSnapshot(kind, baseUrl, model, thinking, section.MaxOutputTokens, key, fromEnvironment);
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
