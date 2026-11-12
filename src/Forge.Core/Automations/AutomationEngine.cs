using System.Globalization;
using Forge.Abstractions;

namespace Forge.Core.Automations;

/// <summary>
/// The automation scheduler and run bookkeeping. <see cref="Tick"/> is synchronous and unit-testable; <see cref="RunAsync"/>
/// drives it from a timer. Runners execute on the thread pool; one active run per automation; scheduled fires that find the
/// automation busy are recorded as skipped, never queued.
/// </summary>
public sealed class AutomationEngine : IAutomationEngine, IAsyncDisposable
{
    public const string AliveAtKey = "engine.alive_at";
    public const string BudgetPrefix = "budget:";
    public const int MaxChainDepth = 5;

    private static readonly TimeSpan LateTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    private readonly IAutomationDefinitionStore _definitions;
    private readonly IAutomationRunStore _runs;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly IAutomationRunner _defaultRunner;
    private readonly Dictionary<string, IAutomationRunner> _runners = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _busy = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _inflight = [];

    private AutomationConfig _config = new();
    private IReadOnlyList<AutomationDefinitionError> _storeErrors = [];
    private IReadOnlyList<AutomationDefinitionError> _errors = [];
    private IReadOnlyList<AutomationDefinition> _definitionList = [];
    private Entry[] _entries = [];
    private TaskCompletionSource _wake = NewWake();
    private DateTimeOffset? _lastAliveWrite;
    private bool _disposed;

