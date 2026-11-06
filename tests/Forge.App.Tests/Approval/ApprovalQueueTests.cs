using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ApprovalQueueTests : IDisposable
{
    private readonly ApprovalQueue queue = new(InlineDispatcher.Instance);
    private readonly List<ApprovalCardViewModel> owned = [];

    public void Dispose()
    {
        foreach (var card in owned)
        {
            card.Dispose();
        }
    }

    private ToolApprovalCardViewModel ToolCard(string sessionId = "s1") =>
        Track(new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest(sessionId, "Session " + sessionId, "p1", "shell_run", ToolSideEffect.Execute, "/tmp", "{}"),
            InlineDispatcher.Instance));

    private ExtensionApprovalCardViewModel ExtensionCard(string pluginId = "plug") =>
        Track(new ExtensionApprovalCardViewModel(
            ApprovalFixtures.Staged(pluginId),
            "s1",
            "Session s1",
            fromPreviousRun: false,
            InlineDispatcher.Instance));

    private T Track<T>(T card)
        where T : ApprovalCardViewModel
    {
        owned.Add(card);
        return card;
    }

    [Fact]
    public async Task Enqueue_from_a_pool_thread_is_immediately_visible()
    {
        var card = ToolCard();
        var added = await Task.Run(() => queue.EnqueueAsync(card, CancellationToken.None));

        Assert.True(added);
        Assert.Equal(1, await queue.CountAsync());
        Assert.True(await queue.ContainsAsync(card.Id));
        Assert.Same(card, await queue.FindAsync(card.Id));
    }

    [Fact]
    public async Task Duplicate_id_is_rejected_and_keeps_the_first_card()
    {
        var first = ExtensionCard("plug");
        var second = ExtensionCard("plug");

        Assert.True(await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.False(await queue.EnqueueAsync(second, CancellationToken.None));
        Assert.Equal(1, await queue.CountAsync());
        Assert.Same(first, await queue.FindAsync(first.Id));
    }

    [Fact]
    public async Task Withdraw_completes_as_deny_and_a_late_click_changes_nothing()
    {
        var card = ToolCard();
        Assert.True(await queue.EnqueueAsync(card, CancellationToken.None));

        await queue.WithdrawAsync(card);

        var outcome = await card.Decision;
        Assert.Equal(ApprovalCardDecision.Deny, outcome.Decision);
        Assert.False(card.TryComplete(ApprovalCardOutcome.Allow));
        Assert.Equal(ApprovalCardDecision.Deny, (await card.Decision).Decision);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Withdraw_with_superseded_is_never_overwritten()
    {
        var card = ExtensionCard("plug");
        Assert.True(await queue.EnqueueAsync(card, CancellationToken.None));

        await queue.WithdrawAsync(card, new ApprovalCardOutcome(ApprovalCardDecision.Superseded));

        var outcome = await card.Decision;
        Assert.True(outcome.IsSuperseded);
        Assert.False(card.TryComplete(ApprovalCardOutcome.Allow));
        Assert.True((await card.Decision).IsSuperseded);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Withdraw_session_removes_only_that_session()
    {
        var first = ToolCard("s1");
        var second = ToolCard("s1");
        var other = ToolCard("s2");
        Assert.True(await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.True(await queue.EnqueueAsync(second, CancellationToken.None));
        Assert.True(await queue.EnqueueAsync(other, CancellationToken.None));

        Assert.Equal(2, await queue.WithdrawSessionAsync("s1"));

        Assert.Equal(ApprovalCardDecision.Deny, (await first.Decision).Decision);
        Assert.Equal(ApprovalCardDecision.Deny, (await second.Decision).Decision);
        Assert.False(other.IsDecided);
        Assert.Equal(1, await queue.CountAsync());
    }

    [Fact]
    public async Task Withdraw_all_denies_every_waiter()
    {
        var first = ToolCard("s1");
        var second = ToolCard("s2");
        Assert.True(await queue.EnqueueAsync(first, CancellationToken.None));
        Assert.True(await queue.EnqueueAsync(second, CancellationToken.None));

        await queue.WithdrawAllAsync();

        Assert.Equal(ApprovalCardDecision.Deny, (await first.Decision).Decision);
        Assert.Equal(ApprovalCardDecision.Deny, (await second.Decision).Decision);
        Assert.Equal(0, await queue.CountAsync());
    }

    [Fact]
    public async Task Concurrent_enqueues_from_many_sessions_all_land()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task<bool>>();
        for (var index = 0; index < 24; index++)
        {
            var card = Track(new ToolApprovalCardViewModel(
                new ToolApprovalCardRequest(
                    "s" + index, "Session", null, "tool_" + index, ToolSideEffect.Execute, "/tmp", "{}"),
                InlineDispatcher.Instance));
            tasks.Add(Task.Run(
                async () =>
                {
                    await gate.Task;
                    return await queue.EnqueueAsync(card, CancellationToken.None);
                }));
        }

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, static added => Assert.True(added));
        Assert.Equal(24, await queue.CountAsync());
    }
}
