using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The bottom group shows its body only while a tab is docked in it. A group kept alive by its title-bar tools alone (the
/// finished sub-agent dropdown, the new-terminal tool) has nothing to expand, so it folds to its 28 px title bar and offers
/// no restore button; the session's own "collapsed by hand" wish is a separate flag and is not what folds it. A docked tab
/// that opens the body again is what the composer's chip and the panel chrome read.
/// </summary>
public sealed class BottomGroupFoldTests
{
    [Fact]
    public void A_group_with_nothing_docked_folds_and_the_next_tab_opens_it_again()
    {
        using var layout = NewLayout();
        Assert.False(layout.BottomTabs.IsVisible);

        // The finished dropdown alone keeps the group in the layout - at title-bar height, with no body to restore.
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;
        Assert.True(layout.BottomTabs.IsVisible);
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.BottomTabs.CanMinimize);
        Assert.False(layout.IsBottomBodyOpen);

        var tab = layout.OpenTab(layout.BottomTabs, "subagent:a1", TabKind.SubAgent, new object(), "Worker");
        Assert.False(layout.BottomTabs.IsMinimized);
        Assert.True(layout.BottomTabs.CanMinimize);
        Assert.True(layout.IsBottomBodyOpen);

        // The agent finished: its tab leaves, the group folds again, and folding it is not the session's choice, so the
        // sub-agent that starts next turn opens the body without the user touching anything.
        layout.CloseTab(tab);
        Assert.True(layout.BottomTabs.IsVisible);
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.BottomTabs.CanMinimize);
        Assert.False(layout.IsBottomMinimized);
        Assert.False(layout.IsBottomBodyOpen);
    }

    [Fact]
    public void A_group_the_user_collapsed_keeps_the_next_tab_folded_until_it_is_restored()
    {
        using var layout = NewLayout();
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;
        layout.IsBottomMinimized = true;

        layout.OpenTab(layout.BottomTabs, "subagent:a1", TabKind.SubAgent, new object(), "Worker");
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.True(layout.BottomTabs.CanMinimize);
        Assert.False(layout.IsBottomBodyOpen);

        // The title bar's own restore button: the body comes back and the choice moves with it.
        layout.ToggleMinimize(layout.BottomTabs);
        Assert.False(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomMinimized);
        Assert.True(layout.IsBottomBodyOpen);
    }

    private static WorkbenchLayout NewLayout() => new(new object(), new object(), new object(), new object(), new object());
}
