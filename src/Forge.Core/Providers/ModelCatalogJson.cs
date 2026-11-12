using System.Globalization;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The on-disk form of <see cref="ModelCatalogSnapshot"/> (<c>model-catalog.json</c>). Written by the refresh, read at
/// startup, and deliberately small: only the facts the app resolves (window, reply limit, four prices, input kinds,
/// reasoning steps) instead of the 6 MB upstream payload.
/// </summary>
/// <remarks>
/// Hand-written reader and writer rather than a source-generated document type: the shape is ours, it is a flat
/// provider→model map, and keeping it out of the settings schema is what lets it be deleted at any time. Reading is
/// tolerant — an unknown field is skipped, a damaged file is reported — because this file is a cache, and a cache that
/// cannot be read must degrade to "no catalog", never to a startup failure.
/// </remarks>
public static class ModelCatalogJson
{
    public const int Version = 1;

    /// <summary>The file's bytes, with no indentation: this is a cache, not something a user reads in a diff.</summary>
    public static byte[] Write(ModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var buffer = new MemoryStream(1024 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("sourceUrl", snapshot.SourceUrl);
            if (string.IsNullOrEmpty(snapshot.ETag))
            {
                writer.WriteNull("etag");
            }
            else
            {
                writer.WriteString("etag", snapshot.ETag);
            }

            writer.WriteString("fetchedAt", snapshot.FetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (snapshot.CheckedAt is { } checkedAt)
            {
                writer.WriteString("checkedAt", checkedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }
            writer.WriteStartObject("providers");
            foreach (var (providerId, models) in snapshot.Providers)
            {
                writer.WriteStartObject(providerId);
                foreach (var (modelId, model) in models)
                {
                    // An entry that knows nothing is not written at all: it would cost bytes and read back as nothing.
                    if (model.ContextWindow is null && model.MaxOutputTokens is null && model.Pricing is null
                        && model.Inputs is null && model.Reasoning is null && model.ReasoningSteps is null)
                    {
                        continue;
                    }

                    writer.WriteStartObject(modelId);
                    if (model.ContextWindow is { } context)
                    {
                        writer.WriteNumber("context", context);
                    }

                    if (model.MaxOutputTokens is { } maxOutput)
                    {
                        writer.WriteNumber("maxOutput", maxOutput);
                    }

                    if (model.Pricing is { } pricing)
                    {
                        writer.WriteStartArray("cost");
                        writer.WriteNumberValue(pricing.Input);
                        writer.WriteNumberValue(pricing.Output);
                        writer.WriteNumberValue(pricing.CacheRead);
                        writer.WriteNumberValue(pricing.CacheWrite);
                        writer.WriteEndArray();
                    }

                    if (model.Inputs is { } inputs)
                    {
                        writer.WriteStartArray("inputs");
                        foreach (var (flag, name) in InputNames)
                        {
                            if ((inputs & flag) != 0)
                            {
                                writer.WriteStringValue(name);
                            }
                        }

                        writer.WriteEndArray();
                    }

                    if (model.Reasoning is { } reasons)
                    {
                        writer.WriteBoolean("reasoning", reasons);
                    }

                    if (model.ReasoningSteps is { } steps)
                    {
                        writer.WriteStartArray("efforts");
                        foreach (var step in steps)
                        {
                            writer.WriteStringValue(EffortName(step));
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a snapshot. False with a message when the file is damaged or written by a build that knows a different shape;
    /// the caller then behaves as if no catalog were cached, which costs one fetch and nothing else.
    /// </summary>
    public static bool TryRead(byte[] payload, out ModelCatalogSnapshot? snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        snapshot = null;
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException exception)
        {
            error = "model-catalog.json is not valid JSON: " + exception.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "model-catalog.json must be an object.";
                return false;
            }

            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != Version)
            {
                error = "model-catalog.json was written by a different version.";
                return false;
            }

            if (!root.TryGetProperty("fetchedAt", out var fetchedAtText)
                || fetchedAtText.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    fetchedAtText.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var fetchedAt))
            {
                error = "model-catalog.json has no readable fetchedAt.";
                return false;
            }

            DateTimeOffset? checkedAt = root.TryGetProperty("checkedAt", out var checkedAtText)
                                        && checkedAtText.ValueKind == JsonValueKind.String
                                        && DateTimeOffset.TryParse(
                                            checkedAtText.GetString(),
                                            CultureInfo.InvariantCulture,
                                            DateTimeStyles.RoundtripKind,
                                            out var checkedValue)
                ? checkedValue
                : null;

            var sourceUrl = root.TryGetProperty("sourceUrl", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString() ?? string.Empty
                : string.Empty;
            var etag = root.TryGetProperty("etag", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;

            if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
            {
                error = "model-catalog.json has no providers section.";
                return false;
            }

            var map = new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal);
            foreach (var provider in providers.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var models = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal);
                foreach (var model in provider.Value.EnumerateObject())
                {
                    if (model.Value.ValueKind == JsonValueKind.Object && ReadModel(model.Value) is { } entry)
                    {
                        models[model.Name] = entry;
                    }
                }

                if (models.Count > 0)
                {
                    map[provider.Name] = models;
                }
            }

            snapshot = new ModelCatalogSnapshot(sourceUrl, etag, fetchedAt, map) { CheckedAt = checkedAt };
            return true;
        }
    }

    private static ModelCatalogModel? ReadModel(JsonElement model)
    {
        int? context = model.TryGetProperty("context", out var c) && c.TryGetInt32(out var contextValue) && contextValue > 0
            ? contextValue
            : null;
        int? maxOutput = model.TryGetProperty("maxOutput", out var m) && m.TryGetInt32(out var maxOutputValue) && maxOutputValue > 0
            ? maxOutputValue
            : null;

        ModelPricing? pricing = null;
        if (model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Array && cost.GetArrayLength() == 4)
        {
            var values = new double[4];
            var index = 0;
            foreach (var value in cost.EnumerateArray())
            {
                values[index++] = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
            }

            pricing = new ModelPricing(values[0], values[1], values[2], values[3]);
        }

        ModelInputs? inputs = null;
        if (model.TryGetProperty("inputs", out var inputList) && inputList.ValueKind == JsonValueKind.Array)
        {
            var flags = ModelInputs.None;
            foreach (var item in inputList.EnumerateArray())
            {
                foreach (var (flag, name) in InputNames)
                {
                    if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), name, StringComparison.Ordinal))
                    {
                        flags |= flag;
                    }
                }
            }

            inputs = flags == ModelInputs.None ? null : flags;
        }

        bool? reasoning = model.TryGetProperty("reasoning", out var reasoningValue)
                         && reasoningValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? reasoningValue.ValueKind == JsonValueKind.True
            : null;

        IReadOnlyList<ThinkingEffort>? steps = null;
        if (model.TryGetProperty("efforts", out var reasoningList) && reasoningList.ValueKind == JsonValueKind.Array)
        {
            var set = new SortedSet<ThinkingEffort>();
            foreach (var item in reasoningList.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Effort(item.GetString()) is { } step)
                {
                    set.Add(step);
                }
            }

            steps = set.Count == 0 ? null : [.. set];
        }

        return context is null && maxOutput is null && pricing is null && inputs is null && steps is null && reasoning is null
            ? null
            : new ModelCatalogModel(context, maxOutput, pricing, inputs, steps) { Reasoning = reasoning };
    }

    private static readonly (ModelInputs Flag, string Name)[] InputNames =
    [
        (ModelInputs.Text, "text"),
        (ModelInputs.Image, "image"),
        (ModelInputs.Pdf, "pdf"),
        (ModelInputs.Audio, "audio"),
        (ModelInputs.Video, "video"),
    ];

    private static string EffortName(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "off",
    };

    private static ThinkingEffort? Effort(string? name) => name switch
    {
        "off" or "none" => ThinkingEffort.Off,
        "minimal" => ThinkingEffort.Minimal,
        "low" => ThinkingEffort.Low,
        "medium" => ThinkingEffort.Medium,
        "high" => ThinkingEffort.High,
        "xhigh" => ThinkingEffort.XHigh,
        "max" => ThinkingEffort.Max,
        _ => null,
    };
}
