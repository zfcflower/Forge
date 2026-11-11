using Forge.Abstractions;
using Forge.Core.Automations;
using Xunit;

namespace Forge.Core.Tests.Automations;

public class AutomationScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // A US-Eastern-like zone built from adjustment rules so the tests do not depend on system tz data.
    // 2025: clocks jump 02:00 -> 03:00 on 9 March and fall back 02:00 -> 01:00 on 2 November.
    private static readonly TimeZoneInfo Eastern = CreateEastern();

    private static TimeZoneInfo CreateEastern()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/Eastern", TimeSpan.FromHours(-5), "Eastern", "EST", "EDT", [rule]);
    }

    private static DateTimeOffset At(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static AutomationSchedule Create(AutomationTrigger trigger)
    {
        Assert.True(AutomationSchedule.TryCreate(trigger, out var schedule, out var error), error);
        return schedule!;
    }

    private static AutomationSchedule Cron(string expression) => Create(new AutomationTrigger(AutomationTriggerKind.Cron) { Cron = expression });

    private static string? CronError(string expression)
    {
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Cron) { Cron = expression }, out var schedule, out var error));
        Assert.Null(schedule);
        return error;
    }

    [Fact]
    public void Daily_picks_the_nearest_of_several_times()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(18, 30), new(9, 0)] });

        Assert.Equal(At(2025, 6, 2, 18, 30), s.Next(At(2025, 6, 2, 9, 0), Utc));
        Assert.Equal(At(2025, 6, 3, 9, 0), s.Next(At(2025, 6, 2, 18, 30), Utc));
    }

    [Fact]
    public void Next_is_strictly_after_the_given_moment()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(12, 0)] });

        Assert.Equal(At(2025, 6, 3, 12, 0), s.Next(At(2025, 6, 2, 12, 0), Utc));
        Assert.Equal(At(2025, 6, 2, 12, 0), s.Next(At(2025, 6, 2, 11, 59, 59), Utc));
    }

    [Fact]
    public void Hourly_counts_from_midnight()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 6, Minute = 15 });

        Assert.Equal(At(2025, 6, 2, 12, 15), s.Next(At(2025, 6, 2, 7, 0), Utc));
        Assert.Equal(At(2025, 6, 3, 0, 15), s.Next(At(2025, 6, 2, 18, 15), Utc));
    }

    [Fact]
    public void Weekly_combines_days_and_times()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Monday, DayOfWeek.Friday], Times = [new(8, 0), new(20, 0)] });

        // 2025-06-02 is a Monday.
        Assert.Equal(At(2025, 6, 2, 20, 0), s.Next(At(2025, 6, 2, 8, 0), Utc));
        Assert.Equal(At(2025, 6, 6, 8, 0), s.Next(At(2025, 6, 2, 20, 0), Utc));
    }

    [Fact]
    public void Feb_30_has_no_next_time()
    {
        Assert.Null(Cron("0 0 30 2 *").Next(At(2025, 1, 1), Utc));
    }

    [Fact]
    public void Day_of_month_and_day_of_week_both_restricted_means_or()
    {
        var s = Cron("0 0 13 * Fri");

        // 2025-06-06 is a Friday, before the 13th.
        Assert.Equal(At(2025, 6, 6), s.Next(At(2025, 6, 1), Utc));
        // 2025-06-13 is both; after it the next Friday is the 20th.
        Assert.Equal(At(2025, 6, 13), s.Next(At(2025, 6, 12, 12), Utc));
        Assert.Equal(At(2025, 6, 20), s.Next(At(2025, 6, 13), Utc));
        // The 13th of a month that is not a Friday still counts (2025-09-13 is a Saturday).
        Assert.Equal(At(2025, 9, 13), s.Next(At(2025, 9, 12), Utc));
    }

    [Fact]
    public void Only_one_restricted_day_field_means_and()
    {
        // Every day of month, Mondays only.
        Assert.Equal(At(2025, 6, 9), Cron("0 0 * * Mon").Next(At(2025, 6, 2), Utc));
    }

    [Fact]
    public void Steps_ranges_and_lists()
    {
        Assert.Equal(At(2025, 6, 2, 10, 15), Cron("*/15 * * * *").Next(At(2025, 6, 2, 10, 7), Utc));
        Assert.Equal(At(2025, 6, 2, 10, 30), Cron("10-40/20 * * * *").Next(At(2025, 6, 2, 10, 11), Utc));
        Assert.Equal(At(2025, 6, 2, 15, 5), Cron("5 9-17/3,21 * * *").Next(At(2025, 6, 2, 12, 5), Utc));
        Assert.Equal(At(2025, 6, 2, 21, 5), Cron("5 9-17/3,21 * * *").Next(At(2025, 6, 2, 15, 5), Utc));
        // A step after a lone value runs to the end of the field.
        Assert.Equal(At(2025, 6, 2, 10, 35), Cron("5/30 * * * *").Next(At(2025, 6, 2, 10, 6), Utc));
    }

    [Fact]
    public void Sunday_may_be_written_0_or_7()
    {
        // 2025-06-02 is a Monday; the next Sunday is the 8th.
        Assert.Equal(At(2025, 6, 8, 9, 0), Cron("0 9 * * 7").Next(At(2025, 6, 2), Utc));
        Assert.Equal(At(2025, 6, 8, 9, 0), Cron("0 9 * * 0").Next(At(2025, 6, 2), Utc));
        Assert.Equal(At(2025, 6, 8, 9, 0), Cron("0 9 * * SUN").Next(At(2025, 6, 2), Utc));
        Assert.Equal(At(2025, 6, 7, 9, 0), Cron("0 9 * * 6-7").Next(At(2025, 6, 2), Utc));
    }

    [Theory]
    [InlineData("0 9 * jun mon")]
    [InlineData("0 9 * JUN MON")]
    [InlineData("0 9 * Jun Mon")]
    public void Month_and_day_names_ignore_case(string expression)
    {
        // First Monday of... (only Mondays in June): 2025-06-02.
        Assert.Equal(At(2025, 6, 2, 9, 0), Cron(expression).Next(At(2025, 1, 1), Utc));
    }

    [Fact]
    public void Month_range_by_name()
    {
        Assert.Equal(At(2025, 3, 1), Cron("0 0 1 mar-may *").Next(At(2025, 1, 1), Utc));
        Assert.Equal(At(2026, 3, 1), Cron("0 0 1 mar-may *").Next(At(2025, 5, 2), Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("61 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("a b c d e")]
    [InlineData("* * * foo *")]
    [InlineData("* * * * funday")]
    [InlineData("-5 * * * *")]
    [InlineData(", * * * *")]
    public void Invalid_cron_is_reported_without_throwing(string expression)
    {
        Assert.False(string.IsNullOrWhiteSpace(CronError(expression)));
    }

    [Fact]
    public void Null_cron_is_an_error()
    {
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Cron), out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(13, 0)]
    [InlineData(1, 60)]
    [InlineData(1, -1)]
    public void Invalid_hourly_is_reported(int every, int minute)
    {
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = every, Minute = minute }, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Invalid_daily_and_weekly_are_reported()
    {
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Daily), out _, out var e1));
        Assert.NotNull(e1);
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(24, 0)] }, out _, out var e2));
        Assert.NotNull(e2);
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Weekly) { Times = [new(1, 0)] }, out _, out var e3));
        Assert.NotNull(e3);
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Weekly) { Days = [DayOfWeek.Monday] }, out _, out var e4));
        Assert.NotNull(e4);
        Assert.False(AutomationSchedule.TryCreate(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(1, 60)] }, out _, out var e5));
        Assert.NotNull(e5);
    }

    [Fact]
    public void Forward_dst_jump_fires_a_skipped_time_when_it_would_have_been_reached()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(2, 30)] });

        // 02:30 on 2025-03-09 does not exist; it is due 30 minutes into the lost hour = 03:30 EDT = 07:30Z.
        var next = s.Next(At(2025, 3, 8, 12), Eastern);
        Assert.Equal(At(2025, 3, 9, 7, 30), next);
        // The day before and after are ordinary: 02:30 EST = 07:30Z, 02:30 EDT = 06:30Z.
        Assert.Equal(At(2025, 3, 10, 6, 30), s.Next(next!.Value, Eastern));
        Assert.Equal(At(2025, 3, 8, 7, 30), s.Next(At(2025, 3, 7, 12), Eastern));
    }

    [Fact]
    public void Backward_dst_jump_fires_a_repeated_time_only_the_first_time()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(1, 30)] });

        // 01:30 on 2025-11-02 happens twice; the first is 01:30 EDT = 05:30Z.
        var first = s.Next(At(2025, 11, 1, 12), Eastern);
        Assert.Equal(At(2025, 11, 2, 5, 30), first);
        // The repeat (01:30 EST = 06:30Z) is not a fire; the next is the following day, 01:30 EST = 06:30Z on the 3rd.
        Assert.Equal(At(2025, 11, 3, 6, 30), s.Next(first!.Value, Eastern));
        Assert.Equal(At(2025, 11, 3, 6, 30), s.Next(At(2025, 11, 2, 6, 0), Eastern));
    }

    [Fact]
    public void Hourly_across_the_fall_back_does_not_double_fire_the_repeated_hour()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Hourly) { IntervalHours = 1, Minute = 0 });

        var fires = s.NextOccurrences(At(2025, 11, 2, 4, 30), Eastern, 5);

        // 01:00 EDT (05:00Z), then 02:00 EST (07:00Z): 06:00Z (the repeated 01:00) is not a fire.
        Assert.Equal([At(2025, 11, 2, 5, 0), At(2025, 11, 2, 7, 0), At(2025, 11, 2, 8, 0), At(2025, 11, 2, 9, 0), At(2025, 11, 2, 10, 0)], fires);
    }

    [Fact]
    public void NextOccurrences_lists_the_requested_count_in_order()
    {
        var s = Create(new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new(9, 0)] });

        var list = s.NextOccurrences(At(2025, 6, 2, 10), Utc, 5);

        Assert.Equal(5, list.Count);
        Assert.Equal(At(2025, 6, 3, 9), list[0]);
        Assert.Equal(At(2025, 6, 7, 9), list[4]);
    }

    [Fact]
    public void NextOccurrences_of_an_impossible_schedule_is_empty()
    {
        Assert.Empty(Cron("0 0 31 2 *").NextOccurrences(At(2025, 1, 1), Utc, 5));
    }

    [Fact]
    public void Leap_day_is_found_within_the_horizon()
    {
        Assert.Equal(At(2028, 2, 29), Cron("0 0 29 2 *").Next(At(2025, 3, 1), Utc));
    }
}
