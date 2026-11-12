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
/// Which kinds of content a model accepts as input, as a bit set. A model that reports nothing leaves this unknown instead
/// of <see cref="None"/>, because "accepts no input" is not a state a chat model is ever in.
/// </summary>
[Flags]
public enum ModelInputs
{
    None = 0,
    Text = 1,
    Image = 2,
    Pdf = 4,
    Audio = 8,
    Video = 16,
}

/// <summary>
/// What one model can do beyond costing money: the content it takes as input and the reasoning steps it accepts. Both
/// halves may be unknown (<c>null</c>), and half-unknown is the normal case — a catalog entry often lists modalities and no
/// step map.
/// </summary>
/// <param name="Inputs">The accepted input kinds, or <c>null</c> when the source said nothing about them.</param>
/// <param name="ReasoningSteps">
/// The reasoning steps the model accepts, ascending and always starting with <see cref="ThinkingEffort.Off"/>; <c>null</c>
/// means the source said nothing, which is not the same as "this model cannot think" (that is <c>[Off]</c>).
/// </param>
public sealed record ModelCapabilities(ModelInputs? Inputs, IReadOnlyList<ThinkingEffort>? ReasoningSteps)
{
    /// <summary>Nothing is known: neither the input kinds nor anything about reasoning.</summary>
    public static ModelCapabilities Unknown { get; } = new(null, null);

    /// <summary>
    /// Whether the model reasons at all, when a source said so without listing steps (a catalog entry often carries
    /// <c>reasoning: true</c> plus an on/off toggle). <c>null</c> is unknown; it is not the same as "does not reason".
    /// </summary>
    public bool? Reasoning { get; init; }

    /// <summary>At least one half carries a value.</summary>
    public bool IsKnown => Inputs is not null || ReasoningSteps is not null || Reasoning is not null;

    /// <summary>
    /// Field-wise merge, in the same direction as <see cref="ModelMetadata.With"/>: every field <paramref name="other"/>
    /// knows replaces the one here, every unknown field of <paramref name="other"/> leaves this value in place.
    /// </summary>
    public ModelCapabilities With(ModelCapabilities? other)
    {
        if (other is null || !other.IsKnown)
        {
            return this;
        }

        return new ModelCapabilities(other.Inputs ?? Inputs, other.ReasoningSteps ?? ReasoningSteps)
        {
            Reasoning = other.Reasoning ?? Reasoning,
        };
    }
}

/// <summary>
/// Everything the shell knows about one model: how large its context window is, what it costs and what it accepts. Every
/// half may be unknown (<c>null</c>), and a provider that reports nothing leaves it unknown; the session instrumentation
/// then shows the token counts it does have instead of inventing a percentage or a price.
/// </summary>
public sealed record ModelMetadata(int? ContextWindow, ModelPricing? Pricing)
{
    /// <summary>Nothing is known: no window, no prices, no capabilities.</summary>
    public static ModelMetadata Unknown { get; } = new(null, null);

    /// <summary>What the model accepts, when any source described it.</summary>
    public ModelCapabilities? Capabilities { get; init; }

    /// <summary>At least one half carries a value; a fully unknown metadata is never worth showing.</summary>
    public bool IsKnown => ContextWindow is > 0 || Pricing is not null || Capabilities is { IsKnown: true };

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

        var capabilities = Capabilities is null
            ? other.Capabilities
            : other.Capabilities is null
                ? Capabilities
                : Capabilities.With(other.Capabilities);

        return new ModelMetadata(other.ContextWindow is > 0 ? other.ContextWindow : ContextWindow, other.Pricing ?? Pricing)
        {
            Capabilities = capabilities?.IsKnown == true ? capabilities : null,
        };
    }
}
