using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The built-in (local) model metadata table: the third source of <see cref="ModelMetadata"/>, weaker than what the
/// provider reports for its own models and weaker than the user's <c>settings.json</c> override.
/// <para>The prices are published list prices in US dollars per one million tokens at the time of writing. They only feed a
/// display estimate, so a stale number is harmless: the provider's own metadata and the user override both win over this
/// table field by field, and a model this table does not know simply shows no percentage and no cost.</para>
/// </summary>
public static class ModelMetadataCatalog
{
    private static readonly (string Id, ModelMetadata Metadata)[] Table = BuildTable();

    /// <summary>
    /// Metadata for a model id, or <see cref="ModelMetadata.Unknown"/>. Matching is deliberately forgiving because a
    /// provider serves ids the table cannot list exhaustively: after normalizing, an exact id wins, then the longest
    /// known id the model id starts with (<c>gpt-4.1-mini</c> before <c>gpt-4.1</c>), then the longest known id the
    /// model id contains (<c>claude-sonnet-4-5-20250929</c> → <c>claude-sonnet-4-5</c>).
    /// </summary>
    public static ModelMetadata Lookup(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelMetadata.Unknown;
        }

        var normalized = Normalize(modelId);
        if (normalized.Length == 0)
        {
            return ModelMetadata.Unknown;
        }

        // Longest id first, so a more specific entry is always preferred over the family it belongs to.
        foreach (var entry in Table)
        {
            if (normalized.Equals(entry.Id, StringComparison.Ordinal))
            {
                return entry.Metadata;
            }
        }

        foreach (var entry in Table)
        {
            if (normalized.StartsWith(entry.Id, StringComparison.Ordinal))
            {
                return entry.Metadata;
            }
        }

        foreach (var entry in Table)
        {
            if (normalized.Contains(entry.Id, StringComparison.Ordinal))
            {
                return entry.Metadata;
            }
        }

        return ModelMetadata.Unknown;
    }

    /// <summary>
    /// Trims, lower-cases, drops an OpenRouter-style <c>vendor/</c> prefix and a trailing <c>:free</c> or
    /// <c>-latest</c>, so the same model matches whatever spelling the gateway uses.
    /// </summary>
    private static string Normalize(string modelId)
    {
        var trimmed = modelId.AsSpan().Trim();
        var slash = trimmed.LastIndexOf('/');
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        if (trimmed.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^5];
        }
        else if (trimmed.EndsWith("-latest", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7];
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    private static (string Id, ModelMetadata Metadata)[] BuildTable()
    {
        var entries = new (string Id, ModelMetadata Metadata)[]
        {
            ("gpt-4.1", new(1_047_576, new ModelPricing(2.00, 8.00, 0.50, 0))),
            ("gpt-4.1-mini", new(1_047_576, new ModelPricing(0.40, 1.60, 0.10, 0))),
            ("gpt-4o", new(128_000, new ModelPricing(2.50, 10.00, 1.25, 0))),
            ("gpt-4o-mini", new(128_000, new ModelPricing(0.15, 0.60, 0.075, 0))),
            ("claude-sonnet-4-5", new(200_000, new ModelPricing(3.00, 15.00, 0.30, 3.75))),
            ("claude-opus-4-1", new(200_000, new ModelPricing(15.00, 75.00, 1.50, 18.75))),
            ("claude-haiku-4-5", new(200_000, new ModelPricing(1.00, 5.00, 0.10, 1.25))),
            ("gemini-2.5-pro", new(1_048_576, new ModelPricing(1.25, 10.00, 0.31, 0))),
            ("gemini-2.5-flash", new(1_048_576, new ModelPricing(0.30, 2.50, 0.075, 0))),
            ("deepseek-chat", new(128_000, new ModelPricing(0.27, 1.10, 0.07, 0))),
        };

        // Longest id first: the lookup walks this order in every pass, so the family entry never shadows a sibling.
        Array.Sort(entries, static (left, right) => right.Id.Length.CompareTo(left.Id.Length));
        return entries;
    }
}
