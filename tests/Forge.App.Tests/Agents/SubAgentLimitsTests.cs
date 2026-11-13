using Forge.App.Agents;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// Pins the bounds and the default of the two sub-agent dials: the settings section stores a raw integer and the runtime
/// must never see a value it cannot honour, so the clamp lives next to the record both sides use instead of being
/// duplicated in the settings view model.
/// </summary>
public sealed class SubAgentLimitsTests
{
    [Theory]
    [InlineData(-5, SubAgentLimits.MinParallel)]
    [InlineData(0, SubAgentLimits.MinParallel)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(SubAgentLimits.MaxParallelCeiling, SubAgentLimits.MaxParallelCeiling)]
    [InlineData(SubAgentLimits.MaxParallelCeiling + 1, SubAgentLimits.MaxParallelCeiling)]
    [InlineData(1000, SubAgentLimits.MaxParallelCeiling)]
    public void A_stored_cap_is_clamped_into_the_offered_range(int stored, int expected) =>
        Assert.Equal(expected, SubAgentLimits.Clamp(stored));

    [Fact]
    public void The_default_is_the_historical_behaviour_and_lies_inside_the_offered_range()
    {
        // What AgentOptions.MaxParallelSubAgents carried while it was hard-coded, with spawning on; the settings range has to
        // contain it, or an untouched install would be shown a value it never chose.
        Assert.True(SubAgentLimits.Default.Enabled);
        Assert.Equal(4, SubAgentLimits.Default.MaxParallel);
        Assert.InRange(SubAgentLimits.Default.MaxParallel, SubAgentLimits.MinParallel, SubAgentLimits.MaxParallelCeiling);
    }
}
