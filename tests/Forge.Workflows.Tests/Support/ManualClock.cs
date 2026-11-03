namespace Forge.Workflows.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves through <see cref="Advance"/>. Timers created through it fire
/// synchronously inside <see cref="Advance"/> in due-time order, so timeouts are exact and need no sleeping.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed = TimeSpan.Zero;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                return _elapsed;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return Epoch + _elapsed;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _elapsed.Ticks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("ManualClock only supports one-shot timers.");
        }

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        TimeSpan target;
        lock (_sync)
        {
            target = _elapsed + by;
        }

        while (true)
        {
            ManualTimer? due = null;
            lock (_sync)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is { } dueAt && dueAt <= target && (due is null || dueAt < due.DueAt))
                    {
                        due = timer;
                    }
                }

                if (due is null)
                {
                    _elapsed = target;
                    return;
                }

                _elapsed = due.DueAt!.Value;
                due.DueAt = null;
            }

            due.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_sync)
        {
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _elapsed + dueTime;
            if (timer.DueAt is not null && !_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
        {
            timer.DueAt = null;
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
