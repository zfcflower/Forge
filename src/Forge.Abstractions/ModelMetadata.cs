namespace Forge.Abstractions;

/// <summary>
/// What one model costs, in US dollars per one million tokens. Cache-read and cache-write are the two cheaper (or, for
/// writes, more expensive) cache paths providers bill separately; both are zero for models that do not bill them.
/// </summary>
public sealed record ModelPricing(double Input, double Output, double CacheRead, double CacheWrite)
{
    /// <summary>Every path at zero: a local or self-hosted model that costs nothing per token.</summary>
    public static ModelPricing None { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Everything the shell knows about one model: how large its context window is and what it costs. Either half may be
/// unknown (<c>null</c>), and a provider that reports nothing leaves both unknown; the session instrumentation then shows
/// the token counts it does have instead of inventing a percentage or a price.
/// </summary>
public sealed record ModelMetadata(int? ContextWindow, ModelPricing? Pricing)
{
    /// <summary>Nothing is known: no window, no prices.</summary>
    public static ModelMetadata Unknown { get; } = new(null, null);

    /// <summary>At least one half carries a value; a fully unknown metadata is never worth showing.</summary>
    public bool IsKnown => ContextWindow is > 0 || Pricing is not null;

    /// <summary>
    /// Field-wise merge: every field <paramref name="other"/> knows replaces the one here, every unknown field of
    /// <paramref name="other"/> leaves this value in place. Resolving metadata is a chain of these merges, ordered from
    /// the weakest source to the strongest, so the last merge wins wherever it speaks.
    /// </summary>
    public ModelMetadata With(ModelMetadata? other)
    {
        if (other is null || !other.IsKnown)
        {
            return this;
        }

        return new ModelMetadata(other.ContextWindow is > 0 ? other.ContextWindow : ContextWindow, other.Pricing ?? Pricing);
    }
}