    public AutomationEngine(
        IAutomationDefinitionStore definitions,
        IAutomationRunStore runs,
        TimeProvider? time,
        TimeZoneInfo? zone,
        IAutomationRunner defaultRunner)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(defaultRunner);
        _definitions = definitions;
        _runs = runs;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        _defaultRunner = defaultRunner;
        Rebuild(_time.GetUtcNow(), null, preserve: false);
    }

    public event EventHandler? Changed;

    public AutomationConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public IReadOnlyList<AutomationDefinitionError> ConfigErrors
    {
        get { lock (_gate) return _errors; }
    }

    public IReadOnlyList<AutomationDefinition> Definitions
    {
        get { lock (_gate) return _definitionList; }
    }

    public IReadOnlyList<AutomationRun> ActiveRuns
    {
        get
        {
            lock (_gate)
            {
                var result = new AutomationRun[_active.Count];
                var i = 0;
                foreach (var a in _active.Values) result[i++] = a.Run;
                return result;
            }
        }
    }

    /// <summary>Registers the runner of a built-in automation (otherwise the default runner serves user definitions only).</summary>
    public void RegisterRunner(string automationId, IAutomationRunner runner)
    {
        ArgumentException.ThrowIfNullOrEmpty(automationId);
        ArgumentNullException.ThrowIfNull(runner);
        lock (_gate) _runners[automationId] = runner;
    }

    public DateTimeOffset? NextFireTime(string automationId)
    {
        lock (_gate)
        {
            if (_config.Paused) return null;
            foreach (var e in _entries)
            {
                if (e.Definition.Id != automationId) continue;
                if (!e.Enabled) return null;
                DateTimeOffset? best = null;
                foreach (var due in e.Due)
                {
                    if (due is { } d && (best is null || d < best)) best = d;
                }

                return best;
            }

            return null;
        }
    }

    /// <summary>Loads config, recovers interrupted runs, resumes waiting-quota runs and trims history.</summary>
    public async Task StartupAsync(CancellationToken ct)
    {
        var load = await _definitions.LoadAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        DateTimeOffset? aliveAt = null;
        var text = await _runs.GetStateAsync(AliveAtKey, ct).ConfigureAwait(false);
        if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            aliveAt = parsed;
        }

        lock (_gate)
        {
            _config = load.Config;
            _storeErrors = load.Errors;
            _lastAliveWrite = now;
            Rebuild(now, aliveAt ?? now, preserve: false);
        }

        // A run that only waited for its session to become idle has not sent anything yet; the runners named in
        // ResumableWhileIdle pick it up again (as waiting with no resume time) instead of losing it to "interrupted".
        var idle = await _runs.QueryAsync(
            new AutomationRunQuery { Statuses = [AutomationRunStatus.WaitingIdle], Limit = 1000 }, ct).ConfigureAwait(false);
        foreach (var run in idle)
        {
            if (ResumableWhileIdle.Contains(run.AutomationId))
            {
                await _runs.UpdateAsync(run with { Status = AutomationRunStatus.WaitingQuota, ResumeAt = null }, ct).ConfigureAwait(false);
            }
        }

        await _runs.MarkInterruptedAsync(aliveAt ?? now, "Forge closed while this run was in progress.", ct).ConfigureAwait(false);

        var waiting = await _runs.QueryAsync(
            new AutomationRunQuery { Statuses = [AutomationRunStatus.WaitingQuota], Limit = 1000 }, ct).ConfigureAwait(false);
        foreach (var run in waiting) ResumeWaiting(run);

        await _runs.TrimAsync(100, 2000, ct).ConfigureAwait(false);
        await WriteAliveAsync(now).ConfigureAwait(false);
        Wake();
        RaiseChanged();
    }

    /// <summary>Automations whose runs waiting for an idle session survive a restart (they re-check before sending).</summary>
    public static readonly IReadOnlySet<string> ResumableWhileIdle = new HashSet<string>(StringComparer.Ordinal) { BuiltInAutomations.QuotaResume };

    private void ResumeWaiting(AutomationRun run)
    {
        AutomationDefinition def;
        IAutomationRunner? runner;
        ActiveRun active;
        lock (_gate)
        {
            if (_busy.ContainsKey(BusyKey(run.AutomationId, run.TargetSessionId)) || _active.ContainsKey(run.Id)) return;
            def = FindDefinition(run.AutomationId) ?? new AutomationDefinition(run.AutomationId, run.AutomationName);
            runner = ResolveRunner(def);
            if (runner is null) return;
            active = new ActiveRun(run, def, runner, isResumed: true);
            _busy[BusyKey(run.AutomationId, run.TargetSessionId)] = run.Id;
            _active[run.Id] = active;
        }

        Track(Task.Run(() => ExecuteAsync(active)));
    }

    /// <summary>The scheduler loop; returns when <paramref name="ct"/> (or disposal) cancels it.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            Task wake;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted) _wake = NewWake();
                wake = _wake.Task;
            }

            TimeSpan wait;
            try
            {
                wait = Tick(_time.GetUtcNow());
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                wait = TimeSpan.FromSeconds(1);
            }

            using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                await Task.WhenAny(Task.Delay(wait, _time, delay.Token), wake).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            delay.Cancel();
        }
    }

    /// <summary>
    /// One scheduling pass at <paramref name="now"/>: starts every due automation (not awaited) and returns how long to wait
    /// before the next pass, clamped to [250 ms, 30 s].
    /// </summary>
    public TimeSpan Tick(DateTimeOffset now)
    {
        List<Fire>? fires = null;
        DateTimeOffset? nextDue = null;
        var writeAlive = false;
        lock (_gate)
        {
            if (_lastAliveWrite is null || now - _lastAliveWrite >= HeartbeatInterval)
            {
                _lastAliveWrite = now;
                writeAlive = true;
            }

            var paused = _config.Paused;
            foreach (var e in _entries)
            {
                if (!e.Enabled) continue;
                var fireIndex = -1;
                DateTimeOffset fireDue = default;
                var source = AutomationTriggerSource.Schedule;
                for (var i = 0; i < e.Due.Length; i++)
                {
                    if (e.Due[i] is not { } due) continue;
                    if (due > now)
                    {
                        if (nextDue is null || due < nextDue) nextDue = due;
                        continue;
                    }

                    // Due. While paused nothing fires; due times fall behind and are recomputed on resume.
                    var catchUp = e.CatchUpPending[i];
                    var eligible = !paused && (catchUp || now - due <= LateTolerance);
                    if (eligible && (fireIndex < 0 || due < fireDue))
                    {
                        fireIndex = i;
                        fireDue = due;
                        source = catchUp ? AutomationTriggerSource.CatchUp : AutomationTriggerSource.Schedule;
                    }

                    e.CatchUpPending[i] = false;
                    var next = e.Schedules[i]?.Next(now, _zone);
                    e.Due[i] = next;
                    if (next is { } n && (nextDue is null || n < nextDue)) nextDue = n;
                }

                if (fireIndex >= 0) (fires ??= []).Add(new Fire(e.Definition, fireIndex, fireDue, source));
            }
        }

        if (writeAlive) Track(WriteAliveAsync(now));
        if (fires is not null)
        {
            foreach (var fire in fires) Track(FireAsync(fire));
            RaiseChanged();
        }

        var wait = nextDue is { } d ? d - now : MaxWait;
        return wait < MinWait ? MinWait : wait > MaxWait ? MaxWait : wait;
    }

    public async Task UpdateConfigAsync(Func<AutomationConfig, AutomationConfig> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = change(Config);
            await _definitions.SaveAsync(updated, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _config = updated;
                Rebuild(_time.GetUtcNow(), null, preserve: true);
            }
        }
        finally
        {
            _configLock.Release();
        }

        Wake();
        RaiseChanged();
    }

    public async Task<AutomationStartResult> StartAsync(AutomationRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        AutomationDefinition? def;
        lock (_gate) def = FindDefinition(request.AutomationId);
        if (def is null) return new AutomationStartResult(AutomationStartStatus.UnknownAutomation, null);
        return await StartCoreAsync(def, request, scheduled: false, ct).ConfigureAwait(false);
    }

    public bool Cancel(string runId, string reason)
    {
        ActiveRun? active;
        lock (_gate)
        {
            if (!_active.TryGetValue(runId, out active)) return false;
            active.CancelReason ??= reason;
        }

        try
        {
            active.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public Task ResetTargetBudgetAsync(string targetKey, CancellationToken ct) =>
        _runs.SetStateAsync(BudgetPrefix + targetKey, null, ct);

    /// <summary>Completes when no start or run is in flight (for tests and orderly shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_gate) pending = [.. _inflight];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ActiveRun[] actives;
        DateTimeOffset now;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            actives = [.. _active.Values];
            now = _time.GetUtcNow();
        }

        // Shutdown: runs are cancelled but their records stay as they are for startup recovery.
        _stop.Cancel();
        foreach (var a in actives)
        {
            try
            {
                a.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        try
        {
            await _runs.SetStateAsync(AliveAtKey, now.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }

    private async Task FireAsync(Fire fire)
    {
        try
        {
            var key = $"t{fire.TriggerIndex}@{fire.Due.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";
            var request = new AutomationRunRequest(fire.Definition.Id, fire.Source) { Occurrence = key };
            await StartCoreAsync(fire.Definition, request, scheduled: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failing store must not take the scheduler down; the next tick tries the next occurrence.
        }
    }

    private async Task<AutomationStartResult> StartCoreAsync(AutomationDefinition def, AutomationRunRequest request, bool scheduled, CancellationToken ct)
    {
        IAutomationRunner? runner;
        lock (_gate) runner = ResolveRunner(def);
        if (runner is null) return new AutomationStartResult(AutomationStartStatus.UnknownAutomation, null);

        var runId = Guid.NewGuid().ToString("N");
        var depth = 0;
        AutomationRun? cause = null;
        if (request.CauseRunId is { } causeId)
        {
            cause = await _runs.GetAsync(causeId, ct).ConfigureAwait(false);
            depth = (cause?.ChainDepth ?? 0) + 1;
        }

        if (cause is not null && cause.AutomationId == def.Id)
        {
            return await RefuseAsync(AutomationStartStatus.SelfTriggered, "An automation cannot be triggered by its own run.").ConfigureAwait(false);
        }

        if (depth > MaxChainDepth)
        {
            return await RefuseAsync(AutomationStartStatus.ChainTooDeep, $"Chain depth {depth} exceeds the limit of {MaxChainDepth}.").ConfigureAwait(false);
        }

        var busyKey = BusyKey(def.Id, request.TargetSessionId);
        lock (_gate)
        {
            if (_busy.ContainsKey(busyKey)) runId = string.Empty;
            else _busy[busyKey] = runId;
        }

        if (runId.Length == 0)
        {
            return await RefuseAsync(AutomationStartStatus.Busy, "The previous run is still in progress.").ConfigureAwait(false);
        }

        var inserted = false;
        try
        {
            var budgetKey = request.TargetKey is null ? null : BudgetPrefix + request.TargetKey;
            var attempts = 0;
            if (budgetKey is not null)
            {
                var text = await _runs.GetStateAsync(budgetKey, ct).ConfigureAwait(false);
                _ = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out attempts);
                if (attempts >= request.MaxTargetAttempts)
                {
                    ReleaseBusy(busyKey, runId);
                    runId = string.Empty;
                    return await RefuseAsync(
                        AutomationStartStatus.BudgetExhausted,
                        $"The attempt budget of {request.TargetKey} ({request.MaxTargetAttempts}) is used up.").ConfigureAwait(false);
                }
            }

            var run = NewRun(runId, def, request, request.Occurrence ?? runId, request.InitialStatus, depth);
            if (AutomationRunStatuses.IsActive(run.Status) is false) run = run with { Status = AutomationRunStatus.Running };
            if (!await _runs.TryInsertAsync(run, ct).ConfigureAwait(false))
            {
                return new AutomationStartResult(AutomationStartStatus.Duplicate, null);
            }

            inserted = true;
            if (budgetKey is not null)
            {
                await _runs.SetStateAsync(budgetKey, (attempts + 1).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }

            var active = new ActiveRun(run, def, runner, isResumed: false);
            lock (_gate) _active[runId] = active;
            Track(Task.Run(() => ExecuteAsync(active)));
            RaiseChanged();
            return new AutomationStartResult(AutomationStartStatus.Started, run);
        }
        finally
        {
            if (!inserted && runId.Length != 0) ReleaseBusy(busyKey, runId);
        }

        async Task<AutomationStartResult> RefuseAsync(AutomationStartStatus status, string message)
        {
            var skipId = Guid.NewGuid().ToString("N");
            // A scheduled occurrence keeps its key (that occurrence is handled); other refusals get a unique key so a later retry of the same event is not blocked.
            var occurrence = scheduled && request.Occurrence is { } o ? o : "skip:" + skipId;
            var skipped = NewRun(skipId, def, request, occurrence, AutomationRunStatus.Skipped, depth) with
            {
                EndedAt = _time.GetUtcNow(),
                Message = message,
            };
            if (!await _runs.TryInsertAsync(skipped, ct).ConfigureAwait(false)) return new AutomationStartResult(AutomationStartStatus.Duplicate, null);
            RaiseChanged();
            return new AutomationStartResult(status, skipped);
        }
    }

    private AutomationRun NewRun(string id, AutomationDefinition def, AutomationRunRequest request, string occurrence, AutomationRunStatus status, int depth) =>
        new(id, def.Id, def.Name, request.Source, occurrence, status, _time.GetUtcNow())
        {
            Detail = request.Detail,
            TargetSessionId = request.TargetSessionId,
            CauseRunId = request.CauseRunId,
            ChainDepth = depth,
            TargetKey = request.TargetKey,
            ResumeAt = request.ResumeAt,
        };

    private async Task ExecuteAsync(ActiveRun active)
    {
        AutomationRunOutcome outcome;
        var context = new RunContext(this, active);
        try
        {
            outcome = await active.Runner.RunAsync(context, active.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (active.Cts.IsCancellationRequested)
        {
            if (active.CancelReason is null)
            {
                // Shutdown: leave the record for startup recovery.
                Detach(active);
                return;
            }

            outcome = AutomationRunOutcome.Cancelled(active.CancelReason);
        }
        catch (Exception ex)
        {
            outcome = AutomationRunOutcome.Failed(ex.Message);
        }

        if (active.CancelReason is { } reason) outcome = AutomationRunOutcome.Cancelled(reason);
        else if (outcome is null || AutomationRunStatuses.IsActive(outcome.Status))
        {
            outcome = AutomationRunOutcome.Failed("The runner returned without a final status.");
        }
        else if (outcome.Status == AutomationRunStatus.Cancelled && IsShuttingDown)
        {
            Detach(active);
            return;
        }

        try
        {
            AutomationRun final;
            lock (_gate) final = active.Run;
            final = final with { Status = outcome.Status, EndedAt = _time.GetUtcNow(), Message = outcome.Message ?? final.Message };
            await _runs.UpdateAsync(final, CancellationToken.None).ConfigureAwait(false);
            lock (_gate) active.Run = final;
        }
        catch (Exception)
        {
            // The record keeps its last persisted state; startup recovery will mark it.
        }

        Detach(active);
        RaiseChanged();
    }

    private bool IsShuttingDown
    {
        get { lock (_gate) return _disposed; }
    }

    private void Detach(ActiveRun active)
    {
        lock (_gate)
        {
            _active.Remove(active.Run.Id);
            ReleaseBusyLocked(BusyKey(active.Run.AutomationId, active.Run.TargetSessionId), active.Run.Id);
        }

        active.Cts.Dispose();
    }

    // One active run per (automation, target session); runs without a target are busy per automation.
    private static string BusyKey(string automationId, string? targetSessionId) => automationId + "|" + targetSessionId;

    private void ReleaseBusy(string busyKey, string runId)
    {
        lock (_gate) ReleaseBusyLocked(busyKey, runId);
    }

    private void ReleaseBusyLocked(string busyKey, string runId)
    {
        if (_busy.TryGetValue(busyKey, out var held) && held == runId) _busy.Remove(busyKey);
    }

    private async Task WriteAliveAsync(DateTimeOffset at)
    {
        try
        {
            await _runs.SetStateAsync(AliveAtKey, at.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void Track(Task task)
    {
        if (task.IsCompleted) return;
        lock (_gate) _inflight.Add(task);
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var self = (AutomationEngine)state!;
                lock (self._gate) self._inflight.Remove(t);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Wake()
    {
        TaskCompletionSource wake;
        lock (_gate) wake = _wake;
        wake.TrySetResult();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber must not break the engine.
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AutomationDefinition? FindDefinition(string id)
    {
        foreach (var d in _definitionList)
        {
            if (d.Id == id) return d;
        }

        return null;
    }

    private IAutomationRunner? ResolveRunner(AutomationDefinition def)
    {
        if (_runners.TryGetValue(def.Id, out var runner)) return runner;
        return def.IsBuiltIn ? null : _defaultRunner;
    }

    // Called under _gate (or from the constructor). Rebuilds definitions and per-trigger due times.
    private void Rebuild(DateTimeOffset now, DateTimeOffset? aliveBase, bool preserve)
    {
        var old = _entries;
        var entries = new List<Entry>(BuiltInAutomations.All.Count + _config.Definitions.Count);
        var list = new List<AutomationDefinition>(entries.Capacity);
        var errors = new List<AutomationDefinitionError>(_storeErrors);
        foreach (var id in BuiltInAutomations.All)
        {
            var enabled = !_config.DisabledBuiltIns.Contains(id);
            var def = new AutomationDefinition(id, id) { Enabled = enabled, Triggers = BuiltInTriggers(id) };
            entries.Add(BuildEntry(def, enabled, old, now, aliveBase, preserve, -1, errors));
            list.Add(def);
        }

        for (var index = 0; index < _config.Definitions.Count; index++)
        {
            var def = _config.Definitions[index];
            if (def.IsBuiltIn) continue;
            entries.Add(BuildEntry(def, def.Enabled, old, now, aliveBase, preserve, index, errors));
            list.Add(def);
        }

        _entries = [.. entries];
        _definitionList = list;
        _errors = errors;
    }

    private Entry BuildEntry(AutomationDefinition def, bool enabled, Entry[] old, DateTimeOffset now, DateTimeOffset? aliveBase, bool preserve, int index, List<AutomationDefinitionError> errors)
    {
        var count = def.Triggers.Count;
        Entry? previous = null;
        if (preserve)
        {
            foreach (var e in old)
            {
                if (e.Definition.Id == def.Id) previous = e;
            }
        }

        var reuse = previous is not null
            && previous.Enabled == enabled
            && ReferenceEquals(previous.Definition.Triggers, def.Triggers)
            && previous.Definition.CatchUp == def.CatchUp;
        var schedules = new AutomationSchedule?[count];
        var due = new DateTimeOffset?[count];
        var pending = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (!AutomationSchedule.TryCreate(def.Triggers[i], out var schedule, out var error))
            {
                errors.Add(new AutomationDefinitionError(index, def.Id, $"Trigger {i + 1}: {error}"));
                continue;
            }

            schedules[i] = schedule;
            if (reuse)
            {
                due[i] = previous!.Due[i];
                pending[i] = previous.CatchUpPending[i];
                continue;
            }

            var catchUp = def.CatchUp && aliveBase is not null;
            due[i] = schedule!.Next(catchUp ? aliveBase!.Value : now, _zone);
            pending[i] = catchUp && due[i] is { } d && d <= now;
        }

        return new Entry(def, enabled, schedules, due, pending);
    }

    private static IReadOnlyList<AutomationTrigger> BuiltInTriggers(string id) => id switch
    {
        // Housekeeping first, the catalog fetch an hour later: both are background file/network work, and one starting
        // while the other holds the run store is pointless.
        BuiltInAutomations.Housekeeping =>
            [new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new AutomationTime(4, 0)] }],
        BuiltInAutomations.ModelCatalogRefresh =>
            [new AutomationTrigger(AutomationTriggerKind.Daily) { Times = [new AutomationTime(5, 0)] }],
        _ => [],
    };

    private sealed class Entry(AutomationDefinition definition, bool enabled, AutomationSchedule?[] schedules, DateTimeOffset?[] due, bool[] catchUpPending)
    {
        public AutomationDefinition Definition { get; } = definition;

        public bool Enabled { get; } = enabled;

        public AutomationSchedule?[] Schedules { get; } = schedules;

        public DateTimeOffset?[] Due { get; } = due;

        public bool[] CatchUpPending { get; } = catchUpPending;
    }

    private readonly record struct Fire(AutomationDefinition Definition, int TriggerIndex, DateTimeOffset Due, AutomationTriggerSource Source);

    private sealed class ActiveRun(AutomationRun run, AutomationDefinition definition, IAutomationRunner runner, bool isResumed)
    {
        public AutomationRun Run { get; set; } = run;

        public AutomationDefinition Definition { get; } = definition;

        public IAutomationRunner Runner { get; } = runner;

        public bool IsResumed { get; } = isResumed;

        public CancellationTokenSource Cts { get; } = new();

        /// <summary>Serializes run updates: a progress report and the runner's own update must not overwrite each other.</summary>
        public SemaphoreSlim Updates { get; } = new(1, 1);

        public string? CancelReason { get; set; }
    }

    private sealed class RunContext(AutomationEngine engine, ActiveRun active) : IAutomationRunContext
    {
        public AutomationRun Run
        {
            get { lock (engine._gate) return active.Run; }
        }

        public AutomationDefinition Definition => active.Definition;

        public bool IsResumed => active.IsResumed;

        public async Task UpdateAsync(Func<AutomationRun, AutomationRun> change, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(change);
            await active.Updates.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var updated = change(Run);
                if (!AutomationRunStatuses.IsActive(updated.Status))
                {
                    throw new InvalidOperationException("A runner ends a run by returning its outcome, not through UpdateAsync.");
                }

                if (updated.Id != active.Run.Id || updated.AutomationId != active.Run.AutomationId)
                {
                    throw new InvalidOperationException("A run update cannot change the run's identity.");
                }

                await engine._runs.UpdateAsync(updated, ct).ConfigureAwait(false);
                lock (engine._gate) active.Run = updated;
            }
            finally
            {
                active.Updates.Release();
            }

            engine.RaiseChanged();
        }
    }
}
