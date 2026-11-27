using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The budget a compaction works against: the usable input U = W − R, where the reserve R is the cap the request actually
/// carries rather than the catalogue's ceiling. Getting R wrong moves the trigger line above the real prompt limit, and a
/// compaction then cannot fire before the provider refuses the request.
/// </summary>
public class CompactionBudgetTests
{
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
