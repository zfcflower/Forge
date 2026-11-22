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
