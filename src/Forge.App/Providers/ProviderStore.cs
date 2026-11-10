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
    public ProviderCatalog Load(ProviderSecretStore secrets, ProviderCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!File.Exists(settingsPath))
        {
            return Seed(secrets, credentials, ProviderStrings.SettingsMissingText());
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
                var migrated = Migrate(legacy);
                return migrated is null
                    ? ProviderCatalog.Empty(ProviderStrings.InvalidText("the provider section is unusable"))
                    : ProviderCatalog.Create([migrated], migrated.Id, secrets, credentials);
            }

            return Seed(secrets, credentials, ProviderStrings.SectionMissingText());
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

        return ProviderCatalog.Create(definitions, document.Providers!.DefaultId, secrets, credentials);
    }

    /// <summary>
    /// A catalogue with the product's default provider when nothing is configured yet: one Anthropic entry, unsaved, so the
    /// provider section opens on something the user can fill in rather than on an empty list. The entry carries no
    /// credential, so its row honestly says a key (or the CLI login) is still missing.
    /// </summary>
    private static ProviderCatalog Seed(ProviderSecretStore secrets, ProviderCredentials credentials, LocText status)
    {
        var preset = ProviderPresets.Default;
        var definition = ProviderDefinition.FromPreset(preset, preset.Id);
        var catalog = ProviderCatalog.Create([definition], definition.Id, secrets, credentials);
        return catalog.Providers.Count > 0 ? catalog : ProviderCatalog.Empty(status);
    }

    /// <summary>
    /// Writes the section back and drops the migrated legacy one. Runs on a background thread; the file is read whole,
    /// only <c>providers</c> and <c>provider</c> are touched, everything else (including sections this build does not know)
    /// stays byte-for-byte in place.
    /// </summary>
    public void Save(IReadOnlyList<ProviderDefinition> definitions, string? defaultId)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                // A file we cannot parse is never overwritten: that would destroy a hand-edited configuration.
                throw new InvalidOperationException("settings.json is not valid JSON; fix or remove it before saving providers.");
            }
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

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// The old single-provider section as one connected provider. The preset is guessed from the protocol: an Anthropic
    /// line becomes the Anthropic preset, anything else a custom OpenAI-compatible entry that keeps its own URL.
    /// </summary>
    private static ProviderDefinition? Migrate(LegacyProviderSection legacy)
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

        return new ProviderDefinition
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
            SecretReference = null,
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
        };
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
