using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Tools;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

/// <summary>
/// The script-run batching of <see cref="SessionApprovalHandler"/> (W14b): a <c>run_code</c> script's inner calls of one
/// tool serialize behind one card, the card says how many wait, and "allow the rest of this run" answers them and the
/// calls the script has not made yet - without writing a remembered rule.
/// </summary>
public sealed class ScriptRunApprovalTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string dir, ApprovalRules rules, ApprovalQueue queue)
        {
            Dir = dir;
            Rules = rules;
            Queue = queue;
        }

        public string Dir { get; }

        public ApprovalRules Rules { get; }

        public ApprovalQueue Queue { get; }

        public static Harness Create()
        {
            var dir = ApprovalFixtures.TempDir("forge-script-approval-");
            return new Harness(
                dir,
                new ApprovalRules(Path.Combine(dir, ApprovalRules.FileName), debounceMilliseconds: 0),
                new ApprovalQueue(InlineDispatcher.Instance));
        }

        public SessionApprovalHandler Handler() =>
            new(Queue, Rules, ApprovalFixtures.Session("s1"));

        public async ValueTask DisposeAsync()
        {
            await Rules.DisposeAsync();
            ApprovalFixtures.DeleteTree(Dir);
        }
    }

    /// <summary>Starts a request without awaiting its decision, so several can be in flight behind one card.</summary>
    private static Task<ApprovalDecision> Start(
        SessionApprovalHandler handler,
        string tool = "web_fetch",
        ToolSideEffect sideEffect = ToolSideEffect.ReadOnly,
        string? runKey = "run-1") =>
        handler.RequestAsync(ApprovalFixtures.ToolRequest(tool, sideEffect, runKey: runKey), CancellationToken.None)
            .AsTask();

    [Fact]
    public async Task A_lone_script_call_shows_one_card_with_the_run_action()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var pending = Start(handler);
        var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.True(card.HasRunScope);
        Assert.False(card.HasPendingCalls);
        Assert.True(card.AllowRunCommand.CanExecute(null));

        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task Calls_of_one_run_and_tool_wait_behind_one_card()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler);
        var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        // The second call joined the run's group synchronously up to its semaphore wait, so it parked without a card.
        var second = Start(handler);
        Assert.Equal(1, await harness.Queue.CountAsync());
        Assert.Equal(1, card.PendingCount);
        Assert.True(card.HasPendingCalls);

        // A plain allow answers only the call on the card; the waiting call then takes its turn and shows its own.
        // The watcher is subscribed before the decision, so the next card cannot appear before the subscription.
        var nextCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        var nextCard = await nextCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.False(nextCard.HasPendingCalls);
        nextCard.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await second.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task Allow_rest_of_run_answers_the_waiters_and_the_calls_to_come()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler);
        var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        // Started once the leader is parked on its decision: each joins synchronously, so the counts are exact.
        var second = Start(handler);
        var third = Start(handler);
        Assert.Equal(2, card.PendingCount);

        card.AllowRunCommand.Execute(null);

        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(ApprovalDecision.Allow, await second.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(ApprovalDecision.Allow, await third.WaitAsync(ApprovalFixtures.StepTimeout));

        // A call the script has not made yet answers without a card of its own.
        Assert.Equal(ApprovalDecision.Allow, await Start(handler).WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(0, await harness.Queue.CountAsync());

        // The grant is not a remembered rule: the tool still asks for every other run. A fresh run's card is created
        // synchronously up to its enqueue, so the watcher goes up before the call starts.
        var otherCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var other = Start(handler, runKey: "run-2");
        var otherCard = await otherCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
        otherCard.DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Deny, await other.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task A_deny_answers_only_this_call_and_the_next_waiter_takes_the_turn()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler);
        var second = Start(handler);
        var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        // Subscribed before the denial, so the waiter's card cannot slip past the subscription: the denial releases
        // the turn, the waiting call takes it and shows its own card.
        var nextCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        card.DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Deny, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        var nextCard = await nextCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
        nextCard.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await second.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task Two_runs_of_the_same_tool_ask_separately()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var firstCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler, runKey: "run-1");
        var firstCard = await firstCardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        var secondCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var second = Start(handler, runKey: "run-2");
        var secondCard = await secondCardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        // Different runs never share a turn or a grant: both cards are up at once.
        Assert.Equal(2, await harness.Queue.CountAsync());
        firstCard.AllowCommand.Execute(null);
        secondCard.AllowRunCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(ApprovalDecision.Allow, await second.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task Calls_without_a_run_key_never_group()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var firstCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler, runKey: null);
        var firstCard = await firstCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.False(firstCard.HasRunScope);
        Assert.False(firstCard.AllowRunCommand.CanExecute(null));

        // The agent loop's own calls stack independently, exactly as before W14b.
        var secondCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var second = Start(handler, runKey: null);
        var secondCard = await secondCardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        Assert.Equal(2, await harness.Queue.CountAsync());
        firstCard.AllowCommand.Execute(null);
        secondCard.DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(ApprovalDecision.Deny, await second.WaitAsync(ApprovalFixtures.StepTimeout));
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_group_and_the_count_falls()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var first = Start(handler);
        var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);

        // A waiter parked on the group's turn, not on a card: cancelling its own token releases it from the group
        // without ever showing a card, and the count the leader shows falls with it.
        using var cts = new CancellationTokenSource();
        var waiting = handler.RequestAsync(
            ApprovalFixtures.ToolRequest("web_fetch", ToolSideEffect.ReadOnly, runKey: "run-1"),
            cts.Token).AsTask();
        Assert.Equal(1, card.PendingCount);
        Assert.Equal(1, await harness.Queue.CountAsync());

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, card.PendingCount);
        Assert.Equal(1, await harness.Queue.CountAsync());

        card.AllowCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Allow, await first.WaitAsync(ApprovalFixtures.StepTimeout));
        Assert.Equal(0, await harness.Queue.CountAsync());
    }

    [Fact]
    public async Task Grant_state_drops_the_oldest_run_at_the_cap()
    {
        await using var harness = Harness.Create();
        using var handler = harness.Handler();

        // Fill the grant map past its cap; the inline dispatcher keeps every round synchronous.
        for (var run = 1; run <= 65; run++)
        {
            var key = "run-" + run;
            var cardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
            var pending = Start(handler, runKey: key);
            var card = await cardTask.WaitAsync(ApprovalFixtures.StepTimeout);
            card.AllowRunCommand.Execute(null);
            Assert.Equal(ApprovalDecision.Allow, await pending.WaitAsync(ApprovalFixtures.StepTimeout));
        }

        // The first run's grant was dropped when the sixty-fifth arrived: its calls ask again. A fresh group's card
        // is created synchronously up to its enqueue, so the watcher goes up before the call starts.
        var againCardTask = ApprovalFixtures.NextToolCardAsync(harness.Queue);
        var again = Start(handler, runKey: "run-1");
        var returned = await againCardTask.WaitAsync(ApprovalFixtures.StepTimeout);
        Assert.True(returned.HasRunScope);
        returned.DenyCommand.Execute(null);
        Assert.Equal(ApprovalDecision.Deny, await again.WaitAsync(ApprovalFixtures.StepTimeout));

        // The newest run's grant is still live.
        Assert.Equal(ApprovalDecision.Allow, await Start(handler, runKey: "run-65").WaitAsync(ApprovalFixtures.StepTimeout));
    }
}
