using System.Windows.Input;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The history-row menu (5.4, 7.2): a session that entered history offers restore, rename (archived rows) and remove — one
/// remove entry, no separate "delete permanently", and no "view" entry (the row's own click opens the read-only tab, which
/// has no input area). An agent session's history row restores like a root's.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TreeMenuTests
{
    private static TreeMenuKey[] Keys(TreeRowState row) => [.. TreeMenu.Build(row).Select(static entry => entry.Key)];

    [Fact]
    public void An_archived_session_offers_restore_rename_and_remove()
    {
        var keys = Keys(new TreeRowState(TreeRowKind.HistorySession) { SessionTitle = "archived" });

        Assert.Equal(
            [TreeMenuKey.Restore, TreeMenuKey.RenameSession, TreeMenuKey.Separator, TreeMenuKey.RemoveSession],
            keys);
    }

    [Fact]
    public void A_removed_session_offers_restore_and_remove()
    {
        var keys = Keys(new TreeRowState(TreeRowKind.RemovedSession) { SessionTitle = "removed" });

        Assert.Equal(
            [TreeMenuKey.Restore, TreeMenuKey.Separator, TreeMenuKey.RemoveSession],
            keys);
    }

    [Fact]
    public void An_agent_session_in_history_can_be_restored_like_a_root()
    {
        var row = new TreeRowState(TreeRowKind.HistorySession)
        {
            SessionTitle = "worker",
            IsAgentSession = true,
        };

        var restore = Assert.Single(TreeMenu.Build(row), static entry => entry.Key == TreeMenuKey.Restore);

        Assert.True(restore.IsEnabled);
        Assert.Null(restore.Reason);
    }

    [Fact]
    public void A_restorable_history_session_keeps_the_restore_entry_enabled()
    {
        var row = new TreeRowState(TreeRowKind.HistorySession) { SessionTitle = "archived" };

        var restore = Assert.Single(TreeMenu.Build(row), static entry => entry.Key == TreeMenuKey.Restore);

        Assert.True(restore.IsEnabled);
        Assert.Null(restore.Reason);
    }

    [Fact]
    public void A_workspace_without_a_session_still_offers_fork()
    {
        var fork = Assert.Single(
            TreeMenu.Build(new TreeRowState(TreeRowKind.Workspace) { InRepository = true }),
            static entry => entry.Key == TreeMenuKey.Fork);

        Assert.True(fork.IsEnabled);
    }

    [Fact]
    public void A_workspace_without_a_session_cannot_fork_off_a_detached_head()
    {
        var row = new TreeRowState(TreeRowKind.Workspace) { InRepository = true, Head = GitHeadKind.Detached };

        var fork = Assert.Single(TreeMenu.Build(row), static entry => entry.Key == TreeMenuKey.Fork);

        Assert.False(fork.IsEnabled);
        Assert.Equal(TreeMenuStrings.HeadNotOnBranch, fork.Reason);
    }

    [Fact]
    public Task A_history_session_row_runs_its_remove_and_restore_entries() => SharedUi.RunAsync(() =>
    {
        var remove = new TestCommand();
        var restore = new TestCommand();
        var node = new TreeNodeViewModel(TreeNodeKind.Session)
        {
            Key = "session:s1",
            SessionId = "s1",
            MenuState = static () => new TreeRowState(TreeRowKind.HistorySession) { SessionTitle = "archived" },
        };
        node.SetCommands(open: null, newSession: null, archive: null, rename: null, reveal: null, remove: remove);
        node.SetRestoreCommand(restore);

        var items = node.MenuItems();

        var removeItem = Assert.Single(items, static item => item.Key == TreeMenuKey.RemoveSession);
        var restoreItem = Assert.Single(items, static item => item.Key == TreeMenuKey.Restore);
        Assert.Same(remove, removeItem.Command);
        Assert.Same(restore, restoreItem.Command);
        Assert.True(removeItem.IsEnabled);
        Assert.True(restoreItem.IsEnabled);
        return Task.CompletedTask;
    });

    /// <summary>
    /// "New agent session" (5.3) picks the kind in a submenu: the entry itself runs nothing, it is live exactly when one of
    /// its rows is, and a kind that may not drive a session keeps its row with the reason on it.
    /// </summary>
    [Fact]
    public void The_new_agent_session_entry_opens_a_submenu_of_kinds()
    {
        var worker = new TestCommand();
        var item = new TreeMenuItemViewModel(
            new TreeMenuEntry(TreeMenuKey.NewAgentSession),
            "New agent session",
            command: null,
            [
                new AgentKindMenuItem("Worker", "Implements one task end to end.", IsEnabled: true, Reason: null, worker, IsDefault: true),
                new AgentKindMenuItem("Scout", "Read-only reconnaissance.", IsEnabled: false, Reason: AgentSessionStrings.AgentNoTools, Command: null),
            ]);

        Assert.True(item.HasKinds);
        Assert.True(item.IsEnabled);
        Assert.Null(item.Command);
        Assert.True(item.Kinds[0].IsDefault);
        Assert.Same(worker, item.Kinds[0].Command);
        Assert.False(item.Kinds[1].IsEnabled);
        Assert.Equal(AgentSessionStrings.AgentNoTools, item.Kinds[1].Reason);
    }

    /// <summary>A submenu whose every row is unavailable must not read as clickable, and says why.</summary>
    [Fact]
    public void A_submenu_with_no_runnable_kind_is_disabled()
    {
        var item = new TreeMenuItemViewModel(
            new TreeMenuEntry(TreeMenuKey.NewAgentSession),
            "New agent session",
            command: null,
            [new AgentKindMenuItem("Scout", "Read-only reconnaissance.", IsEnabled: false, Reason: AgentSessionStrings.AgentNoTools, Command: null)]);

        Assert.False(item.IsEnabled);
        Assert.Equal(TreeMenuStrings.Unavailable, item.Tip);
    }

    /// <summary>Each kind row carries its own summary, and the summary stops at the menu's room (40 characters, one ellipsis).</summary>
    [Fact]
    public void A_kind_summary_is_cut_to_the_menu_room()
    {
        Assert.Equal("Read-only reconnaissance.", AgentKindMenuItem.Brief("  Read-only reconnaissance.  "));

        var cut = AgentKindMenuItem.Brief(new string('x', AgentKindMenuItem.BriefLimit + 20));
        Assert.Equal(AgentKindMenuItem.BriefLimit, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
