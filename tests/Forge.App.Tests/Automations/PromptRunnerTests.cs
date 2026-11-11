using System.Globalization;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Forge.App.Notifications;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class PromptRunnerTests
{
    private static readonly ModelSettings Fallback = new("default-model");

    // ------------------------------------------------------------------ Remind

    [Fact]
    public async Task Remind_only_notifies_and_never_touches_sessions()
    {
        var h = new Harness();
        var definition = h.Definition(action: AutomationAction.Remind, prompt: "drink water");

        var outcome = await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Info, note.Level);
        Assert.Equal(AutomationStrings.RemindTitle("Nightly").Resolve(), note.Title);
        Assert.Equal("drink water", note.Body);
        Assert.Empty(h.Mailbox.Messages);
        Assert.Empty(h.Sessions.ScratchTitles);
        Assert.Empty(h.Sessions.Gets);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public async Task Remind_with_an_empty_prompt_still_completes()
    {
        var h = new Harness();

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition(action: AutomationAction.Remind, prompt: string.Empty)), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal(string.Empty, Assert.Single(h.Notes.Calls).Body);
    }

    [Fact]
    public async Task Remind_works_without_a_notification_service()
    {
        var h = new Harness();
        var runner = h.RunnerWith(notifications: null);

        var outcome = await runner.RunAsync(h.Context(h.Definition(action: AutomationAction.Remind, prompt: "x")), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task Null_context_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new Harness().Runner.RunAsync(null!, CancellationToken.None));
    }

    // ------------------------------------------------------------------ Scratch

    [Fact]
    public async Task Scratch_without_a_general_project_fails_with_the_no_project_text()
    {
        var h = new Harness();

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoProjectText, outcome.Message);
        Assert.Empty(h.Mailbox.Messages);
        Assert.Equal(0, h.Host.Refreshes);
    }

    [Fact]
    public async Task Scratch_delivers_the_prompt_through_the_mailbox_and_completes()
    {
        var h = new Harness();
        var live = new RunnerSession("s9", "Scratch session");
        h.Host.Openable["s9"] = live;
        h.Sessions.Scratch = Info("s9", "Scratch session");
        var context = h.Context(h.Definition(prompt: "do the thing"));

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        var message = Assert.Single(h.Mailbox.Messages);
        Assert.Equal("s9", message.SessionId);
        Assert.Equal("do the thing", message.Text);
        Assert.Equal("run-1", message.RunId);
        Assert.Equal("Nightly", message.Origin);
        Assert.Equal("s9", context.Run.SessionId);
        Assert.Equal(1, h.Host.Refreshes);
        Assert.Equal(1, h.Host.Opens);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Success, note.Level);
        Assert.Equal(AutomationStrings.RunCompletedTitle.Resolve(), note.Title);
        Assert.Equal(AutomationStrings.RunCompletedBody("Nightly", "Scratch session").Resolve(), note.Body);
    }

    [Fact]
    public async Task Scratch_title_is_the_name_plus_the_local_date_and_time()
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(7));

        await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        var expected = "Nightly " + h.Time.GetLocalNow().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expected, Assert.Single(h.Sessions.ScratchTitles));
    }

    [Fact]
    public async Task Scratch_run_uses_the_localized_display_name_for_built_in_ids()
    {
        var h = new Harness();
        h.Sessions.Scratch = Info("s9", "T");
        h.Host.Openable["s9"] = new RunnerSession("s9");
        var definition = new AutomationDefinition(BuiltInAutomations.Housekeeping, "ignored") { Prompt = "p" };

        await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(AutomationStrings.DisplayName(BuiltInAutomations.Housekeeping, "ignored"), h.Mailbox.Messages[0].Origin);
    }

    [Fact]
    public async Task The_read_only_agent_travels_with_the_message()
    {
        var h = new Harness();
        var live = new RunnerSession("s9");
        h.Host.Add(live);
        h.Sessions.Scratch = Info("s9", "T");
        var definition = h.Definition() with { ReadOnlyTools = true };

        await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        var agent = h.Mailbox.Messages[0].Agent;
        Assert.NotNull(agent);
        Assert.Equal(PromptRunner.ReadOnlyTools, agent.ToolNames);
        Assert.Equal(Fallback, agent.Model);
        Assert.Equal(0, h.Host.Opens);
    }

    [Fact]
    public async Task A_message_without_overrides_carries_no_agent()
    {
        var h = new Harness();
        var live = new RunnerSession("s9");
        h.Host.Add(live);
        h.Sessions.Scratch = Info("s9", "T");

        await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Null(h.Mailbox.Messages[0].Agent);
    }

    [Fact]
    public async Task A_session_that_cannot_be_opened_is_never_sent_the_prompt()
    {
        var h = new Harness();
        h.Host.OpensAnything = false;
        h.Sessions.Scratch = Info("s9", "T");

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Cancelled, outcome.Status);
        Assert.Equal(AutomationStrings.SessionUnavailableText, outcome.Message);
        Assert.Empty(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Withdrawn_delivery_cancels_the_run_and_warns()
    {
        var h = new Harness();
        h.Sessions.Scratch = Info("s9", "T");
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.Withdrawn, "taken over");

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Cancelled, outcome.Status);
        Assert.Equal("taken over", outcome.Message);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Warning, note.Level);
        Assert.Equal(AutomationStrings.RunWithdrawnBody("Nightly", "taken over").Resolve(), note.Body);
    }

    [Fact]
    public async Task Cancelled_delivery_cancels_the_run_silently()
    {
        var h = new Harness();
        h.Sessions.Scratch = Info("s9", "T");
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.Cancelled);

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Cancelled, outcome.Status);
        Assert.Equal(string.Empty, outcome.Message);
        Assert.Empty(h.Notes.Calls);
    }

    [Fact]
    public async Task Failed_turn_fails_the_run_and_raises_an_error_notification()
    {
        var h = new Harness();
        h.Sessions.Scratch = Info("s9", "T");
        h.Mailbox.Results[0] = new MailboxResult(MailboxStatus.TurnFailed, "model refused");

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition()), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal("model refused", outcome.Message);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Error, note.Level);
        Assert.Equal(AutomationStrings.RunFailedBody("Nightly", "model refused").Resolve(), note.Body);
    }

    [Fact]
    public async Task A_prompt_that_is_null_in_a_loaded_definition_is_sent_as_empty_text()
    {
        var h = new Harness();
        h.Sessions.Scratch = Info("s9", "T");
        var definition = h.Definition() with { Prompt = null! };

        await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(string.Empty, Assert.Single(h.Mailbox.Messages).Text);
    }

    // ------------------------------------------------------------------ FixedFork

    [Fact]
    public async Task Fork_without_a_project_fails_with_the_no_target_text()
    {
        var h = new Harness();

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition(placement: AutomationPlacement.FixedFork)), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Empty(h.Sessions.ForkProjects);
    }

    [Fact]
    public async Task Fork_that_is_not_a_repository_fails_with_the_no_target_text()
    {
        var h = new Harness();
        var definition = h.Definition(placement: AutomationPlacement.FixedFork) with { ProjectId = "p1" };

        var outcome = await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Equal("p1", Assert.Single(h.Sessions.ForkProjects));
    }

    [Fact]
    public async Task Fork_delivers_into_the_forks_session()
    {
        var h = new Harness();
        h.Sessions.Fork = Info("f1", "Fork session");
        var definition = h.Definition(placement: AutomationPlacement.FixedFork) with { ProjectId = "p1" };
        var context = h.Context(definition);

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal("f1", Assert.Single(h.Mailbox.Messages).SessionId);
        Assert.Equal("f1", context.Run.SessionId);
        Assert.Empty(h.Sessions.ScratchTitles);
    }

    // ------------------------------------------------------------------ Scout

    [Fact]
    public async Task Scout_without_any_target_fails()
    {
        var h = new Harness();

        var outcome = await h.Runner.RunAsync(h.Context(h.Definition(placement: AutomationPlacement.Scout)), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Empty(h.Sessions.Gets);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public async Task Scout_with_an_unknown_target_fails()
    {
        var h = new Harness();
        var definition = h.Definition(placement: AutomationPlacement.Scout) with { TargetSessionId = "ghost" };

        var outcome = await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Equal("ghost", Assert.Single(h.Sessions.Gets));
        Assert.Empty(h.Launcher.Requests);
    }

    [Theory]
    [InlineData(SessionStatus.Archived, null)]
    [InlineData(SessionStatus.Deleted, null)]
    [InlineData(SessionStatus.Active, "parent")]
    public async Task Scout_with_an_archived_deleted_or_child_target_fails(SessionStatus status, string? parent)
    {
        var h = new Harness();
        h.Sessions.Known["t"] = Info("t", "Target") with { Status = status, ParentSessionId = parent };
        var definition = h.Definition(placement: AutomationPlacement.Scout) with { TargetSessionId = "t" };

        var outcome = await h.Runner.RunAsync(h.Context(definition), CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal(AutomationStrings.NoTargetText, outcome.Message);
        Assert.Empty(h.Launcher.Requests);
    }

    [Fact]
    public async Task Scout_launches_a_scout_child_and_records_it_on_the_run()
    {
        var h = new Harness();
        h.Sessions.Known["t"] = Info("t", "Target");
        h.Launcher.Result = new ChildAgentResult("child-7", "report", false, new TokenUsage(11, 22));
        var definition = h.Definition(placement: AutomationPlacement.Scout, prompt: "look around") with { TargetSessionId = "t" };
        var context = h.Context(definition);

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Equal(new ChildAgentRequest("t", "Scout", "look around"), request);
        Assert.Equal("t", context.Run.TargetSessionId);
        Assert.Equal("child-7", context.Run.SessionId);
        Assert.Equal(new TokenUsage(11, 22), context.Run.Usage);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Success, note.Level);
        Assert.Equal(AutomationStrings.RunCompletedBody("Nightly", "Target").Resolve(), note.Body);
        Assert.Empty(h.Mailbox.Messages);
    }

    [Fact]
    public async Task Scout_falls_back_to_the_runs_target_session()
    {
        var h = new Harness();
        h.Sessions.Known["rt"] = Info("rt", "Run target");
        var context = h.Context(h.Definition(placement: AutomationPlacement.Scout), targetSessionId: "rt");

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Completed, outcome.Status);
        Assert.Equal("rt", Assert.Single(h.Launcher.Requests).ParentSessionId);
    }

    [Fact]
    public async Task Scout_prefers_the_definitions_target_over_the_runs()
    {
        var h = new Harness();
        h.Sessions.Known["def"] = Info("def", "Def");
        h.Sessions.Known["run"] = Info("run", "Run");
        var definition = h.Definition(placement: AutomationPlacement.Scout) with { TargetSessionId = "def" };

        await h.Runner.RunAsync(h.Context(definition, targetSessionId: "run"), CancellationToken.None).Within();

        Assert.Equal("def", Assert.Single(h.Launcher.Requests).ParentSessionId);
    }

    [Fact]
    public async Task Scout_error_fails_the_run_with_the_agents_text_and_an_error_notification()
    {
        var h = new Harness();
        h.Sessions.Known["t"] = Info("t", "Target");
        h.Launcher.Result = new ChildAgentResult("child-8", "it broke", true, new TokenUsage(1, 1));
        var definition = h.Definition(placement: AutomationPlacement.Scout) with { TargetSessionId = "t" };
        var context = h.Context(definition);

        var outcome = await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Equal(AutomationRunStatus.Failed, outcome.Status);
        Assert.Equal("it broke", outcome.Message);
        Assert.Equal("child-8", context.Run.SessionId);
        var note = Assert.Single(h.Notes.Calls);
        Assert.Equal(NotificationLevel.Error, note.Level);
        Assert.Equal(AutomationStrings.RunFailedBody("Nightly", "it broke").Resolve(), note.Body);
    }

    [Fact]
    public async Task Scout_without_a_child_session_id_leaves_the_run_session_empty()
    {
        var h = new Harness();
        h.Sessions.Known["t"] = Info("t", "Target");
        h.Launcher.Result = new ChildAgentResult(string.Empty, "x", true, new TokenUsage(0, 0));
        var definition = h.Definition(placement: AutomationPlacement.Scout) with { TargetSessionId = "t" };
        var context = h.Context(definition);

        await h.Runner.RunAsync(context, CancellationToken.None).Within();

        Assert.Null(context.Run.SessionId);
    }

    // ------------------------------------------------------------------ AgentFor

    [Fact]
    public void AgentFor_is_null_when_not_read_only_and_no_model()
    {
        Assert.Null(PromptRunner.AgentFor(new AutomationDefinition("a", "A"), Fallback));
    }

    [Fact]
    public void AgentFor_read_only_keeps_only_the_looking_tools_and_the_fallback_model()
    {
        var agent = PromptRunner.AgentFor(new AutomationDefinition("a", "A") { ReadOnlyTools = true }, Fallback);

        Assert.NotNull(agent);
        Assert.Equal("Root", agent.Name);
        Assert.Equal(Fallback, agent.Model);
        Assert.Equal(["read", "grep", "find", "ls"], agent.ToolNames);
        Assert.Same(PromptRunner.ReadOnlyTools, agent.ToolNames);
    }

    [Fact]
    public void AgentFor_model_only_uses_the_model_and_all_tools()
    {
        var own = new ModelSettings("custom", ProviderId: "prov");

        var agent = PromptRunner.AgentFor(new AutomationDefinition("a", "A") { Model = own }, Fallback);

        Assert.NotNull(agent);
        Assert.Equal(own, agent.Model);
        Assert.Null(agent.ToolNames);
    }

    [Fact]
    public void AgentFor_read_only_with_a_model_combines_both()
    {
        var own = new ModelSettings("custom");

        var agent = PromptRunner.AgentFor(new AutomationDefinition("a", "A") { Model = own, ReadOnlyTools = true }, Fallback);

        Assert.NotNull(agent);
        Assert.Equal(own, agent.Model);
        Assert.Equal(PromptRunner.ReadOnlyTools, agent.ToolNames);
    }

    [Fact]
    public void Read_only_tools_contain_no_writing_tool()
    {
        Assert.DoesNotContain("write", PromptRunner.ReadOnlyTools);
        Assert.DoesNotContain("edit", PromptRunner.ReadOnlyTools);
        Assert.DoesNotContain("bash", PromptRunner.ReadOnlyTools);
    }

    // ------------------------------------------------------------------ WaitingProgress

    [Fact]
    public async Task WaitingProgress_mirrors_waiting_onto_the_run_status()
    {
        var context = new RunnerContext(RunnerContext.NewRun("a", DateTimeOffset.UnixEpoch), new AutomationDefinition("a", "A"));
        var progress = new WaitingProgress(context);

        progress.Report(true);
        await Until(() => context.History.Count == 1);
        progress.Report(false);
        await Until(() => context.History.Count == 2);

        Assert.Equal([AutomationRunStatus.WaitingIdle, AutomationRunStatus.Running], context.Statuses);
    }

    [Fact]
    public void WaitingProgress_swallows_a_failing_status_update()
    {
        var context = new RunnerContext(RunnerContext.NewRun("a", DateTimeOffset.UnixEpoch), new AutomationDefinition("a", "A"))
        {
            ThrowOnUpdate = new InvalidOperationException("db gone"),
        };

        var exception = Record.Exception(() => new WaitingProgress(context).Report(true));

        Assert.Null(exception);
    }

    private static SessionInfo Info(string id, string title) => new(id, "/work/" + id, title, DateTimeOffset.UnixEpoch);

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class Harness
    {
        public Harness()
        {
            Runner = RunnerWith(Notes);
            Host.OpensAnything = true;
        }

        public CiFakeTime Time { get; } = new();

        public RunnerSessions Sessions { get; } = new();

        public ScriptedMailbox Mailbox { get; } = new();

        public RunnerLauncher Launcher { get; } = new();

        public RunnerHost Host { get; } = new();

        public CiFakeNotes Notes { get; } = new();

        public PromptRunner Runner { get; }

        public PromptRunner RunnerWith(CiFakeNotes? notifications) =>
            new(Sessions, Mailbox, Launcher, Host, new InlineUi(), notifications, () => Fallback, Time);

        public AutomationDefinition Definition(
            AutomationAction action = AutomationAction.Prompt,
            AutomationPlacement placement = AutomationPlacement.Scratch,
            string prompt = "prompt") =>
            new("user-1", "Nightly") { Action = action, Placement = placement, Prompt = prompt };

        public RunnerContext Context(AutomationDefinition definition, string? targetSessionId = null) =>
            new(
                RunnerContext.NewRun(definition.Id, Time.GetUtcNow()) with { TargetSessionId = targetSessionId },
                definition);
    }
}
