using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A tool call's status is the transcript's only live report on it: a turning glyph while the call has not returned, then the
/// same ✓/✗ it has always worn. Both halves are load-bearing. The marks are derived properties, and before the call started
/// raising them the card's status slot went blank the moment a result landed and only filled in when its container was
/// recycled and bound again. These assert the visuals at the moment of the result, which is what a reader watches.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ToolStatusTests
{
    [Fact]
    public Task The_card_s_busy_glyph_becomes_the_check_the_moment_the_call_returns() => SharedUi.RunAsync(() =>
    {
        var group = new TurnToolGroupViewModel();
        var call = new ToolRowViewModel("bash", """{"command":"dotnet build src/Forge.App"}""");
        group.Add(call);

        var window = Show(group);
        var busy = Assert.Single(window.GetVisualDescendants().OfType<WaitingGlyph>());
        var check = CardMark(window, "ok");
        var cross = CardMark(window, "fail");
        Assert.True(busy.IsVisible, "a call that has not returned shows the turning glyph");
        Assert.False(check.IsVisible);
        Assert.False(cross.IsVisible);

        call.Complete(new ToolResult("Build succeeded."));
        SharedUi.Settle(window);

        Assert.False(busy.IsVisible, "the glyph stops when the call returns");
        Assert.True(check.IsVisible, "and the check takes its place without waiting for a rebind");
        Assert.False(cross.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_failed_card_wears_the_cross_instead_of_the_check() => SharedUi.RunAsync(() =>
    {
        var group = new TurnToolGroupViewModel();
        var call = new ToolRowViewModel("grep", """{"pattern":"waiting"}""");
        group.Add(call);

        var window = Show(group);
        call.Complete(new ToolResult("no matches", IsError: true));
        SharedUi.Settle(window);

        Assert.False(Assert.Single(window.GetVisualDescendants().OfType<WaitingGlyph>()).IsVisible);
        Assert.True(CardMark(window, "fail").IsVisible);
        Assert.False(CardMark(window, "ok").IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_standalone_row_drops_its_ellipsis_for_the_glyph_and_ends_on_the_check() => SharedUi.RunAsync(() =>
    {
        var call = new ToolRowViewModel("read", """{"file_path":"src/Forge.App/App.axaml"}""");

        var window = Show(call);
        var busy = Assert.Single(window.GetVisualDescendants().OfType<WaitingGlyph>());
        var mark = Assert.Single(Assert.IsType<Panel>(busy.Parent).Children.OfType<Avalonia.Controls.TextBlock>());
        Assert.True(busy.IsVisible);
        Assert.False(mark.IsVisible, "a running row no longer shows a literal ellipsis");

        call.Complete(new ToolResult("file contents"));
        SharedUi.Settle(window);

        Assert.False(busy.IsVisible);
        Assert.True(mark.IsVisible);
        Assert.Equal("✓", mark.Text);
        return Task.CompletedTask;
    });

    /// <summary>Shows the item through the app's own transcript template, exactly as the transcript renders that row.</summary>
    private static Window Show(object item)
    {
        var window = new Window { Width = 600, Height = 400, Content = new ContentControl { Content = item } };
        window.Show();
        SharedUi.Settle(window);
        return window;
    }

    /// <summary>The card's status mark for one state: the check or the cross the template keeps for a returned call.</summary>
    private static FluentIcon CardMark(Window window, string state) =>
        window.GetVisualDescendants()
            .OfType<FluentIcon>()
            .Single(icon => icon.Classes.Contains("tool-status") && icon.Classes.Contains(state));
}
