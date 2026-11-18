using Forge.App.Agents;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

public sealed class ResourceInspectorViewModelTests
{
    private sealed class FakeSource : IResourceInspectorSource
    {
        public List<SubAgentViewModel> Agents { get; } = [];

        public ResourceUsage Usage { get; set; } = ResourceUsage.Empty;

        public bool PromptsStale { get; set; }

        public PromptReloadState PromptReload { get; set; } = PromptReloadState.Ready;

        public SubAgentViewModel? Opened { get; private set; }

        public event EventHandler? RunningResourcesChanged;

        public void CollectAutomationAgents(List<SubAgentViewModel> into) => into.AddRange(Agents);

        public ResourceUsage DescribeUsage() => Usage;

        public void OpenAgent(SubAgentViewModel agent) => Opened = agent;

        public void Raise() => RunningResourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SubAgentViewModel Agent(string id) =>
        new("s", id, "c-" + id, "explore", "look around", string.Empty, null) { IsAutomation = true };

    private static ResourceInspectorViewModel Window(FakeSource source, Action? close = null, Action? reload = null) =>
        new(source, mcp: null, close, reload, post: action => action());

    [Fact]
    public void Refresh_keeps_the_rows_that_stay_and_drops_the_ones_that_left()
    {
        var first = Agent("a1");
        var second = Agent("a2");
        var source = new FakeSource { Agents = { first, second } };
        using var window = Window(source);

        window.Start();
        Assert.Equal([first, second], window.Agents);

        source.Agents.Remove(first);
        source.Raise();

        Assert.Same(second, Assert.Single(window.Agents));
        Assert.True(window.HasAgents);

        source.Agents.Clear();
        source.Raise();
        Assert.False(window.HasAgents);
    }

    [Fact]
    public void Reload_is_refused_with_a_reason_while_a_turn_or_an_automation_agent_runs()
    {
        var reloads = 0;
        var source = new FakeSource { PromptsStale = true };
        using var window = Window(source, reload: () => reloads++);
        window.Start();

        Assert.True(window.PromptsAreStale);
        Assert.True(window.ReloadCommand.CanExecute(null));
        Assert.Null(window.ReloadBlockedReason);

        source.PromptReload = PromptReloadState.TurnRunning;
        source.Raise();
        Assert.False(window.ReloadCommand.CanExecute(null));
        var turnReason = window.ReloadBlockedReason;
        Assert.False(string.IsNullOrEmpty(turnReason));
        window.ReloadCommand.Execute(null);

        source.PromptReload = PromptReloadState.AutomationRunning;
        source.Raise();
        Assert.False(window.ReloadCommand.CanExecute(null));
        Assert.NotEqual(turnReason, window.ReloadBlockedReason);
        window.ReloadCommand.Execute(null);
        Assert.Equal(0, reloads);

        source.PromptReload = PromptReloadState.Ready;
        source.Raise();
        window.ReloadCommand.Execute(null);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public void Usage_tables_end_with_a_total_and_stay_empty_before_the_first_turn()
    {
        var source = new FakeSource();
        using var window = Window(source);
        window.Start();
        Assert.False(window.HasTools);
        Assert.False(window.HasPrompts);

        source.Usage = new ResourceUsage(
            [new ToolUsageRow("files", 2, new UsageSize(80, 20)), new ToolUsageRow("web", 1, new UsageSize(40, 10))],
            [],
            [
                new PromptUsageRow(PromptUsageKind.System, new UsageSize(100, 25)),
                new PromptUsageRow(PromptUsageKind.Skills, new UsageSize(20, 5)),
            ]);
        source.Raise();

        Assert.Equal(3, window.Tools.Count);
        var toolsTotal = window.Tools[^1];
        Assert.True(toolsTotal.IsTotal);
        Assert.Equal(3, toolsTotal.Tools);
        Assert.Equal("120", toolsTotal.CharsText);
        Assert.Equal("≈ 30", toolsTotal.TokensText);

        Assert.Equal(3, window.Prompts.Count);
        Assert.True(window.Prompts[^1].IsTotal);
        Assert.Equal("120", window.Prompts[^1].CharsText);
        Assert.Equal("≈ 30", window.Prompts[^1].TokensText);
    }

    [Fact]
    public void Opening_an_agent_goes_through_the_session_and_closes_the_window()
    {
        var closed = 0;
        var agent = Agent("a1");
        var source = new FakeSource { Agents = { agent } };
        using var window = Window(source, close: () => closed++);
        window.Start();

        window.OpenAgent(agent);

        Assert.Same(agent, source.Opened);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void A_disposed_window_no_longer_listens_to_the_session()
    {
        var posts = 0;
        var source = new FakeSource();
        var window = new ResourceInspectorViewModel(source, mcp: null, post: action => { posts++; action(); });
        window.Start();

        window.Dispose();
        source.Raise();

        Assert.Equal(0, posts);
    }
}
