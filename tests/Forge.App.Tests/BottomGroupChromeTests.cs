using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The bottom group's title bar keeps one shape: the minimize/restore button is drawn in every state, so a tab docking or the
/// last one leaving never shifts the tools beside it, and it always acts - with nothing docked it opens the group onto a line of
/// text instead of a blank box. The float button is the opposite: it floats the active tab, so with nothing open it is not
/// offered at all.
/// </summary>
[Collection("forge-app-ui")]
public sealed class BottomGroupChromeTests
{
    [Fact]
    public Task The_minimize_button_always_works_and_the_float_button_needs_a_tab() => SharedUi.RunAsync(() =>
    {
        using var layout = NewLayout();
        // The group stays in the layout on its finished dropdown alone - the state a finished round leaves behind.
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;
        var (window, chrome) = Show(layout);

        Assert.False(layout.IsBottomBodyOpen);
        Assert.True(IconButton(chrome, "minimize").IsVisible);
        Assert.False(IconButton(chrome, "float").IsVisible);

        // A tab docks: the float button joins the title bar and the empty text never shows over a real body.
        var tab = layout.OpenTab(layout.BottomTabs, "subagent:a1", TabKind.SubAgent, new object(), "Worker");
        SharedUi.Settle(window);
        Assert.True(IconButton(chrome, "minimize").IsVisible);
        Assert.True(IconButton(chrome, "float").IsVisible);
        Assert.False(EmptyText(chrome).IsVisible);

        // It leaves again: the group folds, the float button goes with the tab, and the restore button that is still there
        // opens the empty state - the click is not swallowed by the fold.
        layout.CloseTab(tab);
        SharedUi.Settle(window);
        Assert.True(IconButton(chrome, "minimize").IsVisible);
        Assert.False(IconButton(chrome, "float").IsVisible);
        Assert.False(EmptyText(chrome).IsVisible);

        layout.ToggleMinimize(layout.BottomTabs);
        SharedUi.Settle(window);
        Assert.True(EmptyText(chrome).IsVisible);
        Assert.Equal(ShellStrings.BottomGroupEmpty, EmptyText(chrome).Text);

        // Minimizing again puts the body away with its text.
        layout.ToggleMinimize(layout.BottomTabs);
        SharedUi.Settle(window);
        Assert.False(EmptyText(chrome).IsVisible);
        Assert.True(IconButton(chrome, "minimize").IsVisible);

        window.Close();
        return Task.CompletedTask;
    });

    private static WorkbenchLayout NewLayout() =>
        new(new object(), new object(), new object(), new object(), new object());

    /// <summary>Shows the workspace and returns the window and the bottom group's own title bar.</summary>
    private static (Window Window, PanelChrome Chrome) Show(WorkbenchLayout layout)
    {
        var host = new WorkbenchHost { Layout = layout };
        var window = new Window { Width = 1200, Height = 800, Content = host };
        window.Show();
        SharedUi.Settle(window);
        var chrome = host.GetVisualDescendants()
            .OfType<PanelChrome>()
            .Single(candidate => ReferenceEquals(candidate.Model, layout.BottomTabs));
        return (window, chrome);
    }

    private static PanelIconButton IconButton(PanelChrome chrome, string name)
    {
        var id = "panel-" + WorkbenchIds.BottomTabs + "-" + name;
        return chrome.GetVisualDescendants()
            .OfType<PanelIconButton>()
            .Single(button => AutomationProperties.GetAutomationId(button) == id);
    }

    /// <summary>The body's empty state: the one TextBlock the panel template draws for a group with nothing docked.</summary>
    private static TextBlock EmptyText(PanelChrome chrome) =>
        chrome.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PART_Empty");
}
