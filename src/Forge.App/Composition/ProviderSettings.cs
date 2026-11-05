using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;
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
            throw new FormatException("缺少 provider.kind。");
        }

        if (!string.Equals(kind, ProviderFactory.OpenAiCompatibleKind, StringComparison.Ordinal) &&
            !string.Equals(kind, ProviderFactory.AnthropicKind, StringComparison.Ordinal))
        {
            throw new FormatException(
                "provider.kind 只能是 '" + ProviderFactory.OpenAiCompatibleKind + "' 或 '" +
                ProviderFactory.AnthropicKind + "'，实际是 '" + kind + "'。");
        }

        var baseUrl = (section.BaseUrl ?? string.Empty).Trim();
        if (baseUrl.Length == 0 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new FormatException("provider.baseUrl 必须是绝对 URL。");
        }

        var model = (section.Model ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            throw new FormatException("缺少 provider.model。");
        }

        var thinking = (section.Thinking ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" => ThinkingEffort.Off,
            "low" => ThinkingEffort.Low,
            "medium" => ThinkingEffort.Medium,
            "high" => ThinkingEffort.High,
            _ => throw new FormatException("provider.thinking 只能是 off、low、medium 或 high。"),
        };

        if (section.MaxOutputTokens is <= 0)
        {
            throw new FormatException("provider.maxOutputTokens 必须为正数。");
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
internal sealed record ProviderSettings(ProviderSnapshot? Provider, string Status)
{
    public bool IsConfigured => Provider is not null;

    public static ProviderSettings Load(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return new ProviderSettings(null, ViewModels.Strings.ProviderSettingsMissing());
        }

        SettingsDocument? document;
        try
        {
            using var stream = File.OpenRead(settingsPath);
            document = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.SettingsDocument);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProviderSettings(null, ViewModels.Strings.ProviderSettingsUnreadable(exception.Message));
        }

        if (document?.Provider is not { } section)
        {
            return new ProviderSettings(null, ViewModels.Strings.ProviderSectionMissing());
        }

        try
        {
            var snapshot = ProviderSnapshot.FromSection(section);
            var status = snapshot.HasApiKey
                ? ViewModels.Strings.ProviderReady(snapshot.Summary)
                : ViewModels.Strings.ProviderNoKey(snapshot.Summary);
            return new ProviderSettings(snapshot, status);
        }
        catch (FormatException exception)
        {
            return new ProviderSettings(null, ViewModels.Strings.ProviderInvalid(exception.Message));
        }
    }
}
