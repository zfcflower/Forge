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

    [Fact]
    public void The_reserve_is_the_cap_the_request_carries_not_the_catalogue_ceiling()
    {
        // 200k window, catalogue says 64 000, but the request is sent with 8 192 (the protocol default): that is the reserve.
        Assert.Equal(8_192, CompactionPolicy.Budget(200_000, 64_000, wireMaxOutput: 8_192)!.Value.Reserve);

        // A thinking budget lifts it to 53 248. Capping that at a fifth of the window (40 000) is the bug: with the real cap
        // as the reserve the trigger line is 0.85·(W − R), which is structurally below the prompt limit W − R.
        var wire = CompactionPolicy.Budget(200_000, 64_000, wireMaxOutput: 53_248)!.Value;
        Assert.Equal(53_248, wire.Reserve);
        Assert.Equal(146_752, wire.Usable);
        Assert.True(wire.TriggerTokens(0.85) < 200_000 - 53_248);
        Assert.True(wire.EmergencyTokens(0.95) < 200_000 - 53_248);
    }

    [Fact]
    public void A_pinned_cap_on_a_protocol_that_only_sends_what_it_is_given_is_the_reserve()
    {
        // The compatible and Responses protocols write max_completion_tokens/max_output_tokens exactly when the model pins
        // one, so that pin is the wire figure and must not be clamped to a fifth of the window: a pinned 100k on a 200k
        // window would otherwise reserve 40k and let the 136k trigger line sit above the 100k prompt limit.
        var pinned = CompactionPolicy.Budget(200_000, 100_000, wireMaxOutput: 100_000)!.Value;
        Assert.Equal(100_000, pinned.Reserve);
        Assert.Equal(100_000, pinned.Usable);
        Assert.True(pinned.TriggerTokens(0.85) < 200_000 - 100_000);

        // Without a wire figure the reserve stays a capability guess: the catalogue's 100k clamped to 20% of the window.
        Assert.Equal(40_000, CompactionPolicy.Budget(200_000, 100_000)!.Value.Reserve);
    }

    [Fact]
    public void An_output_cap_that_leaves_no_input_room_is_not_a_budget()
    {
        // A request whose own output cap fills the window has nothing left to compact toward; the overflow fallback still
        // applies, but automatic compaction must not pretend there is a usable budget.
        Assert.Null(CompactionPolicy.Budget(32_000, null, wireMaxOutput: 32_000));
        Assert.Null(CompactionPolicy.Budget(32_000, null, wireMaxOutput: 36_864));
        Assert.Null(CompactionPolicy.Budget(null, null));

        // One token of input room is still a budget; whether anything can be compacted into it is the floor's decision.
        var narrow = CompactionPolicy.Budget(32_000, null, wireMaxOutput: 31_999)!.Value;
        Assert.Equal(1, narrow.Usable);
    }
}
