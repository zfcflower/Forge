using System.Globalization;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Notifications;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class SessionWatchTests
{
    // ------------------------------------------------------------------ WatchFinding

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 42)]
    [InlineData(2, 0)]
    public void Finding_round_trips(int kindValue, int minutes)
    {
        var finding = new WatchFinding((WatchKind)kindValue, "s1", minutes, "My title");

        Assert.Equal(finding, WatchFinding.Parse(finding.Serialize()));
    }

    [Fact]
    public void Finding_serialization_flattens_newlines_in_the_title()
    {
        var parsed = WatchFinding.Parse(new WatchFinding(WatchKind.Stalled, "s1", 1, "a\nb").Serialize());

        Assert.NotNull(parsed);
        Assert.Equal("a b", parsed.Title);
    }

    [Fact]
    public void Finding_with_an_empty_title_round_trips()
    {
        var finding = new WatchFinding(WatchKind.Approval, "s1", 10, string.Empty);

        Assert.Equal(finding, WatchFinding.Parse(finding.Serialize()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stalled\ns1\n5")]
    [InlineData("Unknown\ns1\n5\ntitle")]
    [InlineData("Stalled\ns1\nfive\ntitle")]
    [InlineData("Stalled\ns1\n5.5\ntitle")]
    public void Finding_parse_rejects_bad_input(string? detail)
    {
        Assert.Null(WatchFinding.Parse(detail));
    }

    // ------------------------------------------------------------------ Check

    [Fact]
    public void A_running_tool_call_is_not_a_stall_at_fifteen_minutes()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, IsToolRunning = true, LastActivityAt = h.Now - TimeSpan.FromMinutes(30) });

        Assert.Empty(h.Watch.Check());
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void A_tool_call_silent_for_an_hour_is_reported_as_a_long_command()
    {
        var h = new Harness();
        var last = h.Now - SessionWatch.ToolStallAfter;
        h.Host.Add(new RunnerSession("s1", "Build") { IsRunning = true, IsToolRunning = true, LastActivityAt = last });

        var finding = Assert.Single(h.Watch.Check());

        Assert.Equal(new WatchFinding(WatchKind.ToolRunning, "s1", 60, "Build"), finding);
        Assert.Equal("watch:tool:s1:" + last.UtcTicks.ToString(CultureInfo.InvariantCulture), Assert.Single(h.Engine.Started).Occurrence);
    }

    [Fact]
    public void Check_reports_a_session_without_progress_for_fifteen_minutes()
    {
        var h = new Harness();
        var last = h.Now - SessionWatch.StallAfter;
        h.Host.Add(new RunnerSession("s1", "Stuck") { IsRunning = true, LastActivityAt = last });

        var found = h.Watch.Check();

        var finding = Assert.Single(found);
        Assert.Equal(new WatchFinding(WatchKind.Stalled, "s1", 15, "Stuck"), finding);
        var request = Assert.Single(h.Engine.Started);
        Assert.Equal(BuiltInAutomations.SessionWatch, request.AutomationId);
        Assert.Equal(AutomationTriggerSource.Event, request.Source);
        Assert.Equal("s1", request.TargetSessionId);
        Assert.Equal(finding, WatchFinding.Parse(request.Detail));
        Assert.Equal("watch:stall:s1:" + last.UtcTicks.ToString(CultureInfo.InvariantCulture), request.Occurrence);
    }

    [Fact]
    public void Check_ignores_a_session_just_below_the_stall_threshold()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, LastActivityAt = h.Now - SessionWatch.StallAfter + TimeSpan.FromSeconds(1) });

        Assert.Empty(h.Watch.Check());
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Check_reports_the_whole_minutes_of_a_long_stall()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromMinutes(95.9) });

        Assert.Equal(95, Assert.Single(h.Watch.Check()).Minutes);
    }

    [Fact]
    public void Check_skips_sessions_that_are_not_running_or_are_closed()
    {
        var h = new Harness();
        var old = h.Now - TimeSpan.FromHours(3);
        h.Host.Add(new RunnerSession("idle") { IsRunning = false, LastActivityAt = old });
        h.Host.Add(new RunnerSession("closed") { IsRunning = true, IsAutomationClosed = true, LastActivityAt = old });

        Assert.Empty(h.Watch.Check());
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Check_does_nothing_while_disabled()
    {
        var h = new Harness { Enabled = false };
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromHours(1) });
        h.Approvals.Add(("s1", h.Now - TimeSpan.FromHours(1)));

        Assert.Empty(h.Watch.Check());
        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Check_reports_an_approval_that_waited_ten_minutes_with_the_sessions_title()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("s1", "Needs you"));
        var created = h.Now - SessionWatch.ApprovalAfter;
        h.Approvals.Add(("s1", created));

        var finding = Assert.Single(h.Watch.Check());

        Assert.Equal(new WatchFinding(WatchKind.Approval, "s1", 10, "Needs you"), finding);
        var request = Assert.Single(h.Engine.Started);
        Assert.Equal("watch:approval:s1:" + created.UtcTicks.ToString(CultureInfo.InvariantCulture), request.Occurrence);
        Assert.Equal("s1", request.TargetSessionId);
    }

    [Fact]
    public void Check_ignores_a_young_approval()
    {
        var h = new Harness();
        h.Approvals.Add(("s1", h.Now - SessionWatch.ApprovalAfter + TimeSpan.FromSeconds(1)));

        Assert.Empty(h.Watch.Check());
    }

    [Fact]
    public void Check_reports_an_old_approval_of_an_unknown_session_with_an_empty_title()
    {
        var h = new Harness();
        h.Approvals.Add(("ghost", h.Now - TimeSpan.FromMinutes(30)));

        var finding = Assert.Single(h.Watch.Check());

        Assert.Equal(string.Empty, finding.Title);
        Assert.Equal(30, finding.Minutes);
    }

    [Fact]
    public void Check_reports_stalls_and_approvals_together()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("a", "A") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromMinutes(20) });
        h.Host.Add(new RunnerSession("b", "B") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromMinutes(1) });
        h.Approvals.Add(("b", h.Now - TimeSpan.FromMinutes(11)));

        var found = h.Watch.Check();

        Assert.Equal([WatchKind.Stalled, WatchKind.Approval], found.Select(static f => f.Kind));
        Assert.Equal(2, h.Engine.Started.Count);
    }

    [Fact]
    public void Check_gives_the_same_occurrence_for_the_same_stall_so_it_is_not_reported_twice()
    {
        var h = new Harness();
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromMinutes(20) });

        h.Watch.Check();
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Watch.Check();

        Assert.Equal(2, h.Engine.Started.Count);
        Assert.Equal(h.Engine.Started[0].Occurrence, h.Engine.Started[1].Occurrence);
    }

    [Fact]
    public void Check_survives_an_engine_that_refuses_to_start()
    {
        var h = new Harness();
        h.Engine.Throw = new InvalidOperationException("engine down");
        h.Host.Add(new RunnerSession("s1") { IsRunning = true, LastActivityAt = h.Now - TimeSpan.FromHours(1) });

        var found = Record.Exception(() => h.Watch.Check());

        Assert.Null(found);
    }

    // ------------------------------------------------------------------ OnTurnCompleted

    [Fact]
    public void Finished_turn_in_a_background_window_starts_a_finished_run()
    {
        var h = new Harness();
        h.Host.IsWindowActive = false;
        var session = new RunnerSession("s1", "Done one");

        h.Watch.OnTurnCompleted(session, automationTurn: false);

        var request = Assert.Single(h.Engine.Started);
        Assert.Equal(BuiltInAutomations.SessionWatch, request.AutomationId);
        Assert.Equal(AutomationTriggerSource.Event, request.Source);
        Assert.Equal("s1", request.TargetSessionId);
        Assert.Equal(new WatchFinding(WatchKind.Finished, "s1", 0, "Done one"), WatchFinding.Parse(request.Detail));
        Assert.Equal("watch:done:s1:" + h.Now.UtcTicks.ToString(CultureInfo.InvariantCulture), request.Occurrence);
    }

    [Fact]
    public void Finished_turn_is_ignored_while_the_window_is_active()
    {
        var h = new Harness();
        h.Host.IsWindowActive = true;

        h.Watch.OnTurnCompleted(new RunnerSession("s1"), automationTurn: false);

        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Finished_turn_of_an_automation_is_ignored()
    {
        var h = new Harness();

        h.Watch.OnTurnCompleted(new RunnerSession("s1"), automationTurn: true);

        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Finished_turn_is_ignored_while_disabled()
    {
        var h = new Harness { Enabled = false };

        h.Watch.OnTurnCompleted(new RunnerSession("s1"), automationTurn: false);

        Assert.Empty(h.Engine.Started);
    }

    [Fact]
    public void Two_finished_turns_at_different_times_are_two_occurrences()
    {
        var h = new Harness();
        var session = new RunnerSession("s1");

        h.Watch.OnTurnCompleted(session, false);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Watch.OnTurnCompleted(session, false);

        Assert.NotEqual(h.Engine.Started[0].Occurrence, h.Engine.Started[1].Occurrence);
    }

    [Fact]
    public void Finished_turn_survives_an_engine_failure()
    {
        var h = new Harness();
        h.Engine.Throw = new InvalidOperationException("down");

        var exception = Record.Exception(() => h.Watch.OnTurnCompleted(new RunnerSession("s1"), false));

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------ SessionWatchRunner

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Runner_notifies_with_the_finding_and_completes(int kindValue)
    {
        var kind = (WatchKind)kindValue;
        var level = kind == WatchKind.Finished ? NotificationLevel.Success : NotificationLevel.Warning;
        var notes = new CiFakeNotes();
        var finding = new WatchFinding(kind, "s1", 17, "Some title");

        var outcome = await new SessionWatchRunner(notes).RunAsync(Context(finding.Serialize()), CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        var note = Assert.Single(notes.Calls);
        Assert.Equal(level, note.Level);
        var (title, body) = kind switch
        {
            WatchKind.Stalled => (AutomationStrings.WatchStalledTitle, AutomationStrings.WatchStalledBody("Some title", "17")),
            WatchKind.Approval => (AutomationStrings.WatchApprovalTitle, AutomationStrings.WatchApprovalBody("Some title", "17")),
            _ => (AutomationStrings.WatchFinishedTitle, AutomationStrings.WatchFinishedBody("Some title")),
        };
        Assert.Equal(title.Resolve(), note.Title);
        Assert.Equal(body.Resolve(), note.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task Runner_skips_a_run_without_a_readable_finding(string? detail)
    {
        var notes = new CiFakeNotes();

        var outcome = await new SessionWatchRunner(notes).RunAsync(Context(detail), CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Skipped, outcome.Status);
        Assert.Empty(notes.Calls);
    }

    [Fact]
    public async Task Runner_completes_without_a_notification_service()
    {
        var finding = new WatchFinding(WatchKind.Stalled, "s1", 20, "T");

        var outcome = await new SessionWatchRunner(null).RunAsync(Context(finding.Serialize()), CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task Runner_rejects_a_null_context()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new SessionWatchRunner(null).RunAsync(null!, CancellationToken.None));
    }

    private static RunnerContext Context(string? detail) =>
        new(
            RunnerContext.NewRun(BuiltInAutomations.SessionWatch, DateTimeOffset.UnixEpoch) with { Detail = detail },
            new AutomationDefinition(BuiltInAutomations.SessionWatch, "watch"));

    private sealed class Harness
    {
        public Harness()
        {
            Time.Advance(TimeSpan.FromDays(1));
            Watch = new SessionWatch(Engine, Host, () => [.. Approvals], () => Enabled, Time);
        }

        public CiFakeTime Time { get; } = new();

        public RunnerEngine Engine { get; } = new();

        public RunnerHost Host { get; } = new();

        public List<(string SessionId, DateTimeOffset CreatedAt)> Approvals { get; } = [];

        public bool Enabled { get; set; } = true;

        public SessionWatch Watch { get; }

        public DateTimeOffset Now => Time.GetUtcNow();
    }
}
