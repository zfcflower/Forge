using Forge.App.Approval;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ApprovalOverlayViewModelTests : IDisposable
{
    private readonly ApprovalQueue queue = new(InlineDispatcher.Instance);
    private readonly ApprovalQueueViewModel viewModel;
    private readonly List<ApprovalCardViewModel> owned = [];

    public ApprovalOverlayViewModelTests() => viewModel = new ApprovalQueueViewModel(queue);

    public void Dispose()
    {
        viewModel.Dispose();
        foreach (var card in owned)
        {
            card.Dispose();
        }
    }

    private ToolApprovalCardViewModel ToolCard(string sessionId = "s1")
    {
        var card = new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest(sessionId, "Session", "p1", "shell_run", Forge.Abstractions.ToolSideEffect.Execute, "/tmp", "{}"),
            InlineDispatcher.Instance);
        owned.Add(card);
        return card;
    }

    [Fact]
    public void Initial_state_is_empty_expanded_and_zero()
    {
        Assert.False(viewModel.HasItems);
        Assert.Equal(0, viewModel.Count);
        Assert.Equal("0", viewModel.CountText);
        Assert.True(viewModel.IsExpanded);
    }

    [Fact]
    public async Task Enqueue_and_withdraw_update_count_and_visibility()
    {
        var card = ToolCard();
        Assert.True(await queue.EnqueueAsync(card, CancellationToken.None));

        Assert.True(viewModel.HasItems);
        Assert.Equal(1, viewModel.Count);
        Assert.Equal("1", viewModel.CountText);

        await queue.WithdrawAsync(card);

        Assert.False(viewModel.HasItems);
        Assert.Equal(0, viewModel.Count);
    }

    [Fact]
    public async Task New_card_expands_a_collapsed_panel()
    {
        viewModel.ToggleCommand.Execute(null);
        Assert.False(viewModel.IsExpanded);

        Assert.True(await queue.EnqueueAsync(ToolCard(), CancellationToken.None));

        Assert.True(viewModel.IsExpanded);
    }

    [Fact]
    public void Toggle_flips_and_expand_opens()
    {
        viewModel.ToggleCommand.Execute(null);
        Assert.False(viewModel.IsExpanded);

        viewModel.Expand();
        Assert.True(viewModel.IsExpanded);

        viewModel.ToggleCommand.Execute(null);
        Assert.False(viewModel.IsExpanded);
    }

    [Fact]
    public async Task Dispose_detaches_so_late_queue_events_raise_nothing()
    {
        // HasItems and Count read the live queue, so detachment is observed through the notifications.
        var raised = PropertyChangeLog.For(viewModel);
        Assert.True(await queue.EnqueueAsync(ToolCard(), CancellationToken.None));
        Assert.Contains("HasItems", raised);
        Assert.Contains("Count", raised);

        raised.Clear();
        viewModel.Dispose();
        Assert.True(await queue.EnqueueAsync(ToolCard(), CancellationToken.None));

        Assert.Empty(raised);
        Assert.Equal(2, viewModel.Count);
        viewModel.Dispose();
    }
}
