using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The bottom group shows its body only while a tab is docked in it, or while the user has opened it by hand: a group kept
/// alive by its title-bar tools alone (the finished sub-agent dropdown, the new-terminal tool) has nothing to expand, so it
/// folds to its 28 px title bar - but its own restore button is always drawn and always acts, opening the group's empty state
/// instead of folding straight back. The session's own "collapsed by hand" wish is a separate flag and is not what folds it.
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
        // Folded, yet its restore button stays offered: the button is drawn and acts in every state.
        Assert.True(layout.BottomTabs.CanMinimize);
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
        Assert.False(layout.IsBottomMinimized);
        Assert.False(layout.IsBottomBodyOpen);
    }

    [Fact]
    public void The_restore_button_opens_an_empty_group_and_the_minimize_button_folds_it_again()
    {
        using var layout = NewLayout();
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;

        // The body has nothing to show, but the click is not swallowed: the group opens onto its empty state.
        layout.ToggleMinimize(layout.BottomTabs);
        Assert.False(layout.BottomTabs.IsMinimized);
        Assert.True(layout.IsBottomBodyOpen);

        layout.ToggleMinimize(layout.BottomTabs);
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomBodyOpen);
        // Neither click was the session's wish, so the next tab still opens the body by itself.
        Assert.False(layout.IsBottomMinimized);
        var tab = layout.OpenTab(layout.BottomTabs, "subagent:a1", TabKind.SubAgent, new object(), "Worker");
        Assert.False(layout.BottomTabs.IsMinimized);
        layout.CloseTab(tab);
        Assert.True(layout.BottomTabs.IsMinimized);
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

    /// <summary>
    /// The bottom group's minimize is the user's call even when nothing else in Main is expanded: Main is allowed to sit on its
    /// two title bars, and both restore buttons are drawn and clickable there. The fold is what the session asked for, so
    /// switching back to it keeps the group folded until its restore button is pressed.
    /// </summary>
    [Fact]
    public void The_bottom_group_folds_even_when_nothing_else_in_main_is_expanded()
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.MainTabs, "session:a", TabKind.Session, new object(), "A");
        layout.OpenTab(layout.BottomTabs, "terminal:a", TabKind.Terminal, new object(), "Terminal");

        layout.ToggleMinimize(layout.MainTabs);
        Assert.True(layout.MainTabs.IsMinimized);
        Assert.False(layout.BottomTabs.IsMinimized);

        layout.ToggleMinimize(layout.BottomTabs);
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomBodyOpen);

        // Nothing traps the user there: the group's own button reopens its body.
        layout.ToggleMinimize(layout.BottomTabs);
        Assert.False(layout.BottomTabs.IsMinimized);
        Assert.True(layout.IsBottomBodyOpen);
    }

    /// <summary>
    /// The document group may fold only while the bottom group is beside it: with the bottom group out of the layout the
    /// documents panel is the region's only one, and a fold there would draw a full-size empty card under a title bar.
    /// </summary>
    [Fact]
    public void The_document_group_stays_open_when_it_is_main_s_only_panel()
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.MainTabs, "session:a", TabKind.Session, new object(), "A");
        layout.OpenTab(layout.BottomTabs, "terminal:a", TabKind.Terminal, new object(), "Terminal");
        layout.ToggleMinimize(layout.MainTabs);
        Assert.True(layout.MainTabs.IsMinimized);

        // The only bottom tab leaves and the finished menu is empty: the group drops out of the layout.
        layout.CloseTab(layout.BottomTabs.Tabs[0]);
        Assert.False(layout.BottomTabs.IsVisible);
        Assert.False(layout.MainTabs.IsMinimized);
        Assert.False(layout.MainTabs.CanMinimize);
    }

    /// <summary>
    /// The composer's chip opens the region through the same door the title bar's own restore uses: an empty group opens
    /// onto its empty state instead of folding straight back - a chip that could not reach the body would be a dead
    /// button, and it is drawn whenever the session has sub-agent activity.
    /// </summary>
    [Fact]
    public void A_reveal_from_the_chip_opens_an_empty_group_onto_its_empty_state()
    {
        using var layout = NewLayout();
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomBodyOpen);

        layout.RevealBottomBody();
        Assert.False(layout.BottomTabs.IsMinimized);
        Assert.True(layout.IsBottomBodyOpen);

        // And the user's own button folds it back; the wish was not recorded, so the next tab still opens by itself.
        layout.ToggleMinimize(layout.BottomTabs);
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomMinimized);
    }

    /// <summary>
    /// The shell re-writes the session's wish on every roster event, value unchanged: that idempotent sync must not end a
    /// hand-opened empty body - a child finishing while the user reads the empty state would otherwise fold it under
    /// them - while a real change of the wish is a choice and folds it.
    /// </summary>
    [Fact]
    public void An_unchanged_wish_keeps_a_hand_opened_empty_body()
    {
        using var layout = NewLayout();
        layout.BottomTabs.KeepsVisibleWhenEmpty = true;
        layout.RevealBottomBody();
        Assert.True(layout.IsBottomBodyOpen);

        // The shell's idempotent sync write: same value, nothing happens.
        layout.IsBottomMinimized = false;
        Assert.True(layout.IsBottomBodyOpen);

        // The group being pointed at another session's world ends the moment without touching the wish.
        layout.DropBottomHandOpen();
        Assert.True(layout.BottomTabs.IsMinimized);
        Assert.False(layout.IsBottomMinimized);

        // A changed write is the session's own choice: it folds and records the wish.
        layout.RevealBottomBody();
        layout.IsBottomMinimized = true;
        Assert.False(layout.IsBottomBodyOpen);
    }

    [Fact]
    public void A_reveal_from_the_chip_opens_a_collapsed_group_with_tabs()
    {
        using var layout = NewLayout();
        layout.OpenTab(layout.BottomTabs, "subagent:a1", TabKind.SubAgent, new object(), "Worker");
        layout.IsBottomMinimized = true;
        Assert.True(layout.BottomTabs.IsMinimized);

        // The wish is what a group with tabs needs; the reveal records it and the body comes back.
        layout.RevealBottomBody();
        Assert.False(layout.IsBottomMinimized);
        Assert.True(layout.IsBottomBodyOpen);
    }

    [Fact]
    public void A_reveal_from_the_chip_does_nothing_without_a_group_to_open()
    {
        using var layout = NewLayout();
        layout.RevealBottomBody();
        Assert.False(layout.BottomTabs.IsVisible);
        Assert.False(layout.IsBottomMinimized);
    }

    private static WorkbenchLayout NewLayout() => new(new object(), new object(), new object(), new object(), new object());
}
