using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// The <c>agents</c> settings section: the sub-agent cap and the spawn switch edit the live <see cref="AppPreferences"/>
/// directly rather than a copy, so a value written anywhere else shows through the property and a property write lands in the
/// preference the runtime reads before every turn. The field offers exactly the range the preference setter accepts, and a
/// value typed outside it re-reads as the clamped one.
/// </summary>
public sealed class AgentsSettingsSectionTests
{
    [Fact]
    public void The_cap_round_trips_to_the_preference()
    {
        var preferences = new AppPreferences();
        var section = new AgentsSettingsSectionViewModel(preferences);
        Assert.Equal(AppPreferences.DefaultAgentsMaxParallelSubAgents, section.MaxParallel);

        section.MaxParallel = 3;
        Assert.Equal(3, preferences.AgentsMaxParallelSubAgents);

        // A raw write to the preference is visible through the property: the section holds no copy of the value.
        preferences.AgentsMaxParallelSubAgents = 5;
        Assert.Equal(5, section.MaxParallel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(17)]
    [InlineData(1000)]
    public void A_cap_outside_the_range_lands_and_re_reads_clamped(int written)
    {
        var preferences = new AppPreferences();
        var section = new AgentsSettingsSectionViewModel(preferences);

        section.MaxParallel = written;

        var clamped = Math.Clamp(written, AppPreferences.MinAgentsMaxParallelSubAgents, AppPreferences.MaxAgentsMaxParallelSubAgents);
        Assert.Equal(clamped, preferences.AgentsMaxParallelSubAgents);
        Assert.Equal(clamped, section.MaxParallel);
    }

    [Fact]
    public void The_field_range_is_the_preference_range()
    {
        var section = new AgentsSettingsSectionViewModel(new AppPreferences());

        // The spinner's bounds and the setter's clamp are one range: a smaller field range would hide values the preference
        // accepts, a larger one would offer values it silently rejects.
        Assert.Equal(AppPreferences.MinAgentsMaxParallelSubAgents, section.MinParallel);
        Assert.Equal(AppPreferences.MaxAgentsMaxParallelSubAgents, section.MaxParallelLimit);

        // The untouched store's value is inside that range, so the field never opens on a value it would reject.
        Assert.InRange(section.MaxParallel, section.MinParallel, section.MaxParallelLimit);
    }

    [Fact]
    public void The_spawn_switch_round_trips_to_the_preference()
    {
        var preferences = new AppPreferences();
        var section = new AgentsSettingsSectionViewModel(preferences);
        Assert.True(section.SubAgentsEnabled);

        section.SubAgentsEnabled = false;
        Assert.False(preferences.AgentsSubAgentsEnabled);
        Assert.False(section.SubAgentsEnabled);

        // Written from elsewhere, it is read back without a reload: the next turn reads the live value.
        preferences.AgentsSubAgentsEnabled = true;
        Assert.True(section.SubAgentsEnabled);
    }

    [Fact]
    public void A_write_raises_the_property_that_moved()
    {
        var section = new AgentsSettingsSectionViewModel(new AppPreferences());
        var raised = PropertyChangeLog.For(section);

        section.MaxParallel = 2;
        Assert.Equal([nameof(AgentsSettingsSectionViewModel.MaxParallel)], raised);

        raised.Clear();
        section.SubAgentsEnabled = false;
        Assert.Equal([nameof(AgentsSettingsSectionViewModel.SubAgentsEnabled)], raised);
    }
}
