using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class QuotaProbeScheduleTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public int PendingTimers
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public TimeSpan? NextDue
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count == 0 ? null : _timers.Min(t => t.Due) - _now;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer;
            lock (_gate)
            {
                timer = new ManualTimer(this, callback, state) { Due = _now + dueTime };
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(t => t.Due <= _now).ToList();
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (var timer in due)
            {
                timer.Callback(timer.State);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; }

            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ScriptedProbe(Func<int, Task<bool>> script) : IProviderQuotaProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> ProbeAsync(ModelSettings model, CancellationToken ct) =>
            script(Interlocked.Increment(ref _calls));
    }

    private static readonly ModelSettings Model = new("m", ProviderId: "p");

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(5);
        }
    }

    private static async Task Step(ManualTimeProvider time, TimeSpan expectedMinimum)
    {
        await Until(() => time.PendingTimers > 0);
        Assert.True(time.NextDue >= expectedMinimum);
        time.Advance(time.NextDue!.Value);
    }

    [Fact]
    public void Backoff_sequence_without_jitter_is_5_10_20_60_then_stays_60()
    {
        var schedule = new QuotaProbeSchedule(random: () => 0.5);

        var delays = Enumerable.Range(0, 6).Select(_ => schedule.NextDelay("p")).ToArray();

        Assert.Equal(
            new[] { 5, 10, 20, 60, 60, 60 }.Select(m => TimeSpan.FromMinutes(m)).ToArray(),
            delays);
    }

    [Fact]
    public void Jitter_stays_within_ten_percent()
    {
        var low = new QuotaProbeSchedule(random: () => 0.0).NextDelay("p");
        var high = new QuotaProbeSchedule(random: () => 1.0).NextDelay("p");

        Assert.Equal(TimeSpan.FromMinutes(4.5), low);
        Assert.Equal(TimeSpan.FromMinutes(5.5), high);
    }

    [Fact]
    public void Real_random_never_leaves_the_band()
    {
        for (var i = 0; i < 200; i++)
        {
            var delay = new QuotaProbeSchedule().NextDelay("p");
            Assert.InRange(delay, TimeSpan.FromMinutes(4.5), TimeSpan.FromMinutes(5.5));
        }
    }

    [Fact]
    public void Keys_are_independent_and_reset_restarts_the_sequence()
    {
        var schedule = new QuotaProbeSchedule(random: () => 0.5);
        schedule.NextDelay("a");
        schedule.NextDelay("a");

        Assert.Equal(TimeSpan.FromMinutes(5), schedule.NextDelay("b"));
        Assert.Equal(TimeSpan.FromMinutes(20), schedule.NextDelay("a"));

        schedule.Reset("a");

        Assert.Equal(TimeSpan.FromMinutes(5), schedule.NextDelay("a"));
    }

    [Fact]
    public void Null_provider_maps_to_default_key()
    {
        Assert.Equal("default", QuotaProbeSchedule.KeyFor(null));
        Assert.Equal("x", QuotaProbeSchedule.KeyFor("x"));
    }

    [Fact]
    public async Task Waiters_share_one_probe_loop_and_complete_together()
    {
        var time = new ManualTimeProvider();
        var schedule = new QuotaProbeSchedule(() => 0.5, time);
        var probe = new ScriptedProbe(call => Task.FromResult(call >= 2));

        var first = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        var second = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);

        await Step(time, TimeSpan.FromMinutes(5));
        await Until(() => probe.Calls == 1 && time.PendingTimers > 0);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await Step(time, TimeSpan.FromMinutes(10));

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task Success_resets_the_backoff()
    {
        var time = new ManualTimeProvider();
        var schedule = new QuotaProbeSchedule(() => 0.5, time);
        var probe = new ScriptedProbe(_ => Task.FromResult(true));

        var wait = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        await Step(time, TimeSpan.FromMinutes(5));
        Assert.True(await wait);

        Assert.Equal(TimeSpan.FromMinutes(5), schedule.NextDelay("p"));
    }

    [Fact]
    public async Task Cancelling_one_waiter_keeps_the_shared_loop_for_the_others()
    {
        var time = new ManualTimeProvider();
        var schedule = new QuotaProbeSchedule(() => 0.5, time);
        var probe = new ScriptedProbe(_ => Task.FromResult(true));
        using var cts = new CancellationTokenSource();

        var cancelled = schedule.WaitUntilAvailableAsync("p", Model, probe, cts.Token);
        var staying = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        await Until(() => time.PendingTimers > 0);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(staying.IsCompleted);

        await Step(time, TimeSpan.FromMinutes(5));

        Assert.True(await staying);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task Cancelling_the_last_waiter_stops_the_loop()
    {
        var time = new ManualTimeProvider();
        var schedule = new QuotaProbeSchedule(() => 0.5, time);
        var probe = new ScriptedProbe(_ => Task.FromResult(true));
        using var cts = new CancellationTokenSource();

        var wait = schedule.WaitUntilAvailableAsync("p", Model, probe, cts.Token);
        await Until(() => time.PendingTimers > 0);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        await Until(() => time.PendingTimers == 0);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Probe_errors_are_reported_and_count_as_failed_attempts()
    {
        var time = new ManualTimeProvider();
        var errors = new List<(string, Exception)>();
        var schedule = new QuotaProbeSchedule(() => 0.5, time, (key, ex) => errors.Add((key, ex)));
        var probe = new ScriptedProbe(call => call == 1
            ? Task.FromException<bool>(new InvalidOperationException("boom"))
            : Task.FromResult(true));

        var wait = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        await Step(time, TimeSpan.FromMinutes(5));
        await Until(() => errors.Count == 1 && time.PendingTimers > 0);

        Assert.Equal("p", errors[0].Item1);
        Assert.IsType<InvalidOperationException>(errors[0].Item2);
        await Step(time, TimeSpan.FromMinutes(10));
        Assert.True(await wait);
    }

    [Fact]
    public async Task Waiting_again_after_completion_starts_a_new_loop()
    {
        var time = new ManualTimeProvider();
        var schedule = new QuotaProbeSchedule(() => 0.5, time);
        var probe = new ScriptedProbe(_ => Task.FromResult(true));

        var one = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        await Step(time, TimeSpan.FromMinutes(5));
        Assert.True(await one);

        var two = schedule.WaitUntilAvailableAsync("p", Model, probe, CancellationToken.None);
        await Step(time, TimeSpan.FromMinutes(5));
        Assert.True(await two);
        Assert.Equal(2, probe.Calls);
    }
}
