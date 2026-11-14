using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class QuotaResumeTests
{
    private const string Occurrence = "occ-1";

    // ------------------------------------------------------------------ QuotaNote

    [Fact]
    public void Note_round_trips_all_fields()
    {
        var reset = new DateTimeOffset(2026, 11, 11, 12, 30, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 11, 11, 10, 0, 5, TimeSpan.Zero);
        var note = new QuotaNote("prov", "model-x", reset, at, "Title", "Message");

        var parsed = QuotaNote.Parse(note.Serialize());

        Assert.Equal(note, parsed);
        Assert.Equal(new ModelSettings("model-x", ProviderId: "prov"), parsed!.Settings);
    }

    [Fact]
    public void Note_round_trips_null_provider_and_null_reset()
    {
        var at = new DateTimeOffset(2026, 11, 11, 10, 0, 5, TimeSpan.Zero);
        var note = new QuotaNote(null, "m", null, at, "t", "msg");

        var parsed = QuotaNote.Parse(note.Serialize());

        Assert.NotNull(parsed);
        Assert.Null(parsed.ProviderId);
        Assert.Null(parsed.ResetAt);
        Assert.Equal(at, parsed.At);
        Assert.Null(parsed.Settings.ProviderId);
    }

    [Fact]
    public void Note_serialization_flattens_line_breaks_so_fields_stay_aligned()
    {
        var at = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        var note = new QuotaNote("p", "m", null, at, "a\nb\rc", "x\ny");

        var parsed = QuotaNote.Parse(note.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal("a b c", parsed.Title);
        Assert.Equal("x y", parsed.Message);
    }

    [Fact]
    public void Note_parse_normalizes_to_utc_offsets()
    {
        var local = new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.FromHours(8));
        var note = new QuotaNote("p", "m", local, local, "t", "m");

        var parsed = QuotaNote.Parse(note.Serialize());

        Assert.Equal(local, parsed!.ResetAt);
        Assert.Equal(TimeSpan.Zero, parsed.ResetAt!.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("p\nm\n\n123")]
    [InlineData("p\nm\n\nnot-a-number\nt\nmsg")]
    public void Note_parse_rejects_bad_input(string? text)
    {
        Assert.Null(QuotaNote.Parse(text));
    }

    [Fact]
    public void Note_parse_with_an_unreadable_reset_keeps_the_note_without_reset()
    {
        var parsed = QuotaNote.Parse("p\nm\nxx\n636000000000000000\nt\nmsg");

        Assert.NotNull(parsed);
        Assert.Null(parsed.ResetAt);
        Assert.Equal("msg", parsed.Message);
    }

    // ------------------------------------------- automation slots (ProviderRateLimiter)

    [Fact]
    public void Automation_slots_space_one_provider_by_the_gap()
    {
        var limiter = new ProviderRateLimiter(new SharedClock(new CiFakeTime()));

        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("a"));
        Assert.Equal(ProviderRateLimitPolicy.Default.AutomationGap, limiter.ReserveAutomationSlot("a"));
        Assert.Equal(ProviderRateLimitPolicy.Default.AutomationGap * 2, limiter.ReserveAutomationSlot("a"));

        // The gap the settings section shows by default is ten seconds.
        Assert.Equal(TimeSpan.FromSeconds(10), ProviderRateLimitPolicy.Default.AutomationGap);
    }

    [Fact]
    public void Automation_slots_are_independent_per_provider_and_case_sensitive()
    {
        var limiter = new ProviderRateLimiter(new SharedClock(new CiFakeTime()));

        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("a"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("b"));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("A"));
        Assert.Equal(ProviderRateLimitPolicy.Default.AutomationGap, limiter.ReserveAutomationSlot("b"));
    }

    [Fact]
    public void An_automation_slot_shrinks_with_time_and_resets_after_the_slots_pass()
    {
        var time = new CiFakeTime();
        var limiter = new ProviderRateLimiter(new SharedClock(time));
        limiter.ReserveAutomationSlot("a");
        limiter.ReserveAutomationSlot("a");
        limiter.ReserveAutomationSlot("a");

        time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.ReserveAutomationSlot("a"));

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, limiter.ReserveAutomationSlot("a"));
    }

    [Fact]
    public async Task Automation_slots_are_distinct_under_concurrency()
    {
        var limiter = new ProviderRateLimiter(new SharedClock(new CiFakeTime()));

        var waits = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => limiter.ReserveAutomationSlot("a"))));

        var expected = Enumerable.Range(0, 32).Select(index => ProviderRateLimitPolicy.Default.AutomationGap * index);
        Assert.Equal(expected, waits.OrderBy(static wait => wait));
    }

    // ------------------------------------------------------------------ ResumeAtFor

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 30)]
    [InlineData(1.0, 60)]
    [InlineData(7.0, 60)]
    [InlineData(-3.0, 0)]
    public void ResumeAtFor_adds_clamped_jitter_to_the_reset_time(double random, int expectedSeconds)
    {
        var h = new Harness(random: () => random);
        var reset = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(reset + TimeSpan.FromSeconds(expectedSeconds), h.Runner.ResumeAtFor(reset));
    }

    [Fact]
    public void ResumeAtFor_is_null_without_a_reset_time_so_the_run_probes()
    {
        Assert.Null(new Harness().Runner.ResumeAtFor(null));
    }

    [Fact]
    public void ResumeAtFor_default_random_stays_within_the_jitter_window()
    {
        var reset = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var production = new QuotaResumeRunner(
            new ScriptedMailbox(), new RunnerRunStore(), new RunnerProbe(), new QuotaProbeSchedule(), new ProviderRateLimiter(), null, TimeProvider.System);

        for (var i = 0; i < 50; i++)
        {
            var at = production.ResumeAtFor(reset)!.Value;
            Assert.InRange(at - reset, TimeSpan.Zero, QuotaResumeRunner.MaxJitter);
        }
    }

    // ------------------------------------------------------------------ runner

    [Fact]
    public async Task Run_without_a_target_session_fails()
    {
        var h = new Harness();
        h.Note();
        var context = h.Context(sessionId: null);

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Empty(h.Mailbox.Messages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a note")]
    public async Task Run_without_a_readable_note_fails(string? stored)
    {
        var h = new Harness();
        await h.State.SetStateAsync(QuotaNote.RunPrefix + Occurrence, stored, CancellationToken.None);

        var outcome = await h.Runner.RunAsync(h.Context(), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Empty(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Run_waits_for_the_resume_time_then_sends_continue_and_completes()
    {
        var h = new Harness();
        h.Note(title: "My session");
        var context = h.Context(resumeAt: h.Time.GetUtcNow() + TimeSpan.FromSeconds(30));

        var task = h.Runner.RunAsync(context, CancellationToken.None);
        await h.Time.WaitForTimerAsync();
        Assert.Empty(h.Mailbox.Messages);
        Assert.False(task.IsCompleted);

        h.Time.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        Assert.Empty(h.Mailbox.Messages);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        var outcome = await task.Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        var message = Assert.Single(h.Mailbox.Messages);
        Assert.Equal("s1", message.SessionId);
        Assert.Equal(Forge.App.Prompts.AppPromptTexts.QuotaContinue, message.Text);
        Assert.Equal("run-1", message.RunId);
        Assert.Equal(AutomationStrings.DisplayName(BuiltInAutomations.QuotaResume, "x"), message.Origin);
        Assert.Equal(AutomationRunStatus.Running, context.Run.Status);
        Assert.Null(context.Run.ResumeAt);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Success, note.Level);
        Assert.Equal(AutomationStrings.QuotaResumedTitle.Resolve(), note.Title);
        Assert.Equal(AutomationStrings.QuotaResumedBody("My session").Resolve(), note.Body);
        Assert.Null(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task Run_with_a_past_resume_time_does_not_wait()
    {
        var h = new Harness();
        h.Note();
        var context = h.Context(resumeAt: h.Time.GetUtcNow() - TimeSpan.FromMinutes(1));

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Single(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Run_without_a_resume_time_probes_until_the_quota_is_back()
    {
        var h = new Harness();
        h.Note();
        var context = h.Context(resumeAt: null);

        var task = h.Runner.RunAsync(context, CancellationToken.None);
        await h.Time.WaitForTimerAsync();
        Assert.Equal(0, h.Probe.Calls);
        Assert.Empty(h.Mailbox.Messages);

        h.Time.Advance(TimeSpan.FromMinutes(6));
        var outcome = await task.Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal(1, h.Probe.Calls);
        Assert.Single(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Resumes_of_one_provider_are_staggered_by_ten_seconds()
    {
        var h = new Harness();
        h.Note(provider: "prov");
        h.RateLimits.ReserveAutomationSlot(QuotaProbeSchedule.KeyFor("prov"));
        var context = h.Context(resumeAt: h.Time.GetUtcNow());

        var task = h.Runner.RunAsync(context, CancellationToken.None);
        await h.Time.WaitForTimerAsync();
        Assert.Empty(h.Mailbox.Messages);

        h.Time.Advance(ProviderRateLimitPolicy.Default.AutomationGap);
        var outcome = await task.Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Single(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Another_providers_slot_does_not_delay_the_resume()
    {
        var h = new Harness();
        h.Note(provider: "prov");
        h.RateLimits.ReserveAutomationSlot(QuotaProbeSchedule.KeyFor("other"));

        var outcome = await h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task A_quota_failure_of_the_continue_turn_puts_the_run_back_to_waiting_and_it_resumes_later()
    {
        var h = new Harness(random: () => 0.5);
        h.Note(title: "Original title");
        var reset = h.Time.GetUtcNow() + TimeSpan.FromMinutes(5);
        h.Mailbox.Results.Clear();
        h.Mailbox.Results.Add(new MailboxResult(MailboxStatus.TurnFailed, "quota") { Quota = new ProviderQuotaExhausted("prov", "m2", reset, "limit again") });
        h.Mailbox.Results.Add(new MailboxResult(MailboxStatus.Completed));
        var context = h.Context(resumeAt: h.Time.GetUtcNow());

        var task = h.Runner.RunAsync(context, CancellationToken.None);
        await h.Time.WaitForTimerAsync();

        Assert.Equal(AutomationRunStatus.WaitingQuota, context.Run.Status);
        Assert.Equal(reset + TimeSpan.FromSeconds(30), context.Run.ResumeAt);
        Assert.Equal(AutomationStrings.QuotaAgain("limit again"), context.Run.Message);
        var stored = QuotaNote.Parse(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
        Assert.NotNull(stored);
        Assert.Equal("Original title", stored.Title);
        Assert.Equal("m2", stored.Model);
        Assert.Single(h.Mailbox.Messages);

        h.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
        var outcome = await task.Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal(2, h.Mailbox.Messages.Count);
        Assert.Equal(
            [AutomationRunStatus.Running, AutomationRunStatus.WaitingQuota, AutomationRunStatus.Running],
            context.Statuses);
        Assert.Null(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task A_quota_note_in_shared_state_does_not_make_a_failure_a_quota_failure()
    {
        var h = new Harness();
        h.Note();
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.TurnFailed, "other error");
        h.Mailbox.OnDeliver = _ => h.WriteLatest("s1", new QuotaNote("prov", "m", null, h.Time.GetUtcNow(), "t", "written by someone else"));

        var outcome = await h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal("other error", outcome.Message);
        Assert.Single(h.Mailbox.Messages);
    }

    [Fact]
    public async Task A_quota_failure_with_a_reset_time_already_passed_resumes_after_the_stagger_gap()
    {
        var h = new Harness(random: () => 0);
        h.Note();
        h.Mailbox.Results.Clear();
        h.Mailbox.Results.Add(new MailboxResult(MailboxStatus.TurnFailed, "q") { Quota = new ProviderQuotaExhausted("prov", "m", h.Time.GetUtcNow(), "again") });
        h.Mailbox.Results.Add(new MailboxResult(MailboxStatus.Completed));

        var task = h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None);
        await h.Time.WaitForTimerAsync(); // the stagger gap of the second send
        h.Time.Advance(ProviderRateLimitPolicy.Default.AutomationGap);

        Assert.Equal(AutomationRunStatus.Completed, (await task.Within()).Status);
        Assert.Equal(2, h.Mailbox.Messages.Count);
    }

    [Fact]
    public async Task Quota_failing_again_after_the_maximum_rounds_fails_the_run()
    {
        var h = new Harness(random: () => 0);
        h.Note();
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.TurnFailed, "quota") { Quota = new ProviderQuotaExhausted("prov", "m", h.Time.GetUtcNow(), "still empty") };
        var context = h.Context(resumeAt: h.Time.GetUtcNow());

        var task = h.Runner.RunAsync(context, CancellationToken.None);
        for (var round = 1; round < QuotaResumeRunner.MaxRounds; round++)
        {
            await h.Time.WaitForTimerAsync(); // each later round meets the stagger gap of the previous send
            h.Time.Advance(ProviderRateLimitPolicy.Default.AutomationGap);
        }

        var outcome = await task.Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.QuotaAgain("still empty"), outcome.Message);
        Assert.Equal(QuotaResumeRunner.MaxRounds, h.Mailbox.Messages.Count);
        Assert.Empty(h.Notes.Calls);
        Assert.Null(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task A_non_quota_failure_ends_the_run_as_failed_with_the_reason_and_no_notification()
    {
        var h = new Harness();
        h.Note();
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.TurnFailed, "model exploded");

        var outcome = await h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal("model exploded", outcome.Message);
        Assert.Empty(h.Notes.Calls);
        Assert.Null(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task A_failure_without_a_reason_fails_with_an_empty_message()
    {
        var h = new Harness();
        h.Note();
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.TurnFailed);

        var outcome = await h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(string.Empty, outcome.Message);
    }

    [Theory]
    [InlineData(MailboxStatus.Withdrawn)]
    [InlineData(MailboxStatus.Cancelled)]
    public async Task A_withdrawn_or_cancelled_delivery_cancels_the_run_with_the_reason(MailboxStatus status)
    {
        var h = new Harness();
        h.Note();
        h.Mailbox.Results[0] = new MailboxResult(status, "gone");

        var outcome = await h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Cancelled, outcome.Status);
        Assert.Equal("gone", outcome.Message);
        Assert.Empty(h.Notes.Calls);
    }

    [Fact]
    public async Task Cancelling_while_waiting_keeps_the_note_for_a_restart_and_sends_nothing()
    {
        var h = new Harness();
        h.Note();
        using var cts = new CancellationTokenSource();
        var context = h.Context(resumeAt: h.Time.GetUtcNow() + TimeSpan.FromHours(1));

        var task = h.Runner.RunAsync(context, cts.Token);
        await h.Time.WaitForTimerAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).Within();
        Assert.Empty(h.Mailbox.Messages);
        Assert.NotNull(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task A_user_cancel_outside_a_shutdown_removes_the_note()
    {
        var h = new Harness();
        h.Note();
        var runner = new QuotaResumeRunner(h.Mailbox, h.State, h.Probe, h.Schedule, h.RateLimits, h.Notes, h.Time, () => 0, shuttingDown: () => false);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow() + TimeSpan.FromHours(1)), cts.Token);
        await h.Time.WaitForTimerAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).Within();
        Assert.Empty(h.Mailbox.Messages);
        Assert.Null(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task A_cancel_during_shutdown_keeps_the_note()
    {
        var h = new Harness();
        h.Note();
        var runner = new QuotaResumeRunner(h.Mailbox, h.State, h.Probe, h.Schedule, h.RateLimits, h.Notes, h.Time, () => 0, shuttingDown: () => true);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow() + TimeSpan.FromHours(1)), cts.Token);
        await h.Time.WaitForTimerAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).Within();
        Assert.NotNull(h.State.Peek(QuotaNote.RunPrefix + Occurrence));
    }

    [Fact]
    public async Task Cancelling_during_the_stagger_gap_sends_nothing()
    {
        var h = new Harness();
        h.Note();
        h.RateLimits.ReserveAutomationSlot(QuotaProbeSchedule.KeyFor("prov"));
        using var cts = new CancellationTokenSource();

        var task = h.Runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), cts.Token);
        await h.Time.WaitForTimerAsync();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).Within();
        Assert.Empty(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Null_context_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new Harness().Runner.RunAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_notification_service_does_not_break_completion()
    {
        var h = new Harness();
        h.Note();
        var runner = new QuotaResumeRunner(h.Mailbox, h.State, h.Probe, h.Schedule, h.RateLimits, null, h.Time, () => 0);

        var outcome = await runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task Run_sends_the_project_override_of_the_continue_slot()
    {
        var root = TestTempFiles.NewDirectory("forge-quota-slot-");
        try
        {
            var prompts = Path.Combine(root, "ws", ".forge", "prompts");
            Directory.CreateDirectory(prompts);
            File.WriteAllText(Path.Combine(prompts, "automations.quota.continue.md"), "Carry on, please.");
            var sessions = new CiFakeSessions { Session = new SessionInfo("s1", Path.Combine(root, "ws"), "t", DateTimeOffset.UnixEpoch) };
            var h = new Harness();
            h.Note();
            var runner = new QuotaResumeRunner(
                h.Mailbox,
                h.State,
                h.Probe,
                h.Schedule,
                h.RateLimits,
                h.Notes,
                h.Time,
                () => 0,
                sessions: sessions,
                templates: new Forge.App.Prompts.PromptTemplateService(Path.Combine(root, "user")));

            var outcome = await runner.RunAsync(h.Context(resumeAt: h.Time.GetUtcNow()), CancellationToken.None).Within();

            Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
            Assert.Equal("Carry on, please.", Assert.Single(h.Mailbox.Messages).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Harness
    {
        public Harness(Func<double>? random = null)
        {
            Time = new CiFakeTime();
            Schedule = new QuotaProbeSchedule(() => 0.5, Time);
            // The limiter reads windows in the timestamp domain, so it has to share the fake clock the tests advance:
            // otherwise every wait it computes would be measured against the real stopwatch.
            RateLimits = new ProviderRateLimiter(new SharedClock(Time));
            Runner = new QuotaResumeRunner(Mailbox, State, Probe, Schedule, RateLimits, Notes, Time, random ?? (() => 0));
        }

        public CiFakeTime Time { get; }

        public ScriptedMailbox Mailbox { get; } = new();

        public RunnerRunStore State { get; } = new();

        public RunnerProbe Probe { get; } = new();

        public QuotaProbeSchedule Schedule { get; }

        public ProviderRateLimiter RateLimits { get; }

        public CiFakeNotes Notes { get; } = new();

        public QuotaResumeRunner Runner { get; }

        public void Note(string? provider = "prov", string title = "Session title") =>
            State.SetStateAsync(
                QuotaNote.RunPrefix + Occurrence,
                new QuotaNote(provider, "m", null, Time.GetUtcNow(), title, "msg").Serialize(),
                CancellationToken.None).GetAwaiter().GetResult();

        public Task WriteLatest(string sessionId, QuotaNote note) =>
            State.SetStateAsync(QuotaNote.LatestPrefix + sessionId, note.Serialize(), CancellationToken.None);

        public RunnerContext Context(string? sessionId = "s1", DateTimeOffset? resumeAt = null)
        {
            var run = RunnerContext.NewRun(BuiltInAutomations.QuotaResume, Time.GetUtcNow(), Occurrence) with
            {
                TargetSessionId = sessionId,
                ResumeAt = resumeAt,
                Status = resumeAt is null ? AutomationRunStatus.WaitingQuota : AutomationRunStatus.Running,
            };
            return new RunnerContext(run, new AutomationDefinition(BuiltInAutomations.QuotaResume, "x"));
        }
    }
}

/// <summary>
/// The limiter's view of the harness clock. The readings and timers are the <see cref="CiFakeTime"/> the tests advance by
/// hand, and the timestamp is that same reading: a reservation is a timestamp compare-exchange, so a clock that only moved
/// its UTC value would leave every slot measured against the real stopwatch.
/// </summary>
internal sealed class SharedClock(TimeProvider clock) : TimeProvider
{
    /// <summary>One timestamp unit is one tick, so a duration converts exactly.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => clock.GetUtcNow().UtcTicks;

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);
}
