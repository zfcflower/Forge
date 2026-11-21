using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A tooltip that carries a text the reader could not read elsewhere shows all of it. Two rules make that true: the tip's
/// content wraps inside the tooltip's own width cap (Fluent's theme sets <c>MaxWidth</c> = 320), and it never hangs on the
/// trimmed text itself — Avalonia hands such a tip its owner's text layout, so a one-line owner forced <c>MaxLines=1</c>
/// and an ellipsis onto the tip. That second rule is what broke both the sub-agent tab's task and the floating user message.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TipShowsWholeTextTests
{
    private const string Message = "Now spawn one sub-agent of your own (kind: Worker) with a task telling it to say \"hi\" - it should not read, create or modify any files.";

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

        // The row's own tip: the owner is the row, not its one-line text, which is what keeps the tip off the owner's
        // `MaxLines = 1`.
        var rowTip = Assert.IsType<TextBlock>(ToolTip.GetTip(row));
        Assert.Equal(TextWrapping.Wrap, rowTip.TextWrapping);
        Assert.True(rowTip.MaxWidth <= ToolTipText.MaxWidth);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_trimmed_text_tips_the_whole_text_on_several_lines() => SharedUi.RunAsync(() =>
    {
        var text = new TextBlock
        {
            Text = Message,
            MaxWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        var window = new Window { Width = 400, Height = 160, Content = new StackPanel { Children = { text } } };
        window.Show();
        SharedUi.Settle(window);
        Assert.True(TrimmedTextToolTip.IsTrimmed(text));

        var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(text));
        Assert.Equal(Message, tip.Text);

        // Without the opening handler the service's copy of the owner's layout leaves one trimmed line here.
        ToolTip.SetIsOpen(text, true);
        Dispatcher.UIThread.RunJobs();
        SharedUi.Settle(window);
        Assert.Equal(0, tip.MaxLines);
        Assert.Equal(TextTrimming.None, tip.TextTrimming);
        Assert.True(tip.TextLayout.TextLines.Count > 1);
        Assert.True(tip.Bounds.Height > 30, $"the tip laid out as one line: {tip.Bounds}");

        ToolTip.SetIsOpen(text, false);
        window.Close();
        return Task.CompletedTask;
    });
}
