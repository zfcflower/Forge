using Forge.Abstractions;
using Forge.Core.Automations;
using Xunit;

namespace Forge.Core.Tests.Automations;

public class AutomationEngineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static DateTimeOffset At(int h, int mi = 0, int s = 0, int d = 2) => new(2025, 6, d, h, mi, s, TimeSpan.Zero);

    private static AutomationTrigger DailyAt(int h, int mi = 0) => new(AutomationTriggerKind.Daily) { Times = [new AutomationTime(h, mi)] };

    private static AutomationTrigger HourlyAt(int minute = 0) => new(AutomationTriggerKind.Hourly) { IntervalHours = 1, Minute = minute };

    private static AutomationDefinition Def(string id, params AutomationTrigger[] triggers) =>
        new(id, "Name " + id) { Triggers = triggers };

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(DateTimeOffset now, IAutomationRunner? runner = null, params AutomationDefinition[] defs)
        {
            Time = new ManualTimeProvider(now);
            Definitions.Config = new AutomationConfig { Definitions = defs };
            Runner = runner is null ? DelegateRunner.Completing() : runner;
            Engine = new AutomationEngine(Definitions, Runs, Time, TimeZoneInfo.Utc, Runner);
        }

        public ManualTimeProvider Time { get; }

        public FakeDefinitionStore Definitions { get; } = new();

        public FakeRunStore Runs { get; } = new();

        public IAutomationRunner Runner { get; }

        public AutomationEngine Engine { get; }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();

        public async Task TickAt(DateTimeOffset now)
        {
            Time.Set(now);
            Engine.Tick(now);
            await Engine.WhenIdleAsync().WaitAsync(Timeout);
        }
    }

    private static async Task<Harness> Started(DateTimeOffset now, IAutomationRunner? runner = null, params AutomationDefinition[] defs)
    {
        var h = new Harness(now, runner, defs);
        await h.Engine.StartupAsync(CancellationToken.None);
        return h;
    }

    // ---- lateness ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fire_within_two_minutes_late_runs()
    {
        await using var h = await Started(At(11), null, Def("a", DailyAt(12)));

        await h.TickAt(At(12, 1, 30));

        var run = Assert.Single(h.Runs.All);
        Assert.Equal(AutomationRunStatus.Completed, run.Status);
        Assert.Equal(AutomationTriggerSource.Schedule, run.Source);
        Assert.Equal("t0@2025-06-02T12:00:00Z", run.Occurrence);
    }

    [Fact]
    public async Task Fire_beyond_two_minutes_late_is_not_run_and_the_next_time_is_computed_from_now()
    {
        await using var h = await Started(At(11), null, Def("a", DailyAt(12)));

        await h.TickAt(At(12, 3));

        Assert.Empty(h.Runs.All);
        Assert.Equal(At(12, d: 3), h.Engine.NextFireTime("a"));
    }

    [Fact]
    public async Task A_long_gap_never_produces_a_burst()
    {
        await using var h = await Started(At(0), null, Def("a", HourlyAt(0)));

        await h.TickAt(At(0, 0, 0, d: 3)); // a day later: 24 occurrences were missed

        Assert.Empty(h.Runs.All);
        Assert.Equal(At(1, d: 3), h.Engine.NextFireTime("a"));
    }

    [Fact]
    public async Task Two_triggers_due_at_the_same_moment_fire_once()
    {
        await using var h = await Started(At(11), null, Def("a", DailyAt(12), DailyAt(12)));

        await h.TickAt(At(12, 0, 5));

        Assert.Single(h.Runs.All);
    }

    // ---- catch-up --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CatchUp_runs_missed_occurrences_once()
    {
        var store = new FakeRunStore();
        await store.SetStateAsync(AutomationEngine.AliveAtKey, At(10).ToString("O"), CancellationToken.None);
        var time = new ManualTimeProvider(At(13, 10));
        var defs = new FakeDefinitionStore { Config = new AutomationConfig { Definitions = [Def("a", HourlyAt(0)) with { CatchUp = true }, Def("b", HourlyAt(0))] } };
        var runner = DelegateRunner.Completing();
        await using var engine = new AutomationEngine(defs, store, time, TimeZoneInfo.Utc, runner);
        await engine.StartupAsync(CancellationToken.None);

        engine.Tick(At(13, 10, 1));
        await engine.WhenIdleAsync();
        engine.Tick(At(13, 10, 10));
        await engine.WhenIdleAsync();

        var run = Assert.Single(store.All, r => r.AutomationId == "a");
        Assert.Equal(AutomationTriggerSource.CatchUp, run.Source);
        Assert.Equal("t0@2025-06-02T11:00:00Z", run.Occurrence);
        Assert.DoesNotContain(store.All, r => r.AutomationId == "b");
        Assert.Equal(At(14), engine.NextFireTime("a"));
    }

    [Fact]
    public async Task CatchUp_does_nothing_when_nothing_was_missed()
    {
        var store = new FakeRunStore();
        await store.SetStateAsync(AutomationEngine.AliveAtKey, At(12, 30).ToString("O"), CancellationToken.None);
        var time = new ManualTimeProvider(At(12, 40));
        var defs = new FakeDefinitionStore { Config = new AutomationConfig { Definitions = [Def("a", HourlyAt(0)) with { CatchUp = true }] } };
        await using var engine = new AutomationEngine(defs, store, time, TimeZoneInfo.Utc, DelegateRunner.Completing());
        await engine.StartupAsync(CancellationToken.None);

        engine.Tick(At(12, 40, 1));
        await engine.WhenIdleAsync();

        Assert.Empty(store.All);
    }

    // ---- busy ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scheduled_fire_while_busy_is_skipped_not_queued()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = DelegateRunner.Blocking(started, gate);
        await using var h = await Started(At(11, 59), runner, Def("a", HourlyAt(0)));

        h.Time.Set(At(12));
        h.Engine.Tick(At(12));
        await started.Task.WaitAsync(Timeout);
        h.Time.Set(At(13));
        h.Engine.Tick(At(13));

        var skipped = Assert.Single(h.Runs.All, r => r.Status == AutomationRunStatus.Skipped);
        Assert.Contains("still in progress", skipped.Message);
        Assert.Equal("t0@2025-06-02T13:00:00Z", skipped.Occurrence);
        gate.SetResult();
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(1, ((DelegateRunner)runner).Calls);
        Assert.Equal(2, h.Runs.All.Count);
        Assert.Empty(h.Engine.ActiveRuns);
    }

    [Fact]
    public async Task Manual_start_while_busy_is_refused_and_recorded()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await Started(At(9), DelegateRunner.Blocking(started, gate), Def("a"));

        var first = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await started.Task.WaitAsync(Timeout);
        var second = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);

        Assert.Equal(AutomationStartStatus.Started, first.Status);
        Assert.Equal(AutomationStartStatus.Busy, second.Status);
        Assert.Equal(AutomationRunStatus.Skipped, second.Run!.Status);
        Assert.Single(h.Engine.ActiveRuns);
        gate.SetResult();
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Busy_is_keyed_by_automation_and_target_session()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var runner = new DelegateRunner(async (_, ct) =>
        {
            Interlocked.Increment(ref starts);
            await release.Task.WaitAsync(ct);
            return AutomationRunOutcome.Completed();
        });
        await using var h = await Started(At(9), runner, Def("a"));

        var s1 = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetSessionId = "s1" }, CancellationToken.None);
        var s2 = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetSessionId = "s2" }, CancellationToken.None);
        var s1Again = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetSessionId = "s1" }, CancellationToken.None);
        var noTarget = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        var noTargetAgain = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);

        Assert.Equal(AutomationStartStatus.Started, s1.Status);
        Assert.Equal(AutomationStartStatus.Started, s2.Status);
        Assert.Equal(AutomationStartStatus.Busy, s1Again.Status);
        Assert.Equal(AutomationStartStatus.Started, noTarget.Status);
        Assert.Equal(AutomationStartStatus.Busy, noTargetAgain.Status);
        Assert.Equal(3, h.Engine.ActiveRuns.Count);
        release.SetResult();
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(3, starts);
        // Released: the same session can run again.
        var later = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetSessionId = "s1" }, CancellationToken.None);
        Assert.Equal(AutomationStartStatus.Started, later.Status);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
    }

    // ---- startup recovery ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Startup_marks_active_runs_interrupted_at_the_last_heartbeat()
    {
        var store = new FakeRunStore();
        store.Seed(new AutomationRun("r1", "a", "A", AutomationTriggerSource.Schedule, "o1", AutomationRunStatus.Running, At(8, 59)));
        store.Seed(new AutomationRun("r2", "a", "A", AutomationTriggerSource.Schedule, "o2", AutomationRunStatus.WaitingIdle, At(8, 59)));
        await store.SetStateAsync(AutomationEngine.AliveAtKey, At(9).ToString("O"), CancellationToken.None);
        await using var engine = new AutomationEngine(new FakeDefinitionStore(), store, new ManualTimeProvider(At(12)), TimeZoneInfo.Utc, DelegateRunner.Completing());

        await engine.StartupAsync(CancellationToken.None);

        foreach (var id in new[] { "r1", "r2" })
        {
            var r = store.Get(id);
            Assert.Equal(AutomationRunStatus.Interrupted, r.Status);
            Assert.Equal(At(9), r.EndedAt);
        }
    }

    [Fact]
    public async Task Startup_without_a_heartbeat_uses_now()
    {
        var store = new FakeRunStore();
        store.Seed(new AutomationRun("r1", "a", "A", AutomationTriggerSource.Schedule, "o1", AutomationRunStatus.Running, At(8)));
        await using var engine = new AutomationEngine(new FakeDefinitionStore(), store, new ManualTimeProvider(At(12)), TimeZoneInfo.Utc, DelegateRunner.Completing());

        await engine.StartupAsync(CancellationToken.None);

        Assert.Equal(At(12), store.Get("r1").EndedAt);
    }

    [Fact]
    public async Task Waiting_quota_runs_are_resumed_after_a_restart()
    {
        var store = new FakeRunStore();
        store.Seed(new AutomationRun("w1", BuiltInAutomations.QuotaResume, "Q", AutomationTriggerSource.Event, "e1", AutomationRunStatus.WaitingQuota, At(8))
        {
            ResumeAt = At(9),
            TargetSessionId = "s1",
        });
        var seen = new TaskCompletionSource<(bool Resumed, string RunId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner((ctx, _) =>
        {
            seen.TrySetResult((ctx.IsResumed, ctx.Run.Id));
            return Task.FromResult(AutomationRunOutcome.Completed("resumed"));
        });
        await using var engine = new AutomationEngine(new FakeDefinitionStore(), store, new ManualTimeProvider(At(12)), TimeZoneInfo.Utc, DelegateRunner.Completing());
        engine.RegisterRunner(BuiltInAutomations.QuotaResume, runner);

        await engine.StartupAsync(CancellationToken.None);

        var (resumed, id) = await seen.Task.WaitAsync(Timeout);
        Assert.True(resumed);
        Assert.Equal("w1", id);
        await engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(AutomationRunStatus.Completed, store.Get("w1").Status);
    }

    [Fact]
    public async Task Quota_runs_waiting_for_an_idle_session_survive_a_restart_others_are_interrupted()
    {
        var store = new FakeRunStore();
        store.Seed(new AutomationRun("q1", BuiltInAutomations.QuotaResume, "Q", AutomationTriggerSource.Event, "e1", AutomationRunStatus.WaitingIdle, At(8))
        {
            TargetSessionId = "s1",
        });
        store.Seed(new AutomationRun("u1", "user-1", "U", AutomationTriggerSource.Schedule, "e2", AutomationRunStatus.WaitingIdle, At(8))
        {
            TargetSessionId = "s2",
        });
        var seen = new TaskCompletionSource<AutomationRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner((ctx, _) =>
        {
            seen.TrySetResult(ctx.Run);
            return Task.FromResult(AutomationRunOutcome.Completed("resumed"));
        });
        await using var engine = new AutomationEngine(new FakeDefinitionStore(), store, new ManualTimeProvider(At(12)), TimeZoneInfo.Utc, DelegateRunner.Completing());
        engine.RegisterRunner(BuiltInAutomations.QuotaResume, runner);

        await engine.StartupAsync(CancellationToken.None);

        var resumed = await seen.Task.WaitAsync(Timeout);
        Assert.Equal("q1", resumed.Id);
        Assert.Equal(AutomationRunStatus.WaitingQuota, resumed.Status);
        Assert.Null(resumed.ResumeAt);
        Assert.Equal(AutomationRunStatus.Interrupted, store.Get("u1").Status);
    }

    [Fact]
    public async Task Concurrent_run_updates_are_applied_one_after_another()
    {
        // Each write takes a moment, so unserialized updates would all read the same base and lose increments.
        var store = new FakeRunStore { UpdateDelay = TimeSpan.FromMilliseconds(1) };
        var done = new TaskCompletionSource<AutomationRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner(async (ctx, ct) =>
        {
            // Each update reads the run and adds one to its message counter; unserialized, updates read the same base.
            var updates = Enumerable.Range(0, 50).Select(_ => Task.Run(() => ctx.UpdateAsync(
                run => run with { Message = ((int.TryParse(run.Message, out var n) ? n : 0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ct)));
            await Task.WhenAll(updates);
            done.TrySetResult(ctx.Run);
            return AutomationRunOutcome.Completed();
        });
        await using var engine = new AutomationEngine(new FakeDefinitionStore(), store, new ManualTimeProvider(At(12)), TimeZoneInfo.Utc, DelegateRunner.Completing());
        engine.RegisterRunner(BuiltInAutomations.QuotaResume, runner);
        await engine.StartupAsync(CancellationToken.None);

        await engine.StartAsync(new AutomationRunRequest(BuiltInAutomations.QuotaResume, AutomationTriggerSource.Event) { TargetSessionId = "s1" }, CancellationToken.None);

        var final = await done.Task.WaitAsync(Timeout);
        Assert.Equal("50", final.Message);
    }

    [Fact]
    public async Task Bad_definitions_are_reported_and_the_others_still_work()
    {
        var store = new FakeRunStore();
        var defs = new FakeDefinitionStore
        {
            Config = new AutomationConfig
            {
                Definitions =
                [
                    Def("good", DailyAt(12)),
                    Def("badcron", new AutomationTrigger(AutomationTriggerKind.Cron) { Cron = "nope" }),
                ],
            },
            Errors = [new AutomationDefinitionError(2, "broken", "bad json")],
        };
        await using var engine = new AutomationEngine(defs, store, new ManualTimeProvider(At(11)), TimeZoneInfo.Utc, DelegateRunner.Completing());

        await engine.StartupAsync(CancellationToken.None);
        engine.Tick(At(12));
        await engine.WhenIdleAsync();

        Assert.Equal(2, engine.ConfigErrors.Count);
        Assert.Contains(engine.ConfigErrors, e => e.Id == "broken");
        Assert.Contains(engine.ConfigErrors, e => e.Id == "badcron");
        Assert.Single(store.All, r => r.AutomationId == "good");
        Assert.Null(engine.NextFireTime("badcron"));
    }

    // ---- occurrences, pause, manual ----------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_occurrence_is_not_run_or_recorded_again()
    {
        await using var h = await Started(At(9), null, Def("a"));
        var request = new AutomationRunRequest("a", AutomationTriggerSource.Event) { Occurrence = "ev-1" };

        var first = await h.Engine.StartAsync(request, CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        var second = await h.Engine.StartAsync(request, CancellationToken.None);

        Assert.Equal(AutomationStartStatus.Started, first.Status);
        Assert.Equal(AutomationStartStatus.Duplicate, second.Status);
        Assert.Null(second.Run);
        Assert.Single(h.Runs.All);
        Assert.Equal(1, ((DelegateRunner)h.Runner).Calls);
    }

    [Fact]
    public async Task A_scheduled_occurrence_already_recorded_is_a_duplicate()
    {
        var store = new FakeRunStore();
        store.Seed(new AutomationRun("old", "a", "A", AutomationTriggerSource.Schedule, "t0@2025-06-02T12:00:00Z", AutomationRunStatus.Completed, At(12)));
        var defs = new FakeDefinitionStore { Config = new AutomationConfig { Definitions = [Def("a", DailyAt(12))] } };
        var runner = DelegateRunner.Completing();
        await using var engine = new AutomationEngine(defs, store, new ManualTimeProvider(At(11)), TimeZoneInfo.Utc, runner);
        await engine.StartupAsync(CancellationToken.None);

        engine.Tick(At(12, 0, 1));
        await engine.WhenIdleAsync();

        Assert.Equal(0, runner.Calls);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task Pause_stops_scheduled_fires_but_manual_runs_work_even_for_disabled_automations()
    {
        await using var h = await Started(At(11), null, Def("a", DailyAt(12)), Def("off", DailyAt(12)) with { Enabled = false });
        await h.Engine.UpdateConfigAsync(c => c with { Paused = true }, CancellationToken.None);

        await h.TickAt(At(12, 0, 1));

        Assert.Empty(h.Runs.All);
        Assert.Null(h.Engine.NextFireTime("a"));
        var manual = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        var disabled = await h.Engine.StartAsync(new AutomationRunRequest("off", AutomationTriggerSource.Manual), CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(AutomationStartStatus.Started, manual.Status);
        Assert.Equal(AutomationStartStatus.Started, disabled.Status);
        Assert.All(h.Runs.All, r => Assert.Equal(AutomationRunStatus.Completed, r.Status));
    }

    [Fact]
    public async Task Disabled_automation_does_not_fire_on_schedule()
    {
        await using var h = await Started(At(11), null, Def("off", DailyAt(12)) with { Enabled = false });

        await h.TickAt(At(12));

        Assert.Empty(h.Runs.All);
        Assert.Null(h.Engine.NextFireTime("off"));
    }

    [Fact]
    public async Task Resuming_after_a_pause_does_not_fire_stale_occurrences()
    {
        await using var h = await Started(At(11), null, Def("a", DailyAt(12)));
        await h.Engine.UpdateConfigAsync(c => c with { Paused = true }, CancellationToken.None);
        await h.TickAt(At(12, 0, 1));

        h.Time.Set(At(15));
        await h.Engine.UpdateConfigAsync(c => c with { Paused = false }, CancellationToken.None);
        await h.TickAt(At(15, 0, 1));

        Assert.Empty(h.Runs.All);
        Assert.Equal(At(12, d: 3), h.Engine.NextFireTime("a"));
    }

    [Fact]
    public async Task Unknown_automation_is_refused_without_a_record()
    {
        await using var h = await Started(At(9));

        var result = await h.Engine.StartAsync(new AutomationRunRequest("nope", AutomationTriggerSource.Manual), CancellationToken.None);

        Assert.Equal(AutomationStartStatus.UnknownAutomation, result.Status);
        Assert.Empty(h.Runs.All);
    }

    // ---- built-ins, config -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Built_ins_are_listed_first_and_housekeeping_runs_daily_at_four()
    {
        await using var h = await Started(At(9), null, Def("a"));
        await h.Engine.UpdateConfigAsync(c => c with { DisabledBuiltIns = [BuiltInAutomations.SessionWatch] }, CancellationToken.None);

        var defs = h.Engine.Definitions;

        Assert.Equal([.. BuiltInAutomations.All, "a"], defs.Select(d => d.Id));
        Assert.False(defs.Single(d => d.Id == BuiltInAutomations.SessionWatch).Enabled);
        Assert.True(defs.Single(d => d.Id == BuiltInAutomations.QuotaResume).Enabled);
        Assert.Empty(defs.Single(d => d.Id == BuiltInAutomations.CiChain).Triggers);
        Assert.Equal(At(4, d: 3), h.Engine.NextFireTime(BuiltInAutomations.Housekeeping));
        Assert.Null(h.Engine.NextFireTime(BuiltInAutomations.QuotaResume));
    }

    [Fact]
    public async Task Built_in_runs_use_their_registered_runner()
    {
        await using var h = await Started(At(3, 59));
        var housekeeping = DelegateRunner.Completing();
        h.Engine.RegisterRunner(BuiltInAutomations.Housekeeping, housekeeping);

        await h.TickAt(At(4));

        Assert.Equal(1, housekeeping.Calls);
        Assert.Equal(0, ((DelegateRunner)h.Runner).Calls);
        Assert.Equal(BuiltInAutomations.Housekeeping, Assert.Single(h.Runs.All).AutomationId);
    }

    [Fact]
    public async Task UpdateConfig_saves_reschedules_and_raises_Changed()
    {
        await using var h = await Started(At(9));
        var changed = 0;
        h.Engine.Changed += (_, _) => Interlocked.Increment(ref changed);

        await h.Engine.UpdateConfigAsync(c => c with { Definitions = [Def("n", DailyAt(10))] }, CancellationToken.None);

        Assert.Equal(1, h.Definitions.Saves);
        Assert.Equal("n", Assert.Single(h.Definitions.Config.Definitions).Id);
        Assert.Equal(At(10), h.Engine.NextFireTime("n"));
        Assert.True(changed >= 1);
    }

    // ---- chain & budget ------------------------------------------------------------------------------------------------

    private static AutomationRun Cause(string id, string automation, int depth) =>
        new(id, automation, automation, AutomationTriggerSource.Event, "c-" + id, AutomationRunStatus.Completed, At(8)) { ChainDepth = depth };

    [Fact]
    public async Task Chain_depth_is_the_cause_depth_plus_one_and_capped_at_five()
    {
        await using var h = await Started(At(9), null, Def("a"), Def("b"));
        h.Runs.Seed(Cause("c4", "b", 4));
        h.Runs.Seed(Cause("c5", "b", 5));

        var ok = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Chain) { CauseRunId = "c4" }, CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        var tooDeep = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Chain) { CauseRunId = "c5" }, CancellationToken.None);

        Assert.Equal(AutomationStartStatus.Started, ok.Status);
        Assert.Equal(5, ok.Run!.ChainDepth);
        Assert.Equal(AutomationStartStatus.ChainTooDeep, tooDeep.Status);
        Assert.Equal(AutomationRunStatus.Skipped, tooDeep.Run!.Status);
        Assert.NotNull(tooDeep.Run.Message);
        Assert.Equal(1, ((DelegateRunner)h.Runner).Calls);
        Assert.Equal(AutomationRunStatus.Skipped, h.Runs.Get(tooDeep.Run.Id).Status);
    }

    [Fact]
    public async Task An_automation_cannot_be_triggered_by_its_own_run()
    {
        await using var h = await Started(At(9), null, Def("a"));
        h.Runs.Seed(Cause("c1", "a", 0));

        var result = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Chain) { CauseRunId = "c1" }, CancellationToken.None);

        Assert.Equal(AutomationStartStatus.SelfTriggered, result.Status);
        Assert.Equal(AutomationRunStatus.Skipped, result.Run!.Status);
        Assert.Equal(0, ((DelegateRunner)h.Runner).Calls);
    }

    [Fact]
    public async Task Target_budget_stops_after_three_consecutive_attempts_and_reset_clears_it()
    {
        await using var h = await Started(At(9), null, Def("a"));
        AutomationRunRequest Req() => new("a", AutomationTriggerSource.Event) { TargetKey = "ci:repo#7" };

        for (var i = 0; i < 3; i++)
        {
            var r = await h.Engine.StartAsync(Req(), CancellationToken.None);
            Assert.Equal(AutomationStartStatus.Started, r.Status);
            await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        }

        var exhausted = await h.Engine.StartAsync(Req(), CancellationToken.None);
        Assert.Equal(AutomationStartStatus.BudgetExhausted, exhausted.Status);
        Assert.Equal(AutomationRunStatus.Skipped, exhausted.Run!.Status);
        Assert.Equal(3, ((DelegateRunner)h.Runner).Calls);

        await h.Engine.ResetTargetBudgetAsync("ci:repo#7", CancellationToken.None);
        var again = await h.Engine.StartAsync(Req(), CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(AutomationStartStatus.Started, again.Status);
    }

    [Fact]
    public async Task Target_budget_is_per_key()
    {
        await using var h = await Started(At(9), null, Def("a"));
        for (var i = 0; i < 3; i++)
        {
            await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetKey = "k1" }, CancellationToken.None);
            await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        }

        var other = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Event) { TargetKey = "k2" }, CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);

        Assert.Equal(AutomationStartStatus.Started, other.Status);
    }

    // ---- execution -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_ends_the_run_as_cancelled_with_the_reason()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return AutomationRunOutcome.Completed();
        });
        await using var h = await Started(At(9), runner, Def("a"));
        var start = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await started.Task.WaitAsync(Timeout);

        Assert.True(h.Engine.Cancel(start.Run!.Id, "user asked"));
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);

        var run = h.Runs.Get(start.Run.Id);
        Assert.Equal(AutomationRunStatus.Cancelled, run.Status);
        Assert.Equal("user asked", run.Message);
        Assert.NotNull(run.EndedAt);
        Assert.Empty(h.Engine.ActiveRuns);
        Assert.False(h.Engine.Cancel(start.Run.Id, "again"));
        Assert.False(h.Engine.Cancel("unknown", "x"));
    }

    [Fact]
    public async Task Runner_exception_fails_the_run_and_frees_the_automation()
    {
        var fail = true;
        var runner = new DelegateRunner((_, _) => fail ? throw new InvalidOperationException("boom") : Task.FromResult(AutomationRunOutcome.Completed()));
        await using var h = await Started(At(9), runner, Def("a"));

        var first = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);

        var run = h.Runs.Get(first.Run!.Id);
        Assert.Equal(AutomationRunStatus.Failed, run.Status);
        Assert.Equal("boom", run.Message);
        Assert.NotNull(run.EndedAt);
        fail = false;
        var second = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        Assert.Equal(AutomationStartStatus.Started, second.Status);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Outcome_status_and_message_are_recorded()
    {
        var runner = new DelegateRunner((_, _) => Task.FromResult(AutomationRunOutcome.Skipped("nothing to do")));
        await using var h = await Started(At(9), runner, Def("a"));

        var r = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);

        var run = h.Runs.Get(r.Run!.Id);
        Assert.Equal(AutomationRunStatus.Skipped, run.Status);
        Assert.Equal("nothing to do", run.Message);
    }

    [Fact]
    public async Task Context_updates_are_persisted_and_raise_Changed_but_cannot_end_the_run()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? terminalError = null;
        var runner = new DelegateRunner(async (ctx, ct) =>
        {
            await ctx.UpdateAsync(r => r with { Status = AutomationRunStatus.WaitingIdle, SessionId = "sess" }, ct);
            try
            {
                await ctx.UpdateAsync(r => r with { Status = AutomationRunStatus.Completed }, ct);
            }
            catch (InvalidOperationException ex)
            {
                terminalError = ex;
            }

            waiting.TrySetResult();
            await release.Task.WaitAsync(ct);
            return AutomationRunOutcome.Completed();
        });
        await using var h = await Started(At(9), runner, Def("a"));
        var changed = 0;
        h.Engine.Changed += (_, _) => Interlocked.Increment(ref changed);

        var r = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await waiting.Task.WaitAsync(Timeout);

        var stored = h.Runs.Get(r.Run!.Id);
        Assert.Equal(AutomationRunStatus.WaitingIdle, stored.Status);
        Assert.Equal("sess", stored.SessionId);
        Assert.NotNull(terminalError);
        Assert.Equal(AutomationRunStatus.WaitingIdle, Assert.Single(h.Engine.ActiveRuns).Status);
        Assert.True(changed >= 2);
        release.SetResult();
        await h.Engine.WhenIdleAsync().WaitAsync(Timeout);
        Assert.Equal(AutomationRunStatus.Completed, h.Runs.Get(r.Run.Id).Status);
    }

    [Fact]
    public async Task Disposing_cancels_active_runs_but_leaves_their_records_for_recovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return AutomationRunOutcome.Completed();
        });
        var h = await Started(At(9), runner, Def("a"));
        var r = await h.Engine.StartAsync(new AutomationRunRequest("a", AutomationTriggerSource.Manual), CancellationToken.None);
        await started.Task.WaitAsync(Timeout);

        await h.DisposeAsync();

        var run = h.Runs.Get(r.Run!.Id);
        Assert.Equal(AutomationRunStatus.Running, run.Status);
        Assert.Null(run.EndedAt);
    }

    // ---- heartbeat, loop -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_is_written_at_most_once_a_minute_and_flushed_on_dispose()
    {
        var h = await Started(At(9));
        var baseline = h.Runs.AliveWrites;

        h.Engine.Tick(At(9, 0, 10));
        await h.Engine.WhenIdleAsync();
        h.Engine.Tick(At(9, 0, 40));
        await h.Engine.WhenIdleAsync();
        var within = h.Runs.AliveWrites;
        h.Engine.Tick(At(9, 1, 5));
        await h.Engine.WhenIdleAsync();
        var after = h.Runs.AliveWrites;
        h.Time.Set(At(9, 5));
        await h.DisposeAsync();

        Assert.Equal(baseline, within);
        Assert.Equal(baseline + 1, after);
        Assert.Equal(after + 1, h.Runs.AliveWrites);
        Assert.Equal(At(9, 5), DateTimeOffset.Parse(h.Runs.State(AutomationEngine.AliveAtKey)!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task A_failing_heartbeat_write_does_not_break_Tick()
    {
        await using var h = await Started(At(9));
        h.Runs.SetStateFailure = new IOException("disk full");

        var wait = h.Engine.Tick(At(9, 2));
        await h.Engine.WhenIdleAsync();

        Assert.InRange(wait, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
        h.Runs.SetStateFailure = null;
    }

    [Fact]
    public async Task Tick_wait_is_at_least_250ms_and_at_most_30s()
    {
        await using var h = await Started(At(9), null, Def("a", DailyAt(12)));

        Assert.Equal(TimeSpan.FromSeconds(30), h.Engine.Tick(At(9, 0, 0)));
        Assert.Equal(TimeSpan.FromSeconds(10), h.Engine.Tick(At(11, 59, 50)));
        Assert.Equal(TimeSpan.FromMilliseconds(250), h.Engine.Tick(At(11, 59, 59, 2).AddMilliseconds(900)));
    }

    [Fact]
    public async Task A_tick_exception_does_not_end_the_loop()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner((_, _) =>
        {
            ran.TrySetResult();
            return Task.FromResult(AutomationRunOutcome.Completed());
        });
        await using var h = await Started(At(11, 59), runner, Def("a", DailyAt(12)));
        using var cts = new CancellationTokenSource();
        h.Time.ArmThrow(new InvalidOperationException("clock broke"));

        var loop = h.Engine.RunAsync(cts.Token);
        Assert.False(loop.IsCompleted);
        // The failed tick backs off one second; then the loop ticks again at a time when the run is due.
        h.Time.Set(At(12));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await ran.Task.WaitAsync(Timeout);
        cts.Cancel();
        await loop.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_config_change_wakes_the_loop()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new DelegateRunner((_, _) =>
        {
            ran.TrySetResult();
            return Task.FromResult(AutomationRunOutcome.Completed());
        });
        await using var h = await Started(At(11, 59, 59), runner);
        using var cts = new CancellationTokenSource();
        var loop = h.Engine.RunAsync(cts.Token);

        h.Time.Set(At(11, 59, 59, 2));
        await h.Engine.UpdateConfigAsync(c => c with { Definitions = [Def("a", DailyAt(12))] }, CancellationToken.None);
        // Without advancing the timers, the wake alone makes the loop tick again; move the clock to the due time first.
        h.Time.Set(At(12));
        await h.Engine.UpdateConfigAsync(c => c with { Paused = false }, CancellationToken.None);

        await ran.Task.WaitAsync(Timeout);
        cts.Cancel();
        await loop.WaitAsync(Timeout);
    }
}
