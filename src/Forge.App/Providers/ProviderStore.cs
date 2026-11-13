using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Core.Providers;
using Forge.IO;
using Forge.App.Logging;
using ZLogger;

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
    private readonly Func<string?> claudeVersion;
    private readonly IUsageRecorder? usageRecorder;

    /// <param name="claudeVersion">
    /// What version to claim when the section is first written. Defaults to asking the locally installed CLI, so the claim names a
    /// build the user actually has; a machine without the CLI falls back to the shipped default. Injectable so a test asserts the
    /// section's shape without a 300 MB program on the test machine.
    /// </param>
    /// <param name="usageRecorder">
    /// Where model requests are recorded once the user turns the usage log on; null in a test or a build without the composition root.
    /// </param>
    public ProviderStore(string settingsPath, Func<string?>? claudeVersion = null, IUsageRecorder? usageRecorder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        this.claudeVersion = claudeVersion ?? DefaultClaudeVersion;
        this.usageRecorder = usageRecorder;
    }

    private static string? DefaultClaudeVersion() => ClaudeCliProbe.ReadVersion(ClaudeCliProbe.Locate());

    public string SettingsPath => settingsPath;

    /// <summary>
    /// The options an empty or unreadable catalogue still carries: the model catalog layer when there is one, and the wire
    /// claim's default — an absent <c>claudeWire</c> section means "claim", so a missing file must not read as "off".
    /// </summary>
    private ProviderCatalogOptions EmptyOptions(ModelCatalogStore? modelCatalog) =>
        new(ModelCatalog: modelCatalog, Wire: ClaudeWireSettings.Resolve(null), UsageRecorder: usageRecorder);

    /// <summary>
    /// Loads the connected providers. Never throws for file problems: the returned catalogue carries the sentence that says
    /// what is wrong, and the settings page shows it instead of an empty list.
    /// </summary>
    public ProviderCatalog Load(
        ProviderSecretStore secrets,
        ProviderCredentials credentials,
        ProviderModelCache? cache = null,
        ModelCatalogStore? modelCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentials);

        if (!File.Exists(settingsPath))
        {
            return ProviderCatalog.Empty(ProviderStrings.SettingsMissingText(), EmptyOptions(modelCatalog));
        }

        SettingsDocument? document;
        try
        {
            using var stream = File.OpenRead(settingsPath);
            document = JsonSerializer.Deserialize(stream, ProvidersJsonContext.Default.SettingsDocument);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderCatalog.Empty(ProviderStrings.SettingsUnreadableText(exception.Message), EmptyOptions(modelCatalog));
        }

        if (document is null)
        {
            return ProviderCatalog.Empty(ProviderStrings.SectionMissingText(), EmptyOptions(modelCatalog));
        }

        // The global window and the catalog address are read once: the same two values go to every catalogue this load
        // can produce (a migrated file, nothing connected, or the stored list).
        var catalogSettings = new ProviderCatalogOptions(
            ContextWindowDefault.FromJson(document.ModelCatalog?.DefaultContextWindow),
            document.ModelCatalog?.SourceUrl,
            modelCatalog,
            Wire: ClaudeWireSettings.Resolve(document.ClaudeWire),
            UsageRecorder: usageRecorder);

        var entries = document.Providers?.Items;
        if (entries is null or { Count: 0 })
        {
            // No multi-provider section yet: an older file's single `provider` becomes the first connected provider.
            if (document.Provider is { } legacy)
            {
                var migrated = Migrate(legacy, secrets);
                return migrated is null
                    ? ProviderCatalog.Empty(ProviderStrings.InvalidText("the provider section is unusable"))
                    : ProviderCatalog.Create(
                        [migrated],
                        migrated.Id,
                        secrets,
                        credentials,
                        cache,
                        catalogSettings);
            }

            // Nothing connected yet: an empty catalogue, still carrying the page's global context window.
            return ProviderCatalog.Create(
                [],
                null,
                secrets,
                credentials,
                cache,
                catalogSettings);
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
            return ProviderCatalog.Empty(ProviderStrings.SectionMissingText(), EmptyOptions(modelCatalog));
        }

        return ProviderCatalog.Create(
            definitions,
            document.Providers!.DefaultId,
            secrets,
            credentials,
            cache,
            catalogSettings);
    }

    /// <summary>
    /// Writes the section back and drops the migrated legacy one. Runs on a background thread; the file is read whole,
    /// only <c>providers</c> and <c>provider</c> are touched, everything else (including sections this build does not know)
    /// stays byte-for-byte in place.
    /// </summary>
    public void Save(
        IReadOnlyList<ProviderDefinition> definitions,
        string? defaultId,
        ContextWindowDefault defaultContextWindow,
        string? modelCatalogSourceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        // The preferences store rewrites the same file; without a shared gate the later writer's merge would drop the
        // earlier writer's section (the replace is atomic, the merge is not).
        using var lease = Composition.SettingsFileMutex.Acquire(settingsPath);

        var root = ReadRootForEdit(settingsPath);

        var section = new ProvidersSectionDocument
        {
            Version = ProvidersSectionDocument.CurrentVersion,
            DefaultId = string.IsNullOrWhiteSpace(defaultId) ? null : defaultId,
            Items = [.. definitions.Select(definition => definition.ToDocument())],
        };
        root["providers"] = JsonSerializer.SerializeToNode(section, ProvidersJsonContext.Default.ProvidersSectionDocument);
        root.Remove("provider");
        ReconcileWireSection(root, definitions);

        // The catalog section keeps any field this build does not know (an older build's pointers, a field added later);
        // only the two values the page edits are written, and an empty value removes just that property.
        if (root["modelCatalog"] is not JsonObject catalog)
        {
            catalog = [];
            root["modelCatalog"] = catalog;
        }

        // A count is a JSON number and a share is a string ("50%"): an older build reads the count it understands and
        // treats the share it cannot parse as "no global default" instead of as a wrong number.
        if (defaultContextWindow.ToNode() is { } window)
        {
            catalog["defaultContextWindow"] = window;
        }
        else
        {
            catalog.Remove("defaultContextWindow");
        }

        // The catalog address is the user's choice (a mirror is a legitimate reason to change it); an empty value means
        // "use the built-in default", which is why it removes the property instead of writing an empty string.
        if (string.IsNullOrWhiteSpace(modelCatalogSourceUrl))
        {
            catalog.Remove("sourceUrl");
        }
        else
        {
            catalog["sourceUrl"] = modelCatalogSourceUrl.Trim();
        }

        if (catalog.Count == 0)
        {
            root.Remove("modelCatalog");
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteAllBytes(settingsPath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Keeps <c>claudeWire</c> in step with what is connected: a Claude subscription provider is what the claim governs, so the
    /// section exists as soon as one is saved, and it is removed once none is left — a section nothing reads would be a note
    /// about a provider that is no longer there. An existing section is never rewritten: its values are the user's, including
    /// <c>"enabled": false</c>.
    /// </summary>
    /// <remarks>
    /// Only a save triggers this, never a load: starting the app must not edit the user's file. The consequence worth knowing is
    /// that an explicit <c>off</c> does not survive removing the last Claude provider — re-adding one restores the default claim.
    /// </remarks>
    private void ReconcileWireSection(JsonObject root, IReadOnlyList<ProviderDefinition> definitions)
    {
        var claimsIdentity = definitions.Any(definition =>
            definition.CredentialKind == ProviderCredentialKind.ClaudeSubscription);
        if (!claimsIdentity)
        {
            root.Remove(ClaudeWireSettings.SectionName);
            return;
        }

        if (root[ClaudeWireSettings.SectionName] is not null)
        {
            return;
        }

        root[ClaudeWireSettings.SectionName] = new JsonObject
        {
            ["enabled"] = true,
            // The version is what the locally installed CLI reports, so the claim names a build the user actually has; the build
            // suffix is deliberately absent — it is not stable per build, so it is read from the CLI at request time instead.
            ["version"] = claudeVersion() ?? ClaudeWireSettings.DefaultVersion,
            ["entrypoint"] = ClaudeWireSettings.DefaultEntrypoint,
            ["note"] = ClaudeWireSettings.Documentation,
        };
    }

    /// <summary>
    /// The whole file as an object for one section to edit, or an empty object when the file does not exist yet. A file
    /// that is not valid JSON, or whose root is not an object, is refused instead of overwritten: it is a hand-edited
    /// configuration or somebody else's document, and replacing it would delete what it held. Shared with the agent
    /// defaults store, so both sections fail exactly the same way.
    /// </summary>
    internal static JsonObject ReadRootForEdit(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return [];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(settingsPath));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("settings.json is not valid JSON; fix or remove it before saving settings.");
        }

        return parsed as JsonObject ?? throw new InvalidOperationException(
            "settings.json must be a JSON object; refusing to replace a document with a different root shape.");
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

        // The legacy section named a protocol, so it is read as one of the three this build speaks. A kind that is not one of
        // them is unusable rather than something to guess at: a wrong protocol fails every turn with a confusing error.
        var protocol = kind switch
        {
            ProviderFactory.AnthropicKind => ProviderProtocol.Anthropic,
            ProviderFactory.OpenAiResponsesKind => ProviderProtocol.OpenAiResponses,
            ProviderFactory.OpenAiCompatibleKind => ProviderProtocol.OpenAiCompatible,
            _ => (ProviderProtocol?)null,
        };

        if (protocol is null)
        {
            return null;
        }

        var preset = protocol == ProviderProtocol.Anthropic
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
            Protocol = protocol.Value,
            BaseUrl = baseUrl,
            CredentialKind = ProviderCredentialKind.ApiKey,
            EnvironmentVariable = string.IsNullOrWhiteSpace(legacy.ApiKeyEnv) ? null : legacy.ApiKeyEnv!.Trim(),
            SecretReference = reference,
            Models = models,
            DefaultModel = model,
            // Every step name this build knows, so a migrated setting keeps the effort it had instead of dropping to off.
            Thinking = ThinkingEfforts.TryParse(legacy.Thinking, out var thinking) ? thinking : ThinkingEffort.Off,
            MaxOutputTokens = legacy.MaxOutputTokens is > 0 ? legacy.MaxOutputTokens : null,
            Overrides = ProviderDefinition.FromMetadataDocument(legacy.ModelMeta),
            // The single-provider section carried these two; dropping them on migration would silently change how prompts
            // are cached on the first save.
            SendPromptCacheKey = legacy.SendPromptCacheKey ?? false,
            PromptCacheTtl = ProviderDefinition.ParsePromptCacheTtl(legacy.PromptCacheTtl),
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
            AppLogs.Providers.ZLogWarning($"could not move a stored key into the secret store: {exception.GetType().Name}");
        }
    }
}
