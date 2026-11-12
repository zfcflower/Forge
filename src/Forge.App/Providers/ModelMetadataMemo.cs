using System.Threading;
using Forge.Abstractions;

namespace Forge.App.Providers;

/// <summary>
/// A small in-memory memo of resolved <see cref="ModelMetadata"/> per (provider, model).
/// </summary>
/// <remarks>
/// <para>
/// The session strip reads its model metadata from a plain property that is touched many times per frame while a turn
/// streams, and each read used to walk the whole chain (catalog lookup, provider cache, override merge, plus a record
/// allocation per layer). The chain is pure and its three inputs are owned by <see cref="ProviderServices"/>, so the
/// resolved value is cached here and thrown away whenever one of those inputs changes: a settings save, a provider
/// <c>/models</c> refresh, or a successful catalog fetch. Nothing is persisted — a resolved price is derived data, and a
/// stored copy could only ever be a copy that disagrees with its own inputs.
/// </para>
/// <para>
/// Unknown results are cached too: "the catalog does not know this model" is the common answer for a fresh install and the
/// most expensive one to compute, because the fallback walks every provider.
/// </para>
/// </remarks>
internal sealed class ModelMetadataMemo
{
    private readonly Lock gate = new();
    private readonly Dictionary<Key, ModelMetadata> entries = [];

    /// <summary>How many resolved models are held (tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public bool TryGet(string providerId, string modelId, out ModelMetadata metadata)
    {
        lock (gate)
        {
            return entries.TryGetValue(new Key(providerId, modelId), out metadata!);
        }
    }

    public void Set(string providerId, string modelId, ModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (gate)
        {
            entries[new Key(providerId, modelId)] = metadata;
        }
    }

    /// <summary>Drops everything: one of the three inputs changed, so no held answer is trusted any more.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            entries.Clear();
        }
    }

    private readonly record struct Key(string ProviderId, string ModelId);
}
