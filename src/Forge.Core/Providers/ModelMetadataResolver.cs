using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Resolves what is known about one model by chaining the three metadata sources, weakest first:
/// the built-in table (<see cref="ModelMetadataCatalog"/>) → what the provider reported for that model → the user's
/// <c>settings.json</c> override. <see cref="ModelMetadata.With"/> merges field by field, so a window size can come from
/// the table while the prices come from the user, and an unknown half stays unknown instead of becoming a guessed zero.
/// </summary>
public static class ModelMetadataResolver
{
    /// <summary>
    /// Metadata for <paramref name="modelId"/>. A session exists before a model is chosen, so an empty id is not an error:
    /// it resolves to <see cref="ModelMetadata.Unknown"/> and the shell shows the token counts it has instead of a
    /// percentage or a price.
    /// </summary>
    public static ModelMetadata Resolve(string? modelId, ModelMetadata? providerMetadata, ModelMetadata? userOverride) =>
        ModelMetadataCatalog.Lookup(modelId).With(providerMetadata).With(userOverride);
}
