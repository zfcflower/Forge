using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.App.Localization;
using Forge.Core.Providers;
using Forge.IO;

namespace Forge.App.Providers;

/// <summary>
/// Reads and writes the <c>providers</c> section of <c>settings.json</c>. It owns three things the rest of the app must
/// not re-derive: the migration of the old single-provider section, the "never lose a field the app does not model" rule
/// (the file is edited through <see cref="JsonNode"/>, not re-serialized from the document type), and the rule that a
/// damaged file yields a stated reason instead of an exception at startup.
/// </summary>
internal sealed class ProviderStore
{
    private readonly string settingsPath;

    public ProviderStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
    }

    public string SettingsPath => settingsPath;

    /// <summary>
    /// Loads the connected providers. Never throws for file problems: the returned catalogue carries the sentence that says
    /// what is wrong, and the settings page shows it instead of an empty list.
    /// </summary>
    public ProviderCatalog Load(
        ProviderSecretStore secrets,
        ProviderCredentials credentials,
        ProviderModelCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!File.Exists(settingsPath))
        {
            return ProviderCatalog.Empty(ProviderStrings.SettingsMissingText());
        }

        SettingsDocument? document;
        try
        {
            using var stream = File.OpenRead(settingsPath);
            document = JsonSerializer.Deserialize(stream, ProvidersJsonContext.Default.SettingsDocument);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderCatalog.Empty(ProviderStrings.SettingsUnreadableText(exception.Message));
        }

        if (document is null)
        {
            return ProviderCatalog.Empty(ProviderStrings.SectionMissingText());
        }

        var entries = document.Providers?.Items;
        if (entries is null or { Count: 0 })
        {
            // No multi-provider section yet: an older file's single `provider` becomes the first connected provider.
            if (document.Provider is { } legacy)
            {
                var migrated = Migrate(legacy, secrets);
                return migrated is null
                    ? ProviderCatalog.Empty(ProviderStrings.InvalidText("the provider section is unusable"))
                    : ProviderCatalog.Create([migrated], migrated.Id, secrets, credentials, cache, document.ModelCatalog?.DefaultContextWindow);
            }

            // Nothing connected yet: an empty catalogue, still carrying the page's global context window.
            return ProviderCatalog.Create([], null, secrets, credentials, cache, document.ModelCatalog?.DefaultContextWindow);
        }

        var definitions = new List<ProviderDefinition>(entries.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var fallbackId = string.IsNullOrWhiteSpace(entry.Preset) ? "provider" : entry.Preset!;
            var definition = ProviderDefinition.FromDocument(entry, UniqueId(fallbackId, ids));
            ids.Add(definition.Id);
            definitions.Add(definition);
        }

        if (definitions.Count == 0)
        {
            return ProviderCatalog.Empty(ProviderStrings.SectionMissingText());
        }

        return ProviderCatalog.Create(
            definitions,
            document.Providers!.DefaultId,
            secrets,
            credentials,
            cache,
            document.ModelCatalog?.DefaultContextWindow);
    }

    /// <summary>
    /// Writes the section back and drops the migrated legacy one. Runs on a background thread; the file is read whole,
    /// only <c>providers</c> and <c>provider</c> are touched, everything else (including sections this build does not know)
    /// stays byte-for-byte in place.
    /// </summary>
    public void Save(IReadOnlyList<ProviderDefinition> definitions, string? defaultId, int? defaultContextWindow)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        // The preferences store rewrites the same file; without a shared gate the later writer's merge would drop the
        // earlier writer's section (the replace is atomic, the merge is not).
        using var lease = Composition.SettingsFileMutex.Acquire(settingsPath);

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(settingsPath));
            }
            catch (JsonException)
            {
                // A file we cannot parse is never overwritten: that would destroy a hand-edited configuration.
                throw new InvalidOperationException("settings.json is not valid JSON; fix or remove it before saving providers.");
            }

            // Valid JSON is not enough: an array or a scalar root is somebody else's document, and turning it into an
            // object would delete everything it held.
            root = parsed as JsonObject ?? throw new InvalidOperationException(
                "settings.json must be a JSON object; refusing to replace a document with a different root shape.");
        }
        else
        {
            root = [];
        }

        var section = new ProvidersSectionDocument
        {
            Version = ProvidersSectionDocument.CurrentVersion,
            DefaultId = string.IsNullOrWhiteSpace(defaultId) ? null : defaultId,
            Items = [.. definitions.Select(definition => definition.ToDocument())],
        };
        root["providers"] = JsonSerializer.SerializeToNode(section, ProvidersJsonContext.Default.ProvidersSectionDocument);
        root.Remove("provider");

        // The catalog section keeps any field this build does not know (a fetched snapshot will live here); only the one
        // value the page edits is written, and an empty value removes just that property.
        if (root["modelCatalog"] is not JsonObject catalog)
        {
            catalog = [];
            root["modelCatalog"] = catalog;
        }

        if (defaultContextWindow is > 0)
        {
            catalog["defaultContextWindow"] = defaultContextWindow.Value;
        }
        else
        {
            catalog.Remove("defaultContextWindow");
        }

        if (catalog.Count == 0)
        {
            root.Remove("modelCatalog");
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// The old single-provider section as one connected provider. The preset is guessed from the protocol: an Anthropic
    /// line becomes the Anthropic preset, anything else a custom OpenAI-compatible entry that keeps its own URL.
    /// </summary>
    private static ProviderDefinition? Migrate(LegacyProviderSection legacy, ProviderSecretStore secrets)
    {
        var kind = (legacy.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var baseUrl = (legacy.BaseUrl ?? string.Empty).Trim();
        var model = (legacy.Model ?? string.Empty).Trim();
        if (kind.Length == 0 || baseUrl.Length == 0)
        {
            return null;
        }

        var preset = string.Equals(kind, ProviderFactory.AnthropicKind, StringComparison.Ordinal)
            ? ProviderPresets.Find(ProviderPresets.AnthropicId)!
            : ProviderPresets.Custom;

        var models = new List<string>();
        if (model.Length > 0)
        {
            models.Add(model);
        }

        foreach (var extra in legacy.Models ?? [])
        {
            var name = (extra ?? string.Empty).Trim();
            if (name.Length > 0 && !models.Contains(name, StringComparer.Ordinal))
            {
                models.Add(name);
            }
        }

        // The old file could hold a literal key. It moves into the encrypted store before the definition that points at it
        // is handed out, so the first save (which drops the legacy section) cannot lose the credential.
        var reference = string.IsNullOrWhiteSpace(legacy.ApiKey) ? null : preset.Id + "-key";
        var definition = new ProviderDefinition
        {
            Id = preset.Id,
            PresetId = preset.Id,
            DisplayName = preset.DisplayName,
            Protocol = string.Equals(kind, ProviderFactory.AnthropicKind, StringComparison.Ordinal)
                ? ProviderProtocol.Anthropic
                : ProviderProtocol.OpenAiCompatible,
            BaseUrl = baseUrl,
            CredentialKind = ProviderCredentialKind.ApiKey,
            EnvironmentVariable = string.IsNullOrWhiteSpace(legacy.ApiKeyEnv) ? null : legacy.ApiKeyEnv!.Trim(),
            SecretReference = reference,
            Models = models,
            DefaultModel = model,
            Thinking = (legacy.Thinking ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "low" => Forge.Abstractions.ThinkingEffort.Low,
                "medium" => Forge.Abstractions.ThinkingEffort.Medium,
                "high" => Forge.Abstractions.ThinkingEffort.High,
                _ => Forge.Abstractions.ThinkingEffort.Off,
            },
            MaxOutputTokens = legacy.MaxOutputTokens is > 0 ? legacy.MaxOutputTokens : null,
            Overrides = ProviderDefinition.FromMetadataDocument(legacy.ModelMeta),
            // The single-provider section carried these two; dropping them on migration would silently change how prompts
            // are cached on the first save.
            SendPromptCacheKey = legacy.SendPromptCacheKey ?? false,
            PromptCacheTtl = (legacy.PromptCacheTtl ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "5m" => "5m",
                "1h" => "1h",
                _ => null,
            },
        };

        if (reference is not null)
        {
            AdoptLegacyLiterals(secrets, reference, legacy.ApiKey);
        }

        return definition;
    }

    private static string UniqueId(string candidate, HashSet<string> taken)
    {
        var id = string.IsNullOrWhiteSpace(candidate) ? "provider" : candidate.Trim();
        if (!taken.Contains(id))
        {
            return id;
        }

        for (var index = 2; ; index++)
        {
            var next = id + "-" + index;
            if (!taken.Contains(next))
            {
                return next;
            }
        }
    }

    /// <summary>
    /// A literal key that was stored in the old file (the pre-multi-provider build allowed it) is moved into the encrypted
    /// store the first time the catalogue is built, so the caller can then rewrite the file without the literal.
    /// </summary>
    internal static void AdoptLegacyLiterals(ProviderSecretStore secrets, string secretReference, string? literalKey)
    {
        if (string.IsNullOrWhiteSpace(literalKey) || secrets.Has(secretReference))
        {
            return;
        }

        try
        {
            secrets.Set(secretReference, literalKey!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning("[providers] could not move a stored key into the secret store: " + exception.GetType().Name);
        }
    }
}
