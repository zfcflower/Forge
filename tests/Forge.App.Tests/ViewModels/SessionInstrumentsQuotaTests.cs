using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the account-window reading on the composer strip: both windows with their countdown, nothing when the provider
/// reported nothing, and no reading once the picker points at another provider (a window belongs to an account, not to a
/// session).
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionInstrumentsQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 4, 30, 0, TimeSpan.Zero);

    private static ProviderRateLimit Reading(
        double? fiveHour = 0.62,
        DateTimeOffset? fiveHourReset = null,
        RateLimitStatus fiveHourStatus = RateLimitStatus.Allowed,
        double? weekly = 0.41,
        DateTimeOffset? weeklyReset = null,
        RateLimitStatus weeklyStatus = RateLimitStatus.Allowed,
        RateLimitWindow? claim = RateLimitWindow.FiveHour,
        RateLimitStatus overage = RateLimitStatus.Rejected) =>
        new(
            fiveHour,
            // A reset only defaults for a window the reading actually mentions; otherwise the window is absent entirely.
            fiveHourReset ?? (fiveHour is null ? null : Now.AddMinutes(161)),
            fiveHourStatus,
            weekly,
            weeklyReset ?? (weekly is null ? null : Now.AddDays(3)),
            weeklyStatus,
            claim,
            overage);

    [Fact]
    public void The_label_lists_both_windows_with_their_countdown()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal(
            "额度 5h 62% · 2h41m 后重置   周 41% · 3d 后重置",
            SessionInstrumentsViewModel.QuotaLabelFor(Reading(), Now));
    }

    [Fact]
    public void The_label_is_the_same_readout_in_english()
    {
        using var language = new LanguageScope(UiLanguage.English);

        Assert.Equal(
            "quota 5h 62% · resets in 2h41m   7d 41% · resets in 3d",
            SessionInstrumentsViewModel.QuotaLabelFor(Reading(), Now));
    }

    [Fact]
    public void A_window_the_response_did_not_mention_is_left_out()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        var onlyWeekly = Reading(fiveHour: null, fiveHourReset: null, fiveHourStatus: RateLimitStatus.Unknown);
        Assert.Equal("额度 周 41% · 3d 后重置", SessionInstrumentsViewModel.QuotaLabelFor(onlyWeekly, Now));

        var onlyFiveHour = Reading(weekly: null, weeklyReset: null, weeklyStatus: RateLimitStatus.Unknown);
        Assert.Equal("额度 5h 62% · 2h41m 后重置", SessionInstrumentsViewModel.QuotaLabelFor(onlyFiveHour, Now));

        var nothing = new ProviderRateLimit(null, null, RateLimitStatus.Unknown, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown);
        Assert.Equal(string.Empty, SessionInstrumentsViewModel.QuotaLabelFor(nothing, Now));
    }

    [Fact]
    public void A_window_whose_reset_has_passed_shows_no_countdown()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        var stale = Reading(fiveHourReset: Now.AddMinutes(-5), weekly: null, weeklyReset: null, weeklyStatus: RateLimitStatus.Unknown);

        Assert.Equal("额度 5h 62%", SessionInstrumentsViewModel.QuotaLabelFor(stale, Now));
    }

    [Fact]
    public void The_tooltip_names_the_binding_window_and_the_overage_state()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        var tooltip = SessionInstrumentsViewModel.QuotaTooltipFor(
            Reading(fiveHourStatus: RateLimitStatus.Rejected),
            Now);

        Assert.Contains("5 小时窗口 62.0%", tooltip, StringComparison.Ordinal);
        Assert.Contains("当前限制 5 小时窗口", tooltip, StringComparison.Ordinal);
        Assert.Contains("超额通道 已关闭", tooltip, StringComparison.Ordinal);
        Assert.Contains("周额度 41.0%", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rate_limit_reading_says_it_is_a_rate_limit_and_not_a_spending_window()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        // The console's ceiling: what an API key is throttled by, in requests and tokens per minute.
        var throttle = new ProviderRateLimit(null, null, RateLimitStatus.Unknown, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown)
        {
            Throttle = new ProviderThrottle(50, 43, Now.AddSeconds(12), 40_000, 12_300, Now.AddSeconds(12)),
        };

        var label = SessionInstrumentsViewModel.QuotaLabelFor(throttle, Now);
        Assert.Equal("限流 每分钟请求 43/50 · 每分钟令牌 12.3k/40k · 12s 后重置", label);

        var tooltip = SessionInstrumentsViewModel.QuotaTooltipFor(throttle, Now);
        Assert.Contains("限流（短期限流，不是订阅额度窗口）", tooltip, StringComparison.Ordinal);
        Assert.Contains("请求 43/50", tooltip, StringComparison.Ordinal);
        Assert.Contains("令牌 12.3k/40k", tooltip, StringComparison.Ordinal);

        // A rate limit never reads as a spent quota: a 429 against it is transient and belongs to the retry layer.
        Assert.False(throttle.IsRejected);
        Assert.Null(throttle.ResetAt);
    }

    [Fact]
    public void A_rate_limit_reading_is_laid_out_the_same_way_in_english()
    {
        using var language = new LanguageScope(UiLanguage.English);

        var throttle = new ProviderRateLimit(null, null, RateLimitStatus.Unknown, null, null, RateLimitStatus.Unknown, null, RateLimitStatus.Unknown)
        {
            Throttle = new ProviderThrottle(50, 43, Now.AddSeconds(12), null, null, null),
        };

        Assert.Equal("rate limit requests/min 43/50 · resets in 12s", SessionInstrumentsViewModel.QuotaLabelFor(throttle, Now));
        Assert.Contains("not a subscription window", SessionInstrumentsViewModel.QuotaTooltipFor(throttle, Now), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_reading_is_flagged_and_a_warning_is_flagged_differently()
    {
        // The two style classes are driven by the server's own status, never by a percentage this app invents.
        var rejected = Reading(fiveHourStatus: RateLimitStatus.Rejected);
        var warning = Reading(fiveHourStatus: RateLimitStatus.AllowedWarning, fiveHour: 0.95);

        Assert.True(rejected.IsRejected);
        Assert.Equal(RateLimitWindow.FiveHour, rejected.BindingWindow);
        Assert.False(warning.IsRejected);
        Assert.Equal(RateLimitStatus.AllowedWarning, warning.FiveHour);
    }

    [Fact]
    public async Task The_strip_says_which_cache_lifetime_is_in_force()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            using var instruments = new SessionInstrumentsViewModel(
                new SessionInfo("session-1", @"C:\workorge", "instruments", Now),
                EmptySessionInstrumentSources.Instance,
                static () => [],
                static () => "claude-opus-5",
                static () => "anthropic");

            Assert.False(instruments.HasCacheTtl);

            // The loop reports the lifetime it asked for, so the figure on screen is the one being spent rather than a promise.
            instruments.NoteCacheTtl("1h");
            Assert.True(instruments.HasCacheTtl);
            Assert.Equal("缓存 1h", instruments.CacheTtlText);
            Assert.Contains("提示词缓存 1h", instruments.CacheTtlTooltip, StringComparison.Ordinal);

            instruments.NoteCacheTtl("5m");
            Assert.Equal("缓存 5m", instruments.CacheTtlText);

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_strip_shows_the_reading_and_hides_it_when_the_picker_moves_away()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var language = new LanguageScope(UiLanguage.Chinese);
            var picker = "anthropic";
            using var instruments = new SessionInstrumentsViewModel(
                new SessionInfo("session-1", @"C:\work\forge", "instruments", Now),
                EmptySessionInstrumentSources.Instance,
                static () => [],
                static () => "claude-opus-5",
                () => picker);

            Assert.False(instruments.HasQuota);
            Assert.Equal(string.Empty, instruments.QuotaLabel);

            instruments.NoteRateLimit("anthropic", Reading());
            Assert.True(instruments.HasQuota);
            Assert.StartsWith("额度 5h 62%", instruments.QuotaLabel, StringComparison.Ordinal);

            // A turn start re-reads the counters; that is also when a picker change becomes visible to the strip.
            picker = "openrouter";
            instruments.RefreshCounters();
            Assert.False(instruments.HasQuota);
            Assert.Equal(string.Empty, instruments.QuotaLabel);

            await Task.CompletedTask;
        });
    }
}
