using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins that the XAML overlay loads, binds and does not throw: it shows when a toast appears and hides when the last
/// one is gone, stays hidden without a <see cref="NotificationCenter"/>, and that <see cref="ToastStackPanel"/> puts a
/// plain child where <see cref="ToastStackLayout"/> predicts.
/// </summary>
[Collection("forge-app-ui")]
public sealed class NotificationOverlayTests
{
    [Fact]
    public async Task Overlay_shows_when_an_item_appears_and_hides_when_it_is_gone()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                var time = new OverlayFakeTime();
                var desktop = new OverlayFakeNotifier();
                using var center = new NotificationCenter(() => true, desktop, time: time);

                var overlay = new NotificationOverlay();
                Assert.False(overlay.IsVisible);

                var window = SharedUi.Show(overlay, 500, 500);
                SharedUi.Settle();

                Assert.False(overlay.IsVisible);

                center.NotifyAsync(NotificationLevel.Info, LocText.Literal("t"), LocText.Literal("b")).GetAwaiter().GetResult();
                Assert.Single(center.Items);
                Assert.True(overlay.IsVisible);

                // Two phases on purpose: the stay timer only starts the exit window, and the removal is its own 180 ms timer.
                time.Advance(TimeSpan.FromSeconds(4));
                Dispatcher.UIThread.RunJobs();
                Assert.Single(center.Items);
                Assert.True(center.Items[0].IsLeaving);

                time.Advance(TimeSpan.FromMilliseconds(180));
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(center.Items);
                Assert.False(overlay.IsVisible);

                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Overlay_without_a_center_stays_hidden()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                var time = new OverlayFakeTime();
                var desktop = new OverlayFakeNotifier();
                using (var center = new NotificationCenter(() => true, desktop, time: time))
                {
                }

                Assert.Null(NotificationCenter.Current);

                var overlay = new NotificationOverlay();
                Assert.False(overlay.IsVisible);

                var window = SharedUi.Show(overlay, 500, 500);
                SharedUi.Settle();

                Assert.False(overlay.IsVisible);

                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task ToastStackPanel_arranges_a_plain_child_where_the_pure_function_predicts()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(
            () =>
            {
                var panel = new ToastStackPanel();
                var first = new ContentControl { Width = 100, Height = 10 };
                var second = new ContentControl { Width = 100, Height = 20 };
                var third = new ContentControl { Width = 100, Height = 0 };
                panel.Children.Add(first);
                panel.Children.Add(second);
                panel.Children.Add(third);

                panel.Measure(new Size(380, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 380, panel.DesiredSize.Height));

                var heights = new double[] { first.DesiredSize.Height, second.DesiredSize.Height, third.DesiredSize.Height };
                var expected = ToastStackLayout.Offsets(heights, ToastStackPanel.Spacing);

                Assert.Equal(expected[0], first.Bounds.Y, 3);
                Assert.Equal(expected[1], second.Bounds.Y, 3);
                Assert.Equal(expected[2], third.Bounds.Y, 3);
            },
            CancellationToken.None);
    }

    private sealed class OverlayFakeTime : TimeProvider
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

        private sealed class FakeTimer(OverlayFakeTime owner, TimerCallback callback, object? state) : ITimer
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

    private sealed class OverlayFakeNotifier : ISystemNotifier
    {
        public Task NotifyAsync(string title, string body, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> TryNotifyAsync(string title, string body, CancellationToken ct) => Task.FromResult(true);
    }
}
