using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins <see cref="UpdateSchedule.ShouldCheck"/> (wave W10b): <c>daily</c>/<c>weekly</c> are due only after their interval (or
/// when there is no previous check), a recorded <c>failed</c> check is retried after the short failure window instead of the
/// full interval, and <c>never</c> and unknown tokens are never due.
/// </summary>
public sealed class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_first_check_is_due_on_either_frequency()
    {
        Assert.True(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, null, null, Now));
        Assert.True(UpdateSchedule.ShouldCheck(UpdateSchedule.Weekly, null, null, Now));
    }

    [Theory]
    [InlineData(UpdateSchedule.Daily, 0, false)]
    [InlineData(UpdateSchedule.Daily, 1, true)]
    [InlineData(UpdateSchedule.Daily, 2, true)]
    [InlineData(UpdateSchedule.Weekly, 6, false)]
    [InlineData(UpdateSchedule.Weekly, 7, true)]
    [InlineData(UpdateSchedule.Weekly, 14, true)]
    public void A_check_is_due_only_after_the_interval(string frequency, int daysSinceLastCheck, bool expected)
    {
        var last = Now.AddDays(-daysSinceLastCheck);
        Assert.Equal(expected, UpdateSchedule.ShouldCheck(frequency, last, null, Now));
    }

    [Theory]
    [InlineData(UpdateSchedule.Daily, 86_399, false)]
    [InlineData(UpdateSchedule.Daily, 86_400, true)]
    [InlineData(UpdateSchedule.Weekly, 604_799, false)]
    [InlineData(UpdateSchedule.Weekly, 604_800, true)]
    public void An_interval_boundary_is_due_exactly_at_the_interval(string frequency, int secondsSinceLastCheck, bool expected)
    {
        // One second short of the interval must still wait: an off-by-one here either spams the manifest endpoint every hour
        // or skips a day. A completed check (any status but failed) keeps the full interval.
        var last = Now.AddSeconds(-secondsSinceLastCheck);
        Assert.Equal(expected, UpdateSchedule.ShouldCheck(frequency, last, "up-to-date", Now));
    }

    [Theory]
    [InlineData(UpdateSchedule.Daily, 5 * 3_600, false)]
    [InlineData(UpdateSchedule.Daily, 6 * 3_600, true)]
    [InlineData(UpdateSchedule.Weekly, 6 * 3_600, true)]
    [InlineData(UpdateSchedule.Weekly, 1 * 3_600, false)]
    public void A_failed_check_is_retried_after_the_failure_window(string frequency, int secondsSinceLastCheck, bool expected)
    {
        // DOCUMENTED DEFECT D3 (see the W10b test report): a manifest fetch that failed because the machine was offline at
        // launch used to cost a full day (or week) before the next automatic attempt. The recorded status now decides: a
        // failure retries after the short window, a completed check keeps the configured frequency.
        var last = Now.AddSeconds(-secondsSinceLastCheck);
        Assert.Equal(
            expected,
            UpdateSchedule.ShouldCheck(frequency, last, UpdateSchedule.FailedStatus, Now));
    }

    [Fact]
    public void The_failure_window_is_the_boundary_and_not_an_extra_tick()
    {
        var justShort = Now - UpdateSchedule.FailedRetryInterval + TimeSpan.FromSeconds(1);
        var atTheWindow = Now - UpdateSchedule.FailedRetryInterval;

        Assert.False(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, justShort, UpdateSchedule.FailedStatus, Now));
        Assert.True(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, atTheWindow, UpdateSchedule.FailedStatus, Now));
    }

    [Fact]
    public void An_unreadable_status_token_keeps_the_configured_interval()
    {
        // A status written by a newer build (or by hand) is not a failure this build understands; fail quiet, not spammy.
        Assert.False(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, Now.AddHours(-7), "something-new", Now));
        Assert.False(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, Now.AddHours(-7), null, Now));
        Assert.True(UpdateSchedule.ShouldCheck(UpdateSchedule.Daily, Now.AddHours(-25), "something-new", Now));
    }

    [Theory]
    [InlineData(UpdateSchedule.Daily)]
    [InlineData(UpdateSchedule.Weekly)]
    public void A_last_check_in_the_future_is_not_due(string frequency)
    {
        // A wall clock that was set back after a check leaves the recorded time ahead of "now"; the subtraction goes
        // negative, so the pump waits until the clock catches up instead of checking on every tick (fail-quiet, not
        // fail-loud — recorded as an observation in the W10b test report). Also true for a failed check.
        Assert.False(UpdateSchedule.ShouldCheck(frequency, Now.AddHours(1), null, Now));
        Assert.False(UpdateSchedule.ShouldCheck(frequency, Now.AddHours(1), UpdateSchedule.FailedStatus, Now));
    }

    [Theory]
    [InlineData(UpdateSchedule.Never)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Never_and_unknown_tokens_are_never_due(string frequency)
    {
        Assert.False(UpdateSchedule.ShouldCheck(frequency, null, null, Now));
        Assert.False(UpdateSchedule.ShouldCheck(frequency, Now.AddDays(-30), null, Now));
        Assert.False(UpdateSchedule.ShouldCheck(frequency, Now.AddDays(-30), UpdateSchedule.FailedStatus, Now));
    }
}
