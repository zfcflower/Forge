using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.Automations.Settings;

/// <summary>Next fire times of a trigger (local clock), or why the trigger is invalid.</summary>
internal sealed record AutomationPreview(IReadOnlyList<DateTimeOffset> Next, string? Error);

/// <summary>A project the editor can pick; only repository projects can host a fixed fork or a CI auto-push switch.</summary>
internal sealed record AutomationProjectChoice(string Id, string Name, bool IsRepository);

/// <summary>An active root session a Scout run can attach to.</summary>
internal sealed record AutomationSessionChoice(string Id, string Title, string? ProjectName)
{
    public string Display => string.IsNullOrEmpty(ProjectName) ? Title : Title + " - " + ProjectName;
}

/// <summary>Human text for triggers, statuses and times; shared by the list, the run history and the editor.</summary>
internal static class AutomationText
{
    internal const string TimeFormat = "yyyy-MM-dd HH:mm";

    /// <summary>Built-ins that only ever run from an event and need a target, so "run now" does not apply.</summary>
    public static bool IsEventOnly(string automationId) =>
        automationId is BuiltInAutomations.QuotaResume or BuiltInAutomations.CiChain or BuiltInAutomations.SessionWatch;

    public static string DisplayName(AutomationDefinition definition) => definition.Id switch
    {
        BuiltInAutomations.QuotaResume => Loc.Instance[AutomationSettingsStrings.BuiltInQuotaResumeKey],
        BuiltInAutomations.CiChain => Loc.Instance[AutomationSettingsStrings.BuiltInCiChainKey],
        BuiltInAutomations.SessionWatch => Loc.Instance[AutomationSettingsStrings.BuiltInSessionWatchKey],
        BuiltInAutomations.ModelCatalogRefresh => Loc.Instance[AutomationSettingsStrings.BuiltInModelCatalogKey],
        BuiltInAutomations.Housekeeping => Loc.Instance[AutomationSettingsStrings.BuiltInHousekeepingKey],
        _ => definition.Name,
    };

    public static string Time(AutomationTime time) =>
        time.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + time.Minute.ToString("00", CultureInfo.InvariantCulture);

    public static string Moment(DateTimeOffset moment) => moment.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static string DayName(DayOfWeek day) => Loc.Instance[day switch
    {
        DayOfWeek.Monday => AutomationSettingsStrings.DayMondayKey,
        DayOfWeek.Tuesday => AutomationSettingsStrings.DayTuesdayKey,
        DayOfWeek.Wednesday => AutomationSettingsStrings.DayWednesdayKey,
        DayOfWeek.Thursday => AutomationSettingsStrings.DayThursdayKey,
        DayOfWeek.Friday => AutomationSettingsStrings.DayFridayKey,
        DayOfWeek.Saturday => AutomationSettingsStrings.DaySaturdayKey,
        _ => AutomationSettingsStrings.DaySundayKey,
    }];

    /// <summary>Monday first, the way a week reads in a settings page.</summary>
    public static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    public static string Summarize(AutomationTrigger trigger)
    {
        var loc = Loc.Instance;
        var times = string.Join(", ", trigger.Times.Select(Time));
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Hourly:
                var minute = trigger.Minute.ToString(CultureInfo.InvariantCulture);
                return trigger.IntervalHours <= 1
                    ? loc.Format(AutomationSettingsStrings.TriggerHourlyOneKey, minute)
                    : loc.Format(AutomationSettingsStrings.TriggerHourlyManyKey, trigger.IntervalHours.ToString(CultureInfo.InvariantCulture), minute);
            case AutomationTriggerKind.Daily:
                return loc.Format(AutomationSettingsStrings.TriggerDailyKey, times);
            case AutomationTriggerKind.Weekly:
                var days = string.Join(
                    loc[AutomationSettingsStrings.DaySeparatorKey],
                    WeekOrder.Where(trigger.Days.Contains).Select(DayName));
                return loc.Format(AutomationSettingsStrings.TriggerWeeklyKey, days, times);
            default:
                return loc.Format(AutomationSettingsStrings.TriggerCronKey, trigger.Cron ?? string.Empty);
        }
    }

    /// <summary>All triggers of a definition joined, or "manual only" / "event-driven" when it has none.</summary>
    public static string Summarize(AutomationDefinition definition)
    {
        if (definition.Triggers.Count == 0)
        {
            return Loc.Instance[IsEventOnly(definition.Id)
                ? AutomationSettingsStrings.TriggerEventKey
                : AutomationSettingsStrings.TriggerManualKey];
        }

        return string.Join("; ", definition.Triggers.Select(Summarize));
    }

    public static string Status(AutomationRunStatus status) => Loc.Instance[status switch
    {
        AutomationRunStatus.Running => AutomationSettingsStrings.StatusRunningKey,
        AutomationRunStatus.WaitingIdle => AutomationSettingsStrings.StatusWaitingIdleKey,
        AutomationRunStatus.WaitingQuota => AutomationSettingsStrings.StatusWaitingQuotaKey,
        AutomationRunStatus.Completed => AutomationSettingsStrings.StatusCompletedKey,
        AutomationRunStatus.Failed => AutomationSettingsStrings.StatusFailedKey,
        AutomationRunStatus.Cancelled => AutomationSettingsStrings.StatusCancelledKey,
        AutomationRunStatus.Interrupted => AutomationSettingsStrings.StatusInterruptedKey,
        _ => AutomationSettingsStrings.StatusSkippedKey,
    }];

    public static string Source(AutomationTriggerSource source) => Loc.Instance[source switch
    {
        AutomationTriggerSource.Schedule => AutomationSettingsStrings.SourceScheduleKey,
        AutomationTriggerSource.CatchUp => AutomationSettingsStrings.SourceCatchUpKey,
        AutomationTriggerSource.Manual => AutomationSettingsStrings.SourceManualKey,
        AutomationTriggerSource.Event => AutomationSettingsStrings.SourceEventKey,
        _ => AutomationSettingsStrings.SourceChainKey,
    }];
}
