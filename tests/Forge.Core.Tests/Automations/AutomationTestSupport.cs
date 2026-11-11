using Forge.Abstractions;
using Forge.Core.Automations;

namespace Forge.Core.Tests.Automations;

/// <summary>A clock the test moves by hand; timers created through it fire from <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now) => _now = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Exchange(ref _throwSlot, null) is { } ex) throw ex;
        lock (_gate) return _now;
    }

    private Exception? _throwSlot;

    /// <summary>Makes the next <see cref="GetUtcNow"/> throw once.</summary>
    public void ArmThrow(Exception exception) => Volatile.Write(ref _throwSlot, exception);

    public void Set(DateTimeOffset now)
    {
        lock (_gate) _now = now;
    }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due = [];
        lock (_gate)
        {
            _now += by;
            foreach (var t in _timers)
            {
                if (t.Due <= _now) due.Add(t);
            }

            foreach (var t in due) _timers.Remove(t);
        }

        foreach (var t in due) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class FakeTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = DateTimeOffset.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = owner._now + dueTime;
                owner._timers.Add(this);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeDefinitionStore : IAutomationDefinitionStore
{
    public AutomationConfig Config { get; set; } = new();

    public IReadOnlyList<AutomationDefinitionError> Errors { get; set; } = [];

    public int Saves { get; private set; }

    public Task<AutomationConfigLoad> LoadAsync(CancellationToken ct) => Task.FromResult(new AutomationConfigLoad(Config, Errors));

    public Task SaveAsync(AutomationConfig config, CancellationToken ct)
    {
        Config = config;
        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRunStore : IAutomationRunStore
{
    private readonly object _gate = new();
    private readonly List<AutomationRun> _runs = [];
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);

    public int AliveWrites { get; private set; }

    public Exception? SetStateFailure { get; set; }

    /// <summary>When set, every run update waits this long before it is applied (a slow database write).</summary>
    public TimeSpan? UpdateDelay { get; set; }

    public IReadOnlyList<AutomationRun> All
    {
        get { lock (_gate) return [.. _runs]; }
    }

    public AutomationRun Get(string id) => All.Single(r => r.Id == id);

    public void Seed(AutomationRun run)
    {
        lock (_gate) _runs.Add(run);
    }

    public string? State(string key)
    {
        lock (_gate) return _state.GetValueOrDefault(key);
    }

    public Task<bool> TryInsertAsync(AutomationRun run, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_runs.Any(r => r.Occurrence == run.Occurrence)) return Task.FromResult(false);
            _runs.Add(run);
            return Task.FromResult(true);
        }
    }

    public async Task UpdateAsync(AutomationRun run, CancellationToken ct)
    {
        if (UpdateDelay is { } delay)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            var i = _runs.FindIndex(r => r.Id == run.Id);
            if (i >= 0) _runs[i] = run;
        }
    }

    public Task<AutomationRun?> GetAsync(string runId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_runs.FirstOrDefault(r => r.Id == runId));
    }

    public Task<IReadOnlyList<AutomationRun>> QueryAsync(AutomationRunQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            IEnumerable<AutomationRun> q = _runs;
            if (query.AutomationId is { } id) q = q.Where(r => r.AutomationId == id);
            if (query.Statuses is { } statuses) q = q.Where(r => statuses.Contains(r.Status));
            if (query.TargetSessionId is { } target) q = q.Where(r => r.TargetSessionId == target);
            return Task.FromResult<IReadOnlyList<AutomationRun>>([.. q.Reverse().Take(query.Limit)]);
        }
    }

    public Task<int> MarkInterruptedAsync(DateTimeOffset endedAt, string message, CancellationToken ct)
    {
        lock (_gate)
        {
            var n = 0;
            for (var i = 0; i < _runs.Count; i++)
            {
                if (_runs[i].Status is AutomationRunStatus.Running or AutomationRunStatus.WaitingIdle)
                {
                    _runs[i] = _runs[i] with { Status = AutomationRunStatus.Interrupted, EndedAt = endedAt, Message = message };
                    n++;
                }
            }

            return Task.FromResult(n);
        }
    }

    public Task<int> TrimAsync(int perAutomation, int total, CancellationToken ct) => Task.FromResult(0);

    public Task<string?> GetStateAsync(string key, CancellationToken ct) => Task.FromResult(State(key));

    public Task SetStateAsync(string key, string? value, CancellationToken ct)
    {
        if (SetStateFailure is { } failure) throw failure;
        lock (_gate)
        {
            if (key == AutomationEngine.AliveAtKey) AliveWrites++;
            if (value is null) _state.Remove(key);
            else _state[key] = value;
        }

        return Task.CompletedTask;
    }
}

internal sealed class DelegateRunner(Func<IAutomationRunContext, CancellationToken, Task<AutomationRunOutcome>> run) : IAutomationRunner
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return run(context, ct);
    }

    public static DelegateRunner Completing() => new((_, _) => Task.FromResult(AutomationRunOutcome.Completed("done")));

    /// <summary>Blocks until <paramref name="gate"/> completes; <paramref name="started"/> is set when the run begins.</summary>
    public static DelegateRunner Blocking(TaskCompletionSource started, TaskCompletionSource gate) => new(async (_, ct) =>
    {
        started.TrySetResult();
        await gate.Task.WaitAsync(ct);
        return AutomationRunOutcome.Completed();
    });
}
