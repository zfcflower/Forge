using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The lookup side of the fetched catalog: given the provider a model is asked for on and the model id, it answers the
/// catalog entry that applies. Immutable, built once per snapshot, and shared by every reader — the settings page, the
/// session readout and the reasoning dial all ask it the same question.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider first, model id second.</b> The catalog prices the same model differently per provider (an aggregator adds
/// margin over first-party list prices), so a provider the app knows how to map answers from its own catalog entry. A
/// provider it cannot map — a custom OpenAI-compatible gateway, or a preset added upstream tomorrow — falls back to the
/// entry for that model id wherever the catalog has it, because showing a plausible list price beats showing none: this
/// data only ever feeds a display estimate, and the provider's own <c>/models</c> answer still wins over it.
/// </para>
/// <para>
/// Model ids are matched after <see cref="ModelIdRules.Normalize"/>: exact id first, then the longest known id the model id
/// extends with a snapshot suffix (<c>claude-sonnet-4-5-20250929</c> → <c>claude-sonnet-4-5</c>), then the longest known id
/// the model id contains.
/// </para>
/// </remarks>
public sealed class ModelCatalogView
{
    /// <summary>First-party providers, most authoritative first: they win a model id the catalog lists several times.</summary>
    private static readonly string[] ProviderPriority =
    [
        "anthropic", "openai", "google", "xai", "deepseek", "moonshotai", "meta", "github-copilot", "openrouter",
        "opencode", "opencode-go",
    ];

    /// <summary>The Forge presets the catalog knows about, as preset id → catalog provider id.</summary>
    private static readonly Dictionary<string, string> PresetProviders = new(StringComparer.Ordinal)
    {
        [ProviderPresets.AnthropicId] = "anthropic",
        [ProviderPresets.OpenAiId] = "openai",
        [ProviderPresets.CopilotId] = "github-copilot",
        [ProviderPresets.OpenRouterId] = "openrouter",
        [ProviderPresets.KimiId] = "moonshotai",
        [ProviderPresets.MetaId] = "meta",
        [ProviderPresets.XAiId] = "xai",
        // OpenAI's one preset covers both ways in — a platform key and a ChatGPT plan — and both are OpenAI's own models.
    };

    private readonly Dictionary<string, ProviderIndex> byProvider = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModelCatalogModel> globalExact = new(StringComparer.Ordinal);
    private readonly (string Id, ModelCatalogModel Model)[] globalBases;

    private ModelCatalogView(ModelCatalogSnapshot? snapshot)
    {
        Snapshot = snapshot;
        if (snapshot is null)
        {
            globalBases = [];
            return;
        }

        foreach (var (providerId, models) in snapshot.Providers)
        {
            byProvider[providerId] = ProviderIndex.Create(models);
        }

        // The global index answers for providers this build cannot map. Priority order decides which provider's entry
        // wins; the remaining providers follow alphabetically so the choice is at least deterministic.
        foreach (var providerId in OrderedProviderIds(snapshot))
        {
            if (!snapshot.Providers.TryGetValue(providerId, out var models))
            {
                continue;
            }

            foreach (var (modelId, model) in models)
            {
                var normalized = ModelIdRules.Normalize(modelId);
                if (normalized.Length > 0)
                {
                    globalExact.TryAdd(normalized, model);
                }
            }
        }

        // Longest first: the most specific base wins over the family it belongs to.
        globalBases = [.. globalExact
            .OrderByDescending(static pair => pair.Key.Length)
            .Select(static pair => (pair.Key, pair.Value))];
    }

    /// <summary>No catalog cached yet: every lookup answers "unknown" and the built-in table decides.</summary>
    public static ModelCatalogView Empty { get; } = new(null);

    /// <summary>The snapshot this view was built from, or <c>null</c> for <see cref="Empty"/>.</summary>
    public ModelCatalogSnapshot? Snapshot { get; }

    /// <summary>When the catalog was fetched, or <c>null</c> when nothing was ever fetched.</summary>
    public DateTimeOffset? FetchedAt => Snapshot is { } snapshot && snapshot.FetchedAt != DateTimeOffset.MinValue
        ? snapshot.FetchedAt
        : null;

    /// <summary>The URL the cached catalog came from.</summary>
    public string? SourceUrl => Snapshot?.SourceUrl;

