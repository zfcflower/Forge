namespace Forge.App.Automations;

/// <summary>Which root sessions the idle-archive step of <see cref="BuiltInAutomations.Housekeeping"/> may archive.</summary>
internal enum IdleArchiveScope
{
    /// <summary>Only root sessions an automation created: what the step did before it became configurable.</summary>
    AutomationSessions,

    /// <summary>Any root session, including the ones the user created.</summary>
    AllRootSessions,
}

/// <summary>
/// The idle-archive step of <see cref="BuiltInAutomations.Housekeeping"/>: whether it runs at all, how long a root session
/// must have been idle, and which sessions it may touch. <see cref="HousekeepingRunner"/> reads it through a factory
/// delegate once per run, so a value from the live settings takes effect without a restart; the other exemptions of the
/// step (open sessions, sub-sessions, fork sessions a weekly automation reuses) are not configurable.
/// </summary>
internal sealed record IdleArchiveSettings(bool Enabled, TimeSpan After, IdleArchiveScope Scope)
{
    /// <summary>The stored scope token of <see cref="IdleArchiveScope.AutomationSessions"/>; the default.</summary>
    public const string AutomationScopeToken = "automation";

    /// <summary>The stored scope token of <see cref="IdleArchiveScope.AllRootSessions"/>.</summary>
    public const string AllScopeToken = "all";

    /// <summary>
    /// The behaviour the step had while it was hard-coded: enabled, a seven-day window
    /// (<see cref="HousekeepingRunner.ArchiveAfter"/>) and automation-created root sessions only. Kept as the default so an
    /// unconfigured runner archives exactly what it archived before and every existing install behaves the same after an
    /// upgrade; a factory that returns other values overrides all three.
    /// </summary>
    public static IdleArchiveSettings Default { get; } = new(true, HousekeepingRunner.ArchiveAfter, IdleArchiveScope.AutomationSessions);

    /// <summary>
    /// The settings the automation service is handed: the stored days turn into the window here, and only the token
    /// <see cref="AllScopeToken"/> widens the scope, so an unknown or missing token stays at the automation-only default.
    /// </summary>
    public static IdleArchiveSettings From(Preferences.AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var scope = string.Equals(preferences.IdleArchiveScope, AllScopeToken, StringComparison.Ordinal)
            ? IdleArchiveScope.AllRootSessions
            : IdleArchiveScope.AutomationSessions;
        return new IdleArchiveSettings(preferences.IdleArchiveEnabled, TimeSpan.FromDays(preferences.IdleArchiveDays), scope);
    }
}
