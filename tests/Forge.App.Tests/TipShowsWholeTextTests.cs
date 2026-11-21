using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A tooltip that carries a text the reader could not read elsewhere is a <see cref="TextBlock"/> that wraps inside the
/// tooltip's own width cap, never a plain string: Fluent's <c>ToolTip</c> theme caps its width (320) and a one-line text is
/// simply clipped there, so the sub-agent tab's task and the floating user message both showed only their beginning.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TipShowsWholeTextTests
{
    [Fact]
    public Task A_sub_agent_tab_tips_its_whole_task() => SharedUi.RunAsync(() =>
    {
        const string task = "Say \"hi\". Do not read, create or modify any files, and do not ask a follow-up question either.";
        var agent = new SubAgentViewModel(
            sessionId: "session-1",
            agentId: "agent-1",
            childSessionId: "child-session-1",
            kind: "Worker",
            task: task,
            model: "test-model",
            stopRequested: null,
            parentAgentId: "root");
        var header = new TabHeaderView { Model = new TabModel("subagent:agent-1", TabKind.SubAgent, agent, agent.TabTitle) };

        var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(header));
        Assert.Equal(task, tip.Text);
        Assert.Equal(TextWrapping.Wrap, tip.TextWrapping);
        Assert.True(tip.MaxWidth <= ToolTipText.MaxWidth);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_floating_user_message_tips_the_whole_message() => SharedUi.RunAsync(() =>
    {
        var list = new TranscriptListBox();
        var host = new Grid { Children = { list } };
        var window = new Window { Width = 800, Height = 200, Content = host };
        window.Show();
        SharedUi.Settle(window);

        Assert.NotNull(TranscriptFollower.Attach(list));
        var row = Assert.Single(host.Children.OfType<Border>());
        var rowText = Assert.IsType<TextBlock>(row.Child);

        var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(rowText));
        Assert.Equal(TextWrapping.Wrap, tip.TextWrapping);
        Assert.True(tip.MaxWidth <= ToolTipText.MaxWidth);

        window.Close();
        return Task.CompletedTask;
    });
}
