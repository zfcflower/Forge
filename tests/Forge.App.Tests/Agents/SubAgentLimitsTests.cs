using System.Reflection;
using Forge.App.Agents;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// Pins the bounds and the default of the two sub-agent dials and the mapping from the stored preferences to the record the
/// runtime is handed: the settings section stores a raw integer and the runtime must never see a value it cannot honour, so
/// the clamp lives next to the record both sides use instead of being duplicated in the settings view model - and the
/// bounds are the preference's own constants, not a second copy of 1, 16 and 4. The recording end of the seam (the cap
/// bounding a spawn tree) is covered by <see cref="SessionRuntimeSubAgentLimitsTests"/>.
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

    [Fact]
    public void The_bounds_and_the_default_alias_the_preference_constants()
    {
        // One range, one owner: the section's field, the preference setter and the runtime clamp all read these, so a
        // re-hardcoded 1/16/4 here fails instead of drifting away from what the settings page offers.
        Assert.Equal(AppPreferences.MinAgentsMaxParallelSubAgents, SubAgentLimits.MinParallel);
        Assert.Equal(AppPreferences.MaxAgentsMaxParallelSubAgents, SubAgentLimits.MaxParallelCeiling);
        Assert.Equal(AppPreferences.DefaultAgentsMaxParallelSubAgents, SubAgentLimits.DefaultParallel);
    }

    [Theory]
    [InlineData(SubAgentLimits.MinParallel)]
    [InlineData(7)]
    [InlineData(SubAgentLimits.MaxParallelCeiling)]
    public void The_stored_cap_and_the_stored_switch_reach_the_record(int stored)
    {
        var limits = SubAgentLimits.From(new AppPreferences
        {
            AgentsMaxParallelSubAgents = stored,
            AgentsSubAgentsEnabled = true,
        });

        Assert.Equal(stored, limits.MaxParallel);
        Assert.True(limits.Enabled);
    }

    [Fact]
    public void A_store_without_the_section_maps_to_the_default()
    {
        // The record is what a runtime assembled from an empty settings.json uses: the historical four, spawning on.
        Assert.Equal(SubAgentLimits.Default, SubAgentLimits.From(new AppPreferences()));
    }

    [Fact]
    public void The_switched_off_store_reaches_the_record()
    {
        var limits = SubAgentLimits.From(new AppPreferences { AgentsSubAgentsEnabled = false });

        Assert.False(limits.Enabled);
        Assert.Equal(SubAgentLimits.DefaultParallel, limits.MaxParallel);
    }

    [Theory]
    [InlineData(0, SubAgentLimits.MinParallel)]
    [InlineData(-5, SubAgentLimits.MinParallel)]
    [InlineData(SubAgentLimits.MaxParallelCeiling + 1, SubAgentLimits.MaxParallelCeiling)]
    [InlineData(1000, SubAgentLimits.MaxParallelCeiling)]
    public void A_cap_the_preferences_hold_outside_the_range_is_clamped_on_the_way_to_the_runtime(int stored, int expected)
    {
        var preferences = new AppPreferences();
        WriteRawCap(preferences, stored);

        // The write broke the object's invariant on purpose (the setter and the load path both clamp), so this is the value a
        // future write path that forgets the clamp - or an object built some other way - would hand over. The runtime still
        // never sees it.
        Assert.Equal(stored, preferences.AgentsMaxParallelSubAgents);
        Assert.Equal(expected, SubAgentLimits.From(preferences).MaxParallel);
    }

    /// <summary>
    /// Puts <paramref name="cap"/> into the preference without going through the setter: the setter clamps, so a value outside
    /// the offered range can only be in the object when its field was written directly.
    /// </summary>
    private static void WriteRawCap(AppPreferences preferences, int cap)
    {
        var field = typeof(AppPreferences).GetField(
            "agentsMaxParallelSubAgents",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(
            field is not null,
            "AppPreferences no longer has an 'agentsMaxParallelSubAgents' field; this test writes it directly to skip the setter's clamp.");
        field!.SetValue(preferences, cap);
    }
}
