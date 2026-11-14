using Forge.App.Automations;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Automations;

/// <summary>
/// Pins the mapping from the stored <c>idle-archive</c> preferences to the settings the housekeeping step is handed: the
/// stored days become the window, only the <c>all</c> token widens the scope, the switch carries through, and a store
/// nobody configured maps to the behaviour the step had while it was hard-coded. What the step then does with those values
/// (which sessions it touches, and the exemptions) is covered by <see cref="HousekeepingTests"/>.
/// </summary>
public sealed class IdleArchiveSettingsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(AppPreferences.MaxIdleArchiveDays)]
    public void The_stored_days_become_the_window(int days)
    {
        var settings = IdleArchiveSettings.From(new AppPreferences { IdleArchiveDays = days });

        Assert.Equal(TimeSpan.FromDays(days), settings.After);
        Assert.True(settings.Enabled);
        Assert.Equal(IdleArchiveScope.AutomationSessions, settings.Scope);
    }

    [Fact]
    public void An_unconfigured_store_is_the_previously_hard_coded_step()
    {
        var settings = IdleArchiveSettings.From(new AppPreferences());

        // Seven days, enabled, automation-created root sessions only: the value Default carries, so an upgrade changes nothing.
        Assert.Equal(IdleArchiveSettings.Default, settings);
        Assert.Equal(TimeSpan.FromDays(AppPreferences.DefaultIdleArchiveDays), settings.After);
        Assert.Equal(AppPreferences.DefaultIdleArchiveScope, IdleArchiveSettings.AutomationScopeToken);
    }

    [Theory]
    [InlineData(IdleArchiveSettings.AllScopeToken, true)]
    [InlineData(IdleArchiveSettings.AutomationScopeToken, false)]
    [InlineData("", false)]
    [InlineData("everything", false)]
    public void Only_the_all_token_widens_the_scope(string stored, bool widened)
    {
        var settings = IdleArchiveSettings.From(new AppPreferences { IdleArchiveScope = stored });

        // The store itself turns a missing or unknown token into the default one, so every other value stays automation-only.
        Assert.Equal(widened ? IdleArchiveScope.AllRootSessions : IdleArchiveScope.AutomationSessions, settings.Scope);
    }

    [Fact]
    public void The_widening_token_is_one_the_store_keeps()
    {
        // The token the step compares against has to be a token the preference accepts: one the setter normalizes away would
        // silently narrow the scope back for good.
        var preferences = new AppPreferences { IdleArchiveScope = IdleArchiveSettings.AllScopeToken };

        Assert.Equal(IdleArchiveSettings.AllScopeToken, preferences.IdleArchiveScope);
        Assert.Equal(IdleArchiveScope.AllRootSessions, IdleArchiveSettings.From(preferences).Scope);
    }

    [Fact]
    public void The_switch_off_reaches_the_step()
    {
        var settings = IdleArchiveSettings.From(new AppPreferences { IdleArchiveEnabled = false });

        Assert.False(settings.Enabled);
        Assert.Equal(TimeSpan.FromDays(AppPreferences.DefaultIdleArchiveDays), settings.After);
        Assert.Equal(IdleArchiveScope.AutomationSessions, settings.Scope);
    }
}
