using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// The on-disk form of the provider <c>/models</c> answers (<c>provider-models.json</c>): for each provider, the ids it
/// listed in its own order and whatever window and prices it reported.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> part of <c>settings.json</c>. That file is the user's configuration, merged section by
/// section by three writers under one mutex and synced or hand-edited by its owner; a network answer rewritten there every
/// day would turn "the provider changed its price" into "the configuration changed", and a stale cache could no longer be
/// thrown away without losing a real setting. Beside it, this file is expendable: delete it and the app falls back to the
/// catalog snapshot until the next refresh.
/// </remarks>
internal static class ProviderModelsFile
{
    public const int Version = 1;

    public const string FileName = "provider-models.json";

    /// <summary>The file's bytes: provider id → array of models, in the order the provider listed them.</summary>
    public static byte[] Write(IReadOnlyDictionary<string, ProviderModelList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        using var buffer = new MemoryStream(64 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartObject("providers");
            foreach (var (providerId, list) in lists)
            {
                writer.WriteStartArray(providerId);
                foreach (var id in list.Ids)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", id);
                    if (list.Metadata.TryGetValue(id, out var metadata))
                    {
                        if (metadata.ContextWindow is { } context)
                        {
                            writer.WriteNumber("context", context);
                        }

                        if (metadata.Pricing is { } pricing)
                        {
                            writer.WriteStartArray("cost");
                            writer.WriteNumberValue(pricing.Input);
                            writer.WriteNumberValue(pricing.Output);
                            writer.WriteNumberValue(pricing.CacheRead);
                            writer.WriteNumberValue(pricing.CacheWrite);
                            writer.WriteEndArray();
                        }
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the file back. False when it is damaged or from another version; the caller then starts with an empty cache,
    /// which costs one refresh per provider and nothing else.
    /// </summary>
    public static bool TryRead(byte[] payload, out Dictionary<string, ProviderModelList> lists, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lists = new Dictionary<string, ProviderModelList>(StringComparer.Ordinal);
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            error = "provider-models.json is not valid JSON: " + exception.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != Version)
            {
                error = "provider-models.json was written by a different version.";
                return false;
            }

            if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
            {
                error = "provider-models.json has no providers section.";
                return false;
            }

            foreach (var provider in providers.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var ids = new List<string>(provider.Value.GetArrayLength());
                var metadata = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal);
                foreach (var entry in provider.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.String
                        || id.GetString() is not { Length: > 0 } modelId)
                    {
                        continue;
                    }

                    ids.Add(modelId);
                    var context = entry.TryGetProperty("context", out var contextValue)
                        && contextValue.ValueKind == JsonValueKind.Number
                        && contextValue.TryGetInt32(out var window)
                        && window > 0
                            ? window
                            : (int?)null;
                    ModelPricing? pricing = null;
                    if (entry.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Array && cost.GetArrayLength() == 4)
                    {
                        var values = new double[4];
                        var index = 0;
                        foreach (var value in cost.EnumerateArray())
                        {
                            values[index++] = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
                        }

                        pricing = new ModelPricing(values[0], values[1], values[2], values[3]);
                    }

                    if (context is not null || pricing is not null)
                    {
                        metadata[modelId] = new ModelMetadata(context, pricing);
                    }
                }

                if (ids.Count > 0)
                {
                    lists[provider.Name] = new ProviderModelList(ids, metadata);
                }
            }

            return true;
        }
    }
}
