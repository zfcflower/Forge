using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins <see cref="NotificationCenter"/> without Avalonia: an inline <c>post</c> and a fake <see cref="TimeProvider"/>
/// whose timers fire only when the test advances time, so the stay timer and the 180 ms removal timer are observed
/// separately. No test waits on a real clock, starts a process or needs a desktop session.
/// </summary>
public sealed class NotificationCenterTests
{
    private static readonly TimeSpan InfoStay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(180);

    [Fact]
    public async Task Switch_off_shows_nothing_and_calls_no_desktop_notifier()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => false, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));

        Assert.Empty(center.Items);
        Assert.Empty(desktop.Calls);

        center.IsBackground = true;
        await center.NotifyAsync(NotificationLevel.Error, LocText.Literal("t"), LocText.Literal("b"));

        Assert.Empty(center.Items);
        Assert.Empty(desktop.Calls);
    }

    [Fact]
    public async Task Foreground_inserts_newest_first()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("first"), LocText.Literal("one"));
        Assert.Single(center.Items);
        Assert.Equal("first", center.Items[0].Title);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("second"), LocText.Literal("two"));
        Assert.Equal(2, center.Items.Count);
        Assert.Equal("second", center.Items[0].Title);
        Assert.Equal("first", center.Items[1].Title);
        Assert.Empty(desktop.Calls);
    }

    [Fact]
    public async Task Repeat_collapses_and_restarts_the_stay()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("same"), LocText.Literal("body"));
        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("same"), LocText.Literal("body"));

        var item = Assert.Single(center.Items);
        Assert.Equal(2, item.Count);
        Assert.True(item.HasCount);
        Assert.Equal("×2", item.CountText);

        // The first stay would have ended at 4 s; the repeat at 3 s pushes it to 7 s.
        time.Advance(TimeSpan.FromSeconds(3));
        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("same"), LocText.Literal("body"));
        Assert.Equal(3, item.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(item.IsLeaving);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(item.IsLeaving);

        time.Advance(Exit);
        Assert.Empty(center.Items);
    }

    [Fact]
    public async Task Explicit_key_merges_texts_that_differ()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Warning, LocText.Literal("a-title"), LocText.Literal("a-body"), key: "k");
        await center.NotifyAsync(NotificationLevel.Warning, LocText.Literal("b-title"), LocText.Literal("b-body"), key: "k");

        var item = Assert.Single(center.Items);
        Assert.Equal(2, item.Count);
        Assert.Equal("a-title", item.Title);
        Assert.Equal("k", item.Key);
    }

    [Fact]
    public async Task Repeat_reclaims_an_item_that_was_already_leaving()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        var item = Assert.Single(center.Items);

        time.Advance(InfoStay);
        Assert.True(item.IsLeaving);
        Assert.Single(center.Items);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        Assert.False(item.IsLeaving);
        Assert.Equal(2, item.Count);
        Assert.Single(center.Items);

        time.Advance(InfoStay);
        Assert.True(item.IsLeaving);
        time.Advance(Exit);
        Assert.Empty(center.Items);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    public async Task Stay_matches_level_and_removal_needs_one_exit_delay(int levelValue, int staySeconds)
    {
        // 0 = Info, 1 = Success, 2 = Warning, 3 = Error; the numbers are the enum order, not a contract.
        var level = (NotificationLevel)levelValue;
        var stay = TimeSpan.FromSeconds(staySeconds);
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(level, LocText.Literal("t"), LocText.Literal("b"));
        var item = Assert.Single(center.Items);

        time.Advance(stay - TimeSpan.FromMilliseconds(1));
        Assert.False(item.IsLeaving);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(item.IsLeaving);
        Assert.Single(center.Items);

        time.Advance(Exit - TimeSpan.FromMilliseconds(1));
        Assert.Single(center.Items);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(center.Items);
    }

    [Fact]
    public async Task Six_distinct_notifications_evict_the_oldest()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        for (var i = 0; i < 6; i++)
        {
            await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t" + i), LocText.Literal("b" + i));
        }

        // The oldest is on its way out but still in the collection until the exit delay elapses.
        Assert.True(center.Items.Count <= 6);
        var oldest = center.Items.Single(item => item.Title == "t0");
        Assert.True(oldest.IsLeaving);

        time.Advance(Exit);

        Assert.DoesNotContain(center.Items, item => item.Title == "t0");
        Assert.True(center.Items.Count <= 5);
        Assert.Equal(5, center.Items.Count);
        Assert.All(center.Items, item => Assert.False(item.IsLeaving));
        Assert.Equal("t5", center.Items[0].Title);
    }

    [Fact]
    public async Task Background_goes_to_the_desktop_notifier_with_resolved_text()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        center.IsBackground = true;
        var title = new LocText(NotificationsStrings.TerminalExitedTitleKey);
        await center.NotifyAsync(NotificationLevel.Success, title, LocText.Literal("plain-body"));

        Assert.Empty(center.Items);
        var call = Assert.Single(desktop.Calls);
        Assert.Equal(Loc.Instance[NotificationsStrings.TerminalExitedTitleKey], call.Title);
        Assert.Equal("plain-body", call.Body);

        center.IsBackground = false;
        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        Assert.Single(center.Items);
        Assert.Single(desktop.Calls);
    }

    [Fact]
    public async Task Cancelled_token_throws_and_shows_nothing()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"), ct: cancelled.Token));

        Assert.Empty(center.Items);
        Assert.Empty(desktop.Calls);
    }

    [Fact]
    public async Task Language_switch_re_resolves_visible_items()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        using var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, new LocText(NotificationsStrings.EnabledLabel), new LocText(NotificationsStrings.EnabledHint));
        await center.NotifyAsync(NotificationLevel.Success, new LocText(NotificationsStrings.ApprovalWaitingBody, "a", "b"), LocText.Literal("stable"));

        Assert.Equal(2, center.Items.Count);
        var first = center.Items[1];
        var second = center.Items[0];
        var beforeTitle = first.Title;
        var beforeBody = first.Body;
        var beforeLevel = first.LevelText;

        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Equal(Loc.Instance[NotificationsStrings.EnabledLabel], first.Title);
            Assert.Equal(Loc.Instance[NotificationsStrings.EnabledHint], first.Body);
            Assert.Equal(Loc.Instance[NotificationsStrings.LevelInfo], first.LevelText);
            Assert.Equal(Loc.Instance.Format(NotificationsStrings.ApprovalWaitingBody, "a", "b"), second.Title);
            Assert.Equal(Loc.Instance[NotificationsStrings.LevelSuccess], second.LevelText);
            Assert.NotEqual(beforeTitle, first.Title);
            Assert.NotEqual(beforeBody, first.Body);
            Assert.NotEqual(beforeLevel, first.LevelText);
        }

        Assert.Equal(2, center.Items.Count);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_clears_current()
    {
        var time = new NotificationFakeTime();
        var desktop = new NotificationFakeNotifier();
        var center = new NotificationCenter(() => true, desktop, post: action => action(), time: time);

        await center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        Assert.Single(center.Items);
        Assert.Same(center, NotificationCenter.Current);

        center.Dispose();
        Assert.Empty(center.Items);
        Assert.Null(NotificationCenter.Current);

        center.Dispose();
        Assert.Null(NotificationCenter.Current);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(center.Items);
    }

    [Fact]
    public void DefaultKeyOf_covers_level_and_resolved_text()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);

        var sameA = NotificationCenter.DefaultKeyOf(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        var sameB = NotificationCenter.DefaultKeyOf(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b"));
        var otherLevel = NotificationCenter.DefaultKeyOf(NotificationLevel.Error, LocText.Literal("t"), LocText.Literal("b"));
        var otherText = NotificationCenter.DefaultKeyOf(NotificationLevel.Info, LocText.Literal("t!"), LocText.Literal("b"));

        Assert.Equal(sameA, sameB);
        Assert.NotEqual(sameA, otherLevel);
        Assert.NotEqual(sameA, otherText);
    }

    [Fact]
    public void DurationOf_matches_the_documented_stays()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), NotificationCenter.DurationOf(NotificationLevel.Info));
        Assert.Equal(TimeSpan.FromSeconds(4), NotificationCenter.DurationOf(NotificationLevel.Success));
        Assert.Equal(TimeSpan.FromSeconds(6), NotificationCenter.DurationOf(NotificationLevel.Warning));
        Assert.Equal(TimeSpan.FromSeconds(8), NotificationCenter.DurationOf(NotificationLevel.Error));
        Assert.Equal(NotificationCenter.InfoDuration, NotificationCenter.DurationOf(NotificationLevel.Info));
        Assert.Equal(NotificationCenter.SuccessDuration, NotificationCenter.DurationOf(NotificationLevel.Success));
        Assert.Equal(NotificationCenter.WarningDuration, NotificationCenter.DurationOf(NotificationLevel.Warning));
        Assert.Equal(NotificationCenter.ErrorDuration, NotificationCenter.DurationOf(NotificationLevel.Error));
        Assert.Equal(TimeSpan.FromMilliseconds(180), NotificationCenter.ExitDelay);
        Assert.Equal(5, NotificationCenter.MaxItems);
    }

    /// <summary>A clock that only moves when the test advances it, so the stay and the exit delay are observed separately.</summary>
    private sealed class NotificationFakeTime : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<FakeTimer> timers = [];
        private TimeSpan now;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero) + now;
            }
        }

        public override long GetTimestamp()
        {
            lock (gate)
            {
                return now.Ticks;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            lock (gate)
            {
                timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<FakeTimer> due = [];
            lock (gate)
            {
                now += by;
                foreach (var timer in timers)
                {
                    if (!timer.Disposed && !timer.Fired && timer.Due is { } at && at <= now)
                    {
                        timer.Fired = true;
                        due.Add(timer);
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private TimeSpan Now()
        {
            lock (gate)
            {
                return now;
            }
        }

        private sealed class FakeTimer(NotificationFakeTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; private set; }

            public bool Disposed { get; private set; }

            public bool Fired { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                    return true;
                }

                Fired = false;
                Due = owner.Now() + dueTime;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A desktop notifier that records resolved texts; nothing is started.</summary>
    private sealed class NotificationFakeNotifier : ISystemNotifier
    {
        private readonly List<(string Title, string Body)> calls = [];
        private readonly object gate = new();

        public IReadOnlyList<(string Title, string Body)> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public Task NotifyAsync(string title, string body, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(title);
            ArgumentNullException.ThrowIfNull(body);
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                calls.Add((title, body));
            }

            return Task.CompletedTask;
        }
    }
}
