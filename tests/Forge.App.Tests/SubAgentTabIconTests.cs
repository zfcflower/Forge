using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The bottom group's tab of a sub-agent wears the glyph of its level, the same one the project tree paints for that row:
/// the conversation-and-return bubble for an agent the session's own agent spawned, the lock for one that hangs under
/// another agent and cannot spawn again. A tab reads the level off the agent's parent link, where the main agent stands as
/// <c>"root"</c> — the link is never null for a live child, so a null check labelled every level the deepest one.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SubAgentTabIconTests
{
    [Fact]
    public Task A_tab_wears_the_glyph_of_its_agent_level() => SharedUi.RunAsync(() =>
    {
        // Spawned by the session's own agent: the first level, free to delegate.
        var firstLevel = TabHeaderViewFor(parentAgentId: "root");
        // Spawned by that agent: the last level, which cannot spawn another.
        var lastLevel = TabHeaderViewFor(parentAgentId: "agent-1");
        // A child replayed from the store without a parent row: still the session's own child.
        var replayed = TabHeaderViewFor(parentAgentId: null);

        Assert.Same(TreeIcons.ChatArrowBackDown, firstLevel.Icon);
        Assert.Same(TreeIcons.ChatArrowBackDown, replayed.Icon);
        Assert.Same(TreeIcons.ChatLock, lastLevel.Icon);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_glyph_says_running_or_history_by_its_colour() => SharedUi.RunAsync(() =>
    {
        var agent = new SubAgentViewModel(
            "session-1", "Worker-3", "child-session-1", "Worker", "Say hi.", "test-model", stopRequested: null, parentAgentId: "root");
        var header = new TabHeaderView { Model = new TabModel("tab-subagent", TabKind.SubAgent, agent, agent.TabTitle) };
        var window = new Window { Width = 320, Height = 120, Content = header };
        window.Show();
        SharedUi.Settle(window);
        // The header's own leading glyph; the close button's template has an icon of the same name inside itself.
        var icon = header.GetVisualDescendants()
            .OfType<FluentIcon>()
            .Single(glyph => ReferenceEquals(glyph.TemplatedParent, header));

        // Idle: the template's resting colour, the same one every inactive tab wears.
        Assert.Equal(TabIconTone.Default, header.IconTone);
        Assert.Same(Resource(window, "TextMutedBrush"), icon.Foreground);

        // A turn of its own: the tab wears the accent, live, without the header being rebuilt.
        agent.MarkRunning(DateTimeOffset.UtcNow);
        SharedUi.Settle(window);
        Assert.Equal(TabIconTone.Running, header.IconTone);
        Assert.Same(Resource(window, "TiffanyInkBrush"), icon.Foreground);

        // A replayed/history tab: muted again - the tab strip says "history" with a colour instead of a prefix glyph.
        agent.Finish(SubAgentStatus.Completed, TimeSpan.FromSeconds(3), null, DateTimeOffset.UtcNow);
        agent.IsReadOnly = true;
        SharedUi.Settle(window);
        Assert.Equal(TabIconTone.History, header.IconTone);
        Assert.Same(Resource(window, "TextMutedBrush"), icon.Foreground);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_live_sub_agent_tab_offers_no_close_until_its_run_ends() => SharedUi.RunAsync(() =>
    {
        var agent = new SubAgentViewModel(
            "session-1", "Worker-3", "child-session-1", "Worker", "Say hi.", "test-model", stopRequested: null, parentAgentId: "root");
        var header = new TabHeaderView { Model = new TabModel("tab-subagent", TabKind.SubAgent, agent, agent.TabTitle) };
        var window = new Window { Width = 320, Height = 120, Content = header };
        window.Show();
        SharedUi.Settle(window);
        var closeBox = header.GetVisualDescendants()
            .OfType<PanelIconButton>()
            .Single(button => button.Name == "PART_Close");

        // Queued for a slot, then running: the tab is the window onto a live run, so it offers no close at all -
        // a user-close would strand the run and, when it is the group's only tab, leave the composer's sub-agent
        // chip nothing to open.
        Assert.False(header.IsClosable);
        Assert.False(closeBox.IsVisible);
        agent.MarkRunning(DateTimeOffset.UtcNow);
        SharedUi.Settle(window);
        Assert.False(header.IsClosable);
        Assert.False(closeBox.IsVisible);

        // The run ends - stopped by the user included: the close box is back, and closing is the retire into history.
        agent.Finish(SubAgentStatus.Stopped, TimeSpan.FromSeconds(3), null, DateTimeOffset.UtcNow);
        SharedUi.Settle(window);
        Assert.True(header.IsClosable);
        Assert.True(closeBox.IsVisible);

        window.Close();
        return Task.CompletedTask;
    });

    /// <summary>The named resource of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static object Resource(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), "missing resource: " + key);
        Assert.NotNull(value);
        return value;
    }

    private static TabHeaderView TabHeaderViewFor(string? parentAgentId)
    {
        var agent = new SubAgentViewModel(
            sessionId: "session-1",
            agentId: "agent-" + (parentAgentId ?? "replayed"),
            childSessionId: "child-session-1",
            kind: "Worker",
            task: "Say hi.",
            model: "test-model",
            stopRequested: null,
            parentAgentId: parentAgentId);
        return new TabHeaderView
        {
            Model = new TabModel("tab-" + agent.AgentId, TabKind.SubAgent, agent, "Worker · Say hi."),
        };
    }
}
