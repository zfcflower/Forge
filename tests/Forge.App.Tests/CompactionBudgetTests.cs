using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The raw tail a compaction keeps is the one number a large-window model gets wrong for free: derived it is min(20 000,
/// 0.2·U), which on a 1M window is under three percent of the budget the model has. A configured figure replaces it, and
/// still has to leave the target reachable.
/// </summary>
public class CompactionBudgetTests
{
    [Fact]
    public void A_configured_raw_tail_replaces_the_derived_one_and_stays_inside_the_budget()
    {
        // 170k window, unknown output cap: R = 34k, U = 136k, so the derived tail is min(20 000, 27 200) = 20 000.
        Assert.Equal(20_000, CompactionPolicy.Budget(170_000, null)!.Value.KeepRecentTokens);

        // A 1M-window model keeps the figure the settings text suggests for it instead of the derived 20 000.
        Assert.Equal(100_000, CompactionPolicy.Budget(1_000_000, null, 100_000)!.Value.KeepRecentTokens);

        // The configured figure is capped at half of U: 500 000 would otherwise make the target unreachable.
        Assert.Equal(68_000, CompactionPolicy.Budget(170_000, null, 500_000)!.Value.KeepRecentTokens);

        // With no known window there is no budget at all, configured tail or not.
        Assert.Null(CompactionPolicy.Budget(null, null, 50_000));
    }

    [Fact]
    public void A_known_reply_cap_shrinks_the_output_reserve_to_what_the_request_actually_allows()
    {
        // 200k window, reply cap unknown: the reserve is the 20% guess, so 40k of usable input is held back.
        var guessed = CompactionPolicy.Budget(200_000, null)!.Value;
        Assert.Equal(40_000, guessed.Reserve);
        Assert.Equal(160_000, guessed.Usable);

        // The same window with Claude's real 8192-token reply cap: the reserve is that cap, and the 32k it no longer holds
        // back belongs to the context (the trigger line moves out with it).
        var known = CompactionPolicy.Budget(200_000, 8_192)!.Value;
        Assert.Equal(8_192, known.Reserve);
        Assert.Equal(191_808, known.Usable);
        Assert.Equal(163_036, known.TriggerTokens(0.85));
    }

    [Fact]
    public void The_mid_turn_line_has_to_sit_above_the_trigger()
    {
        // A mid-turn line at or below the trigger would turn every trigger hit into a mid-turn compaction.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompactionPolicy { TriggerRatio = 0.85, EmergencyRatio = 0.8 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompactionPolicy { TriggerRatio = 0.85, EmergencyRatio = 0.85 }.Validate());

        new CompactionPolicy { TriggerRatio = 0.85, EmergencyRatio = 0.86 }.Validate();

        // The emergency line is the same share of the same usable budget.
        var budget = CompactionPolicy.Budget(170_000, null)!.Value;
        Assert.Equal(129_200, budget.EmergencyTokens(0.95));
    }
}
