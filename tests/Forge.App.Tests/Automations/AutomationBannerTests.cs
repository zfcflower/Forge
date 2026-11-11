using Forge.Abstractions;
using Forge.App.Localization;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class AutomationBannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private static AutomationRun Quota(DateTimeOffset? resumeAt) =>
        new("r1", BuiltInAutomations.QuotaResume, "Q", AutomationTriggerSource.Event, "e1", AutomationRunStatus.WaitingQuota, Now)
        {
            ResumeAt = resumeAt,
        };

    [Fact]
    public void Remaining_time_rounds_up_to_the_minute_and_switches_to_hours()
    {
        Assert.Equal(Loc.Instance[AutomationStrings.BannerSoonKey], AutomationStrings.BannerRemaining(TimeSpan.FromSeconds(20)));
        Assert.Equal(Loc.Instance[AutomationStrings.BannerSoonKey], AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(-3)));
        Assert.Equal(Loc.Instance.Format(AutomationStrings.BannerInMinutesKey, "1"), AutomationStrings.BannerRemaining(TimeSpan.FromSeconds(31)));
        Assert.Equal(Loc.Instance.Format(AutomationStrings.BannerInMinutesKey, "5"), AutomationStrings.BannerRemaining(TimeSpan.FromSeconds(241)));
        Assert.Equal(Loc.Instance.Format(AutomationStrings.BannerInMinutesKey, "59"), AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(59)));
        Assert.Equal(Loc.Instance.Format(AutomationStrings.BannerInHoursKey, "1", "00"), AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(60)));
        Assert.Equal(Loc.Instance.Format(AutomationStrings.BannerInHoursKey, "2", "05"), AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(124.5)));
    }

    [Fact]
    public void A_quota_banner_counts_down_against_the_clock()
    {
        var run = Quota(Now.AddMinutes(30));

        var early = AutomationStrings.Banner(run, Now);
        var later = AutomationStrings.Banner(run, Now.AddMinutes(20));

        Assert.Equal(AutomationStrings.BannerWaitingQuota(TextFormat.ShortStamp(Now.AddMinutes(30)), AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(30))), early);
        Assert.Equal(AutomationStrings.BannerWaitingQuota(TextFormat.ShortStamp(Now.AddMinutes(30)), AutomationStrings.BannerRemaining(TimeSpan.FromMinutes(10))), later);
        Assert.NotEqual(early, later);
    }

    [Fact]
    public void Other_states_have_fixed_banners()
    {
        Assert.Equal(AutomationStrings.BannerWaitingQuotaProbe, AutomationStrings.Banner(Quota(null), Now));

        var idle = Quota(null) with { Status = AutomationRunStatus.WaitingIdle };
        Assert.Equal(AutomationStrings.BannerWaitingIdle(AutomationStrings.DisplayName(idle.AutomationId, idle.AutomationName)), AutomationStrings.Banner(idle, Now));

        var running = Quota(null) with { Status = AutomationRunStatus.Running };
        Assert.Equal(AutomationStrings.BannerRunning(AutomationStrings.DisplayName(running.AutomationId, running.AutomationName)), AutomationStrings.Banner(running, Now));
    }
}
