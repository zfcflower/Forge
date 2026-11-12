using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Resolves what is known about one model by chaining the three metadata sources, weakest first: the fetched catalog
/// snapshot (<c>model-catalog.json</c>) → what the provider reported for that model → the user's <c>settings.json</c>
/// override. <see cref="ModelMetadata.With"/> merges field by field, so a window size can come from the snapshot while the
/// prices come from the provider, and an unknown half stays unknown instead of becoming a guessed zero.
/// </summary>
/// <remarks>
/// <para>
/// There is no compiled fallback table: the app cannot run an agent without a configured provider, and a provider is
/// reached over the network, so the moment a price or a window is worth showing is a moment a catalog can be fetched. An
/// unknown half is displayed as unknown, which is honest, while a stale compiled number would quietly be wrong.
/// </para>
/// <para>
/// The snapshot stays below what a provider says about its own models: the gateway is authoritative for the models it
/// serves, while the catalog is the same kind of data — published list prices per model id — for everything else.
/// </para>
/// </remarks>
public static class ModelMetadataResolver
{
    /// <summary>
    /// Metadata for <paramref name="modelId"/>. A session exists before a model is chosen, so an empty id is not an error:
    /// it resolves to <see cref="ModelMetadata.Unknown"/> and the shell shows the token counts it has instead of a
    /// percentage or a price. Every source is optional: passing <c>null</c> is "that layer knows nothing".
    /// </summary>
    public static ModelMetadata Resolve(
        string? modelId,
        ModelMetadata? catalogSnapshot,
        ModelMetadata? providerMetadata,
        ModelMetadata? userOverride)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelMetadata.Unknown;
        }

        var resolved = (catalogSnapshot ?? ModelMetadata.Unknown)
            .With(providerMetadata)
            .With(userOverride);
        return resolved.IsKnown ? resolved : ModelMetadata.Unknown;
    }
}
