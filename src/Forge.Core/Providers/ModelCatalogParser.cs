using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Reads the models.dev payload (<c>https://models.dev/api.json</c>) into the compact <see cref="ModelCatalogSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// The payload is <c>{providerId → {id, name, env[], models: {modelId → …}}}</c> — an object keyed by provider, not an
/// array. A model carries useful facts under <c>limit</c> (<c>context</c>, <c>output</c>), <c>cost</c> (<c>input</c>,
/// <c>output</c>, <c>cache_read</c>, <c>cache_write</c>, plus tiered shapes this build ignores), <c>modalities.input</c>
/// and <c>reasoning</c>/<c>reasoning_options</c>.
/// </para>
/// <para>
/// Parsing is deliberately forgiving: an entry missing everything the app uses is dropped, an unknown modality or reasoning
/// type is ignored, and a provider without a <c>models</c> object is skipped. The upstream schema is not ours to validate —
/// one odd entry must not cost the whole catalog, and a daily refresh is exactly the place where a new upstream field shows
/// up unannounced.
/// </para>
/// <para>
/// Reading goes through <see cref="JsonDocument"/> with no reflection, so the native AOT build needs no source-generated
/// model for a document shape that belongs to someone else and changes without us.
/// </para>
/// </remarks>
public static class ModelCatalogParser
{
    /// <summary>Refuses anything larger than this: a corrupted or hostile response must not be turned into an in-memory tree.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Parses one payload. Returns false with a message when the document is not the expected shape or holds no model at
    /// all; a partially odd payload still parses, minus the entries that said nothing usable.
    /// </summary>
    public static bool TryParse(
        byte[] payload,
        string sourceUrl,
        DateTimeOffset fetchedAt,
        string? etag,
        out ModelCatalogSnapshot? snapshot,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        snapshot = null;
        error = null;
        if (payload.Length == 0)
        {
            error = "The catalog response was empty.";
            return false;
        }

        if (payload.Length > MaxBytes)
        {
            error = "The catalog response is larger than " + MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            error = "The catalog response is not valid JSON: " + exception.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The catalog response must be a JSON object keyed by provider id.";
                return false;
            }

            var providers = new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal);
            foreach (var provider in root.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object
                    || !provider.Value.TryGetProperty("models", out var models)
                    || models.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entries = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal);
                foreach (var model in models.EnumerateObject())
                {
                    if (model.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (ReadModel(model.Value) is { } entry)
                    {
                        entries[model.Name] = entry;
                    }
                }

                if (entries.Count > 0)
                {
                    providers[provider.Name] = entries;
                }
            }

            if (providers.Count == 0)
            {
                error = "The catalog response described no models.";
                return false;
            }

            snapshot = new ModelCatalogSnapshot(sourceUrl, etag, fetchedAt, providers);
            return true;
        }
    }

    /// <summary>One model, or <c>null</c> when it says nothing this build uses.</summary>
    private static ModelCatalogModel? ReadModel(JsonElement model)
    {
        int? context = null;
        int? maxOutput = null;
        if (model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            context = Positive(limit, "context");
            maxOutput = Positive(limit, "output");
        }

        var pricing = ReadPricing(model);
        var inputs = ReadInputs(model);
        var reasoning = ReadReasoning(model);
        if (context is null && maxOutput is null && pricing is null && inputs is null && reasoning is null)
        {
            return null;
        }

        return new ModelCatalogModel(context, maxOutput, pricing, inputs, reasoning?.Steps)
        {
            Reasoning = reasoning?.Reasoning,
        };
    }

    /// <summary>
    /// The four per-million prices, or <c>null</c> when input or output is missing: a half-priced model would show a $0.00
    /// input cost as if it were free, which is worse than showing no price. The cache paths the source omits are zero
    /// because zero is what the app already means by "this model does not bill cache tokens".
    /// </summary>
    private static ModelPricing? ReadPricing(JsonElement model)
    {
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Number(cost, "input") is not { } input || Number(cost, "output") is not { } output)
        {
            return null;
        }

        return new ModelPricing(input, output, Number(cost, "cache_read") ?? 0, Number(cost, "cache_write") ?? 0);
    }

    /// <summary>The accepted input kinds; <c>null</c> when the source listed none this build names.</summary>
    private static ModelInputs? ReadInputs(JsonElement model)
    {
        if (!model.TryGetProperty("modalities", out var modalities)
            || modalities.ValueKind != JsonValueKind.Object
            || !modalities.TryGetProperty("input", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var inputs = ModelInputs.None;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            inputs |= item.GetString() switch
            {
                "text" => ModelInputs.Text,
                "image" => ModelInputs.Image,
                "pdf" => ModelInputs.Pdf,
                "audio" => ModelInputs.Audio,
                "video" => ModelInputs.Video,
                _ => ModelInputs.None,
            };
        }

        return inputs == ModelInputs.None ? null : inputs;
    }

    /// <summary>
    /// Whether the model reasons and which steps it accepts. The flag comes from <c>reasoning</c>; the steps only from an
    /// <c>effort</c> option, because a <c>toggle</c> or a token budget says "it thinks" without naming a level, and offering
    /// a step the model may reject is exactly what the reasoning dial must never do. A model the source marks as not
    /// reasoning accepts <see cref="ThinkingEffort.Off"/> alone; one that reasons with no effort list keeps its steps
    /// unknown (and the compiled table decides).
    /// </summary>
    private static (bool? Reasoning, IReadOnlyList<ThinkingEffort>? Steps)? ReadReasoning(JsonElement model)
    {
        if (!model.TryGetProperty("reasoning", out var reasoning)
            || reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        if (reasoning.ValueKind == JsonValueKind.False)
        {
            return (false, [ThinkingEffort.Off]);
        }

        return (true, ReadEffortSteps(model));
    }

    private static IReadOnlyList<ThinkingEffort>? ReadEffortSteps(JsonElement model)
    {
        if (!model.TryGetProperty("reasoning_options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || !option.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "effort", StringComparison.Ordinal)
                || !option.TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // A sorted set keeps the dial's order and drops duplicates; "none" and "off" are the same step.
            var steps = new SortedSet<ThinkingEffort> { ThinkingEffort.Off };
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && Effort(value.GetString()) is { } effort)
                {
                    steps.Add(effort);
                }
            }

            if (steps.Count > 1)
            {
                return [.. steps];
            }
        }

        return null;
    }

    private static ThinkingEffort? Effort(string? value) => value switch
    {
        "none" or "off" => ThinkingEffort.Off,
        "minimal" => ThinkingEffort.Minimal,
        "low" => ThinkingEffort.Low,
        "medium" => ThinkingEffort.Medium,
        "high" => ThinkingEffort.High,
        "xhigh" => ThinkingEffort.XHigh,
        "max" => ThinkingEffort.Max,
        _ => null,
    };

    private static int? Positive(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    private static double? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) && number >= 0
            ? number
            : null;
}
