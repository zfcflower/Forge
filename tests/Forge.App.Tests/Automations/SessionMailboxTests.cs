using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Localization;
using Xunit;

namespace Forge.App.Tests.Automations;

public sealed class SessionMailboxTests
{
    private static readonly MailboxMessage Message = new("s1", "hello", "run-1", "My automation");

    private readonly CiFakeTime time = new();
    private readonly RunnerHost host = new();

    private SessionMailbox Mailbox() => new(host, new InlineUi(), time);

    [Fact]
    public async Task Idle_session_gets_the_message_at_once_without_waiting_reports()
    {
        var session = new RunnerSession("s1");
        host.Add(session);
        var progress = new RecordingProgress();

        var result = await Mailbox().DeliverAsync(Message, progress, CancellationToken.None).Within();

        Assert.Equal(MailboxStatus.Completed, result.Status);
        var turn = Assert.Single(session.Turns);
        Assert.Equal("hello", turn.Text);
        Assert.Equal(AutomationStrings.OriginNotice("My automation").Resolve(), turn.Origin);
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task Turn_failure_and_usage_are_passed_through()
    {
        var usage = new TokenUsage(5, 6);
        var session = new RunnerSession("s1") { TurnResult = new MailboxResult(MailboxStatus.TurnFailed, "boom", usage) };
        host.Add(session);

        var result = await Mailbox().DeliverAsync(Message, null, CancellationToken.None).Within();

        Assert.Equal(MailboxStatus.TurnFailed, result.Status);
        Assert.Equal("boom", result.Reason);
        Assert.Same(usage, result.Usage);
    }

    [Fact]
    public async Task Busy_session_is_waited_for_and_reports_true_then_false()
    {
        var session = new RunnerSession("s1") { IsAutomationIdle = false };
        host.Add(session);
        var progress = new RecordingProgress();

        var task = Mailbox().DeliverAsync(Message, progress, CancellationToken.None);
        await time.WaitForTimerAsync();
        Assert.False(task.IsCompleted);
        Assert.Empty(session.Turns);
        Assert.Equal([true], progress.Values);

        // Still busy after one poll: no second "waiting" report, still nothing delivered.
        time.Advance(TimeSpan.FromSeconds(1));
        await time.WaitForTimerAsync();
        Assert.Empty(session.Turns);
        Assert.Equal([true], progress.Values);

        session.IsAutomationIdle = true;
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await task.Within();

        Assert.Equal(MailboxStatus.Completed, result.Status);
        Assert.Single(session.Turns);
        Assert.Equal([true, false], progress.Values);
    }

    [Fact]
    public async Task Poll_interval_is_configurable()
    {
        var session = new RunnerSession("s1") { IsAutomationIdle = false };
        host.Add(session);
        var mailbox = new SessionMailbox(host, new InlineUi(), time, TimeSpan.FromSeconds(30));

        var task = mailbox.DeliverAsync(Message, null, CancellationToken.None);
        await time.WaitForTimerAsync();
        session.IsAutomationIdle = true;

        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Yield();
        Assert.False(task.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MailboxStatus.Completed, (await task.Within()).Status);
    }

    [Fact]
    public async Task Unknown_session_is_withdrawn_as_unavailable()
    {
        var progress = new RecordingProgress();

        var result = await Mailbox().DeliverAsync(Message, progress, CancellationToken.None).Within();

        Assert.Equal(MailboxStatus.Withdrawn, result.Status);
        Assert.Equal(AutomationStrings.SessionUnavailableText, result.Reason);
        Assert.Equal(1, host.Opens);
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task Closed_tab_session_is_opened_on_first_look_and_delivered()
    {
        var session = new RunnerSession("s1");
        host.Openable["s1"] = session;

        var result = await Mailbox().DeliverAsync(Message, null, CancellationToken.None).Within();

        Assert.Equal(MailboxStatus.Completed, result.Status);
        Assert.Equal(1, host.Opens);
        Assert.Single(session.Turns);
    }

    [Fact]
    public async Task Session_closed_by_the_user_is_withdrawn_even_when_idle()
    {
        var session = new RunnerSession("s1") { IsAutomationClosed = true };
        host.Add(session);

        var result = await Mailbox().DeliverAsync(Message, null, CancellationToken.None).Within();

        Assert.Equal(MailboxStatus.Withdrawn, result.Status);
        Assert.Equal(AutomationStrings.SessionUnavailableText, result.Reason);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task Session_taken_over_while_waiting_is_withdrawn_and_waiting_ends()
    {
        var session = new RunnerSession("s1") { IsAutomationIdle = false };
        host.Add(session);
        var progress = new RecordingProgress();

        var task = Mailbox().DeliverAsync(Message, progress, CancellationToken.None);
        await time.WaitForTimerAsync();
        session.IsAutomationClosed = true;
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await task.Within();

        Assert.Equal(MailboxStatus.Withdrawn, result.Status);
        Assert.Equal(AutomationStrings.SessionUnavailableText, result.Reason);
        Assert.Empty(session.Turns);
        Assert.Equal([true, false], progress.Values);
    }

    [Fact]
    public async Task Tab_closed_while_waiting_is_withdrawn_as_closed_and_is_not_reopened()
    {
        var session = new RunnerSession("s1") { IsAutomationIdle = false };
        host.Add(session);
        host.Openable["s1"] = session;

        var task = Mailbox().DeliverAsync(Message, null, CancellationToken.None);
        await time.WaitForTimerAsync();
        host.Remove("s1");
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await task.Within();

        Assert.Equal(MailboxStatus.Withdrawn, result.Status);
        Assert.Equal(AutomationStrings.SessionClosedText, result.Reason);
        Assert.Equal(0, host.Opens);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task Already_cancelled_token_delivers_nothing()
    {
        var session = new RunnerSession("s1");
        host.Add(session);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Mailbox().DeliverAsync(Message, null, cts.Token).Within();

        Assert.Equal(MailboxStatus.Cancelled, result.Status);
        Assert.Empty(session.Turns);
        Assert.Equal(0, host.Opens);
    }

    [Fact]
    public async Task Cancelling_while_waiting_ends_with_cancelled_and_never_delivers()
    {
        var session = new RunnerSession("s1") { IsAutomationIdle = false };
        host.Add(session);
        var progress = new RecordingProgress();
        using var cts = new CancellationTokenSource();

        var task = Mailbox().DeliverAsync(Message, progress, cts.Token);
        await time.WaitForTimerAsync();
        cts.Cancel();
        var result = await task.Within();

        Assert.Equal(MailboxStatus.Cancelled, result.Status);
        Assert.Empty(session.Turns);
        Assert.Equal([true], progress.Values);
    }

    [Fact]
    public async Task Cancelling_after_the_session_was_opened_but_before_the_turn_does_not_start_it()
    {
        var session = new RunnerSession("s1");
        host.Openable["s1"] = session;
        using var cts = new CancellationTokenSource();
        host.OnOpen = cts.Cancel;

        var result = await Mailbox().DeliverAsync(Message, null, cts.Token).Within();

        Assert.Equal(MailboxStatus.Cancelled, result.Status);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public async Task Null_message_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Mailbox().DeliverAsync(null!, null, CancellationToken.None));
    }

    [Fact]
    public void IsIdle_is_false_for_unknown_and_busy_sessions_and_true_for_idle_ones()
    {
        var idle = new RunnerSession("idle");
        var busy = new RunnerSession("busy") { IsAutomationIdle = false };
        host.Add(idle);
        host.Add(busy);
        var mailbox = Mailbox();

        Assert.True(mailbox.IsIdle("idle"));
        Assert.False(mailbox.IsIdle("busy"));
        Assert.False(mailbox.IsIdle("nope"));
        Assert.Equal(0, host.Opens);
    }

    [Fact]
    public async Task Child_launcher_starts_the_child_under_an_open_parent()
    {
        var session = new RunnerSession("s1") { ChildResult = new ChildAgentResult("c9", "ok", false, new TokenUsage(1, 2)) };
        host.Add(session);

        var result = await new SessionChildAgentLauncher(host, new InlineUi())
            .LaunchAsync(new ChildAgentRequest("s1", "Scout", "look"), CancellationToken.None).Within();

        Assert.Equal("c9", result.ChildSessionId);
        Assert.Equal(("Scout", "look"), Assert.Single(session.Children));
        Assert.Equal(0, host.Opens);
    }

    [Fact]
    public async Task Child_launcher_opens_a_closed_tab_parent()
    {
        var session = new RunnerSession("s1");
        host.Openable["s1"] = session;

        await new SessionChildAgentLauncher(host, new InlineUi())
            .LaunchAsync(new ChildAgentRequest("s1", "Scout", "look"), CancellationToken.None).Within();

        Assert.Equal(1, host.Opens);
        Assert.Single(session.Children);
    }

    [Fact]
    public async Task Child_launcher_refuses_a_missing_or_closed_parent()
    {
        var launcher = new SessionChildAgentLauncher(host, new InlineUi());
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.LaunchAsync(new ChildAgentRequest("nope", "Scout", "x"), CancellationToken.None));
        Assert.Equal(AutomationStrings.SessionUnavailableText, missing.Message);

        var closed = new RunnerSession("s2") { IsAutomationClosed = true };
        host.Add(closed);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => launcher.LaunchAsync(new ChildAgentRequest("s2", "Scout", "x"), CancellationToken.None));
        Assert.Empty(closed.Children);

        await Assert.ThrowsAsync<ArgumentNullException>(() => launcher.LaunchAsync(null!, CancellationToken.None));
    }
}
