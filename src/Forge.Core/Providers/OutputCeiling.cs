using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The figure a request is sent with as its output cap, in one place: the adapter writes it as <c>max_tokens</c> and
/// <see cref="Agents.CompactionPolicy"/> subtracts it from the window as the output reserve, because a prompt plus a
/// reply has to fit the window together. Today only the Anthropic protocol moves the number: an extended-thinking
/// budget is spent out of the same allowance, so a request whose budget reaches the cap is sent with
/// <c>budget + headroom</c> instead. A reserve computed from the catalogue's figure alone would then be smaller than
/// what the request really carries, and 0.85·(W − R) could sit above the real prompt limit — the context would be
/// over the window before compaction ever starts.
/// </summary>
public static class OutputCeiling
{
    /// <summary>The cap an Anthropic request is sent with when neither the model nor the catalogue names one.</summary>
    public const int DefaultMaxTokens = 8_192;

    /// <summary>Room above a thinking budget for the answer itself: <c>max_tokens</c> has to exceed <c>budget_tokens</c>.</summary>
    public const int ThinkingHeadroom = 4_096;

    /// <summary>Smallest cap a compatible request asks for: below this the model would cut ordinary answers short.</summary>
    public const int CompatibleFloor = 8_192;

    /// <summary>Largest cap a compatible request asks for: the catalogue's figure is a capability, not a promise the gateway honours.</summary>
    public const int CompatibleCeiling = 65_536;

    /// <summary>
    /// The cap a compatible or Responses request carries: the user's own pin when there is one, else the catalogue's output
    /// limit clamped into [<see cref="CompatibleFloor"/>, <see cref="CompatibleCeiling"/>], else null (send nothing). A cap
    /// is what makes the output reserve accurate - without one the budget has to assume 20% of the window is spoken for,
    /// which on a large window reserves six figures for a reply that never grows that far, and folds history far earlier
    /// than it has to. A gateway that refuses the field is handled by the adapter's one-shot downgrade, not here.
    /// </summary>
    public static int? Compatible(int? pinned, int? catalogueLimit) =>
        pinned is { } pin && pin > 0
            ? pin
            : catalogueLimit is { } limit && limit > 0
                ? Math.Clamp(limit, CompatibleFloor, CompatibleCeiling)
                : null;

    /// <summary>The extended-thinking budget a step asks for: 1k, 2k, 8k, 24k, 32k, 48k, or nothing.</summary>
    public static int ThinkingBudget(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => 1_024,
        ThinkingEffort.Low => 2_048,
        ThinkingEffort.Medium => 8_192,
        ThinkingEffort.High => 24_576,
        ThinkingEffort.XHigh => 32_768,
        ThinkingEffort.Max => 49_152,
        _ => 0,
    };

    /// <summary>
    /// The cap a request for <paramref name="model"/> is sent with: the model's own pin, else
    /// <see cref="DefaultMaxTokens"/>, lifted to the thinking budget's room when that would exceed it. There is
    /// deliberately no ceiling parameter: the catalogue's limit is the model's <em>capability</em>, not the figure a request
    /// carries, and letting a caller pass one in is exactly how the reserve drifted above what the adapter sends.
    /// </summary>
    public static int Wire(ModelSettings model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var cap = model.MaxOutputTokens ?? DefaultMaxTokens;
        var budget = ThinkingBudget(model.Thinking);
        return budget >= cap ? budget + ThinkingHeadroom : cap;
    }
}
