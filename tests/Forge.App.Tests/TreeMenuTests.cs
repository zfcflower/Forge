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