    public int ProviderCount => Snapshot?.Providers.Count ?? 0;

    public int ModelCount => Snapshot?.ModelCount ?? 0;

    /// <summary>Builds a view over a snapshot (or the empty view for <c>null</c>).</summary>
    public static ModelCatalogView Create(ModelCatalogSnapshot? snapshot) => snapshot is null ? Empty : new ModelCatalogView(snapshot);

    /// <summary>The catalog provider id a preset maps to, or <c>null</c> when this build does not know the preset.</summary>
    public static string? ProviderIdForPreset(string? presetId) =>
        string.IsNullOrWhiteSpace(presetId) ? null : PresetProviders.GetValueOrDefault(presetId);

    /// <summary>The catalog entry for one model on one provider, or <c>null</c> when the catalog says nothing about it.</summary>
    public ModelCatalogModel? Find(string? presetId, string? modelId)
    {
        var normalized = ModelIdRules.Normalize(modelId);
        if (normalized.Length == 0 || Snapshot is null)
        {
            return null;
        }

        if (ProviderIdForPreset(presetId) is { } providerId
            && byProvider.TryGetValue(providerId, out var index)
            && index.Find(normalized) is { } mapped)
        {
            return mapped;
        }

        return FindGlobal(normalized);
    }

    /// <summary>The resolved metadata for one model: the entry as <see cref="ModelMetadata"/>, or unknown.</summary>
    public ModelMetadata Lookup(string? presetId, string? modelId) => Find(presetId, modelId)?.ToMetadata() ?? ModelMetadata.Unknown;

    /// <summary>The reasoning steps the catalog knows for one model, or <c>null</c>; the caller falls back to its own table.</summary>
    public IReadOnlyList<ThinkingEffort>? ReasoningStepsFor(string? presetId, string? modelId) =>
        Find(presetId, modelId)?.ReasoningSteps;

    private ModelCatalogModel? FindGlobal(string normalized)
    {
        if (globalExact.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        foreach (var (id, model) in globalBases)
        {
            if (normalized.Length > id.Length
                && normalized.StartsWith(id, StringComparison.Ordinal)
                && ModelIdRules.IsSnapshotSuffix(normalized.AsSpan(id.Length)))
            {
                return model;
            }
        }

        return null;
    }

    private static IEnumerable<string> OrderedProviderIds(ModelCatalogSnapshot snapshot)
    {
        foreach (var providerId in ProviderPriority)
        {
            if (snapshot.Providers.ContainsKey(providerId))
            {
                yield return providerId;
            }
        }

        foreach (var providerId in snapshot.Providers.Keys.Order(StringComparer.Ordinal))
        {
            if (!ProviderPriority.Contains(providerId, StringComparer.Ordinal))
            {
                yield return providerId;
            }
        }
    }

    /// <summary>One provider's models, keyed and ordered for the three-pass match.</summary>
    private sealed class ProviderIndex
    {
        private readonly Dictionary<string, ModelCatalogModel> exact;
        private readonly (string Id, ModelCatalogModel Model)[] longestFirst;

        private ProviderIndex(Dictionary<string, ModelCatalogModel> exact)
        {
            this.exact = exact;
            longestFirst = [.. exact
                .OrderByDescending(static pair => pair.Key.Length)
                .Select(static pair => (pair.Key, pair.Value))];
        }

        public static ProviderIndex Create(IReadOnlyDictionary<string, ModelCatalogModel> models)
        {
            var exact = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal);
            foreach (var (modelId, model) in models)
            {
                var normalized = ModelIdRules.Normalize(modelId);
                if (normalized.Length > 0)
                {
                    exact.TryAdd(normalized, model);
                }
            }

            return new ProviderIndex(exact);
        }

        public ModelCatalogModel? Find(string normalized)
        {
            if (exact.TryGetValue(normalized, out var found))
            {
                return found;
            }

            foreach (var (id, model) in longestFirst)
            {
                if (normalized.Length > id.Length
                    && normalized.StartsWith(id, StringComparison.Ordinal)
                    && ModelIdRules.IsSnapshotSuffix(normalized.AsSpan(id.Length)))
                {
                    return model;
                }
            }

            foreach (var (id, model) in longestFirst)
            {
                if (normalized.Contains(id, StringComparison.Ordinal))
                {
                    return model;
                }
            }

            return null;
        }
    }
}
