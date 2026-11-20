using System.Windows.Input;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>Every entry a tree menu can hold (lifecycle 5.2); <see cref="Separator"/> is the divider line.</summary>
internal enum TreeMenuKey
{
    Separator,
    NewWorkspace,
    LinkWorkspace,
    RenameProject,
    Reveal,
    OpenInVsCode,
    CloseProject,
    NewSession,
    NewAgentSession,
    ReplaceSession,
    Fork,
    ForkInherit,
    MergeBack,
    Sync,
    AbortMerge,
    RenameBranch,
    RemoveWorkspace,
    RestoreWorkspace,
    PurgeAll,
    RenameSession,
    Archive,
    Promote,
    RemoveSession,
    View,
    Continue,
    Restore,
    Purge,
}

/// <summary>The row types of 5.2 that have a menu; <see cref="None"/> is every row that has none.</summary>
internal enum TreeRowKind
{
    None,
    RepositoryProject,
    PlainProject,
    RootCheckout,
    Workspace,
    RecycledWorkspace,
    ActiveSession,
    AgentSession,
    HistorySession,
    RemovedSession,
}

/// <summary>
/// One row as the menu sees it: the tree's last plan (head, default branch, merge / rebase, directory present) plus the
/// in-memory flags of the moment (busy, handing over, operation, conflict). Built without any git or database call (I3).
/// </summary>
internal sealed record TreeRowState(TreeRowKind Kind)
{
    /// <summary>The row belongs to a repository project, so the fork entries exist.</summary>
    public bool InRepository { get; init; }

    /// <summary>A workspace row's fork source (5.4 rule 6); null when it is not a fork.</summary>
    public string? ForkOfBranch { get; init; }

    /// <summary>The title of the workspace's active session, or of the session row itself; null when there is none.</summary>
    public string? SessionTitle { get; init; }

    /// <summary>Why that session is not idle (turn, sub-agents, queue, interjections); empty when it is.</summary>
    public IReadOnlyList<string> Busy { get; init; } = [];

    /// <summary>That session is being handed over.</summary>
    public bool HandingOver { get; init; }

    /// <summary>A lifecycle operation holds the workspace (fork commit, merge, removal, rename).</summary>
    public bool Operating { get; init; }

    /// <summary>The workspace waits for a merge conflict to be resolved or aborted.</summary>
    public bool Conflicted { get; init; }

    /// <summary>The checkout's HEAD as last read; null when it was not read (the entries stay, the flow checks again).</summary>
    public GitHeadKind? Head { get; init; }

    /// <summary>The workspace's branch is known (the rename-branch entry needs one).</summary>
    public bool HasBranch { get; init; }

    /// <summary>The workspace has the repository's default branch checked out.</summary>
    public bool IsDefaultBranch { get; init; }

    /// <summary>The row's directory is gone (5.3 "目录缺失"; for a history session, "继续" cannot run).</summary>
    public bool DirectoryMissing { get; init; }

    /// <summary>
    /// Why git no longer treats the existing directory as a worktree (prunable, or not in the worktree list); null when it
    /// does or when that could not be read. What would run git there is disabled with this reason.
    /// </summary>
    public string? StaleReason { get; init; }

    /// <summary>A merge or rebase is in progress in the checkout (read at refresh).</summary>
    public bool MergeOrRebase { get; init; }

    /// <summary>The row is an agent session (a user-opened child session of a root), not a root session.</summary>
    public bool IsAgentSession { get; init; }

    /// <summary>A VS Code executable was resolved, so "open in VS Code" is worth offering; without one the entry is left out.</summary>
    public bool CanOpenInVsCode { get; init; }

    public bool HeadOffBranch => Head is GitHeadKind.Detached or GitHeadKind.UnbornBranch;
}

/// <summary>One generated menu entry: the key, whether it can run, why not (or a warning when it can), the default.</summary>
internal sealed record TreeMenuEntry(TreeMenuKey Key, bool IsEnabled = true, string? Reason = null, bool IsDefault = false)
{
    /// <summary>A caution shown on an entry that does run (5.3: a session on a detached HEAD).</summary>
    public string? Warning { get; init; }

    public static readonly TreeMenuEntry Divider = new(TreeMenuKey.Separator);

    public bool IsSeparator => Key == TreeMenuKey.Separator;
}

/// <summary>
/// The menu generation of lifecycle 5.4 rule 1: a pure function from a row snapshot to its entries. Structurally
/// impossible entries are left out; temporarily unavailable ones stay, disabled, with the reason (rule 2).
/// </summary>
internal static class TreeMenu
{
    public static IReadOnlyList<TreeMenuEntry> Build(TreeRowState row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entries = row.Kind switch
        {
            TreeRowKind.RepositoryProject => Project(row, repository: true),
            TreeRowKind.PlainProject => Project(row, repository: false),
            TreeRowKind.RootCheckout => RootCheckout(row),
            TreeRowKind.Workspace => Workspace(row),
            TreeRowKind.RecycledWorkspace =>
            [
                Gate(TreeMenuKey.RestoreWorkspace, row.Operating ? HandoverStrings.BusyWorkspaceOperating : null),
                TreeMenuEntry.Divider,
                new(TreeMenuKey.PurgeAll),
            ],
            TreeRowKind.ActiveSession => ActiveSession(row),
            // A session the user opened under a root stands beside it: it is renamed and archived here, and promoted to the
            // workspace's root from here; removal follows its own archive, so an active row never offers it.
            TreeRowKind.AgentSession => AgentSession(row),
            TreeRowKind.HistorySession => HistorySession(row),
            TreeRowKind.RemovedSession =>
            [
                new(TreeMenuKey.View, IsDefault: true),
                Gate(TreeMenuKey.Restore, WorkspaceBlock(row)),
                TreeMenuEntry.Divider,
                new(TreeMenuKey.Purge),
            ],
            _ => (List<TreeMenuEntry>)[],
        };
        return Tidy(entries);
    }

    /// <summary>
    /// The first group of a root-checkout or workspace row (5.3), which is also what its trailing "+" offers: the entries
    /// before the first divider of <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<TreeMenuEntry> FirstGroup(TreeRowState row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind is TreeRowKind.RootCheckout or TreeRowKind.Workspace ? First(row) : [];
    }

    private static List<TreeMenuEntry> Project(TreeRowState row, bool repository)
    {
        var entries = new List<TreeMenuEntry>();
        if (repository)
        {
            entries.Add(new(TreeMenuKey.NewWorkspace));
            entries.Add(new(TreeMenuKey.LinkWorkspace));
            entries.Add(TreeMenuEntry.Divider);
        }

        entries.Add(new(TreeMenuKey.RenameProject));
        entries.Add(new(TreeMenuKey.Reveal));
        AddOpenInVsCode(entries, row);
        entries.Add(TreeMenuEntry.Divider);
        entries.Add(new(TreeMenuKey.CloseProject));
        return entries;
    }

    private static List<TreeMenuEntry> RootCheckout(TreeRowState row)
    {
        var entries = First(row);
        entries.Add(TreeMenuEntry.Divider);
        AddAbort(entries, row);
        entries.Add(Gate(TreeMenuKey.Reveal, row.DirectoryMissing ? LifecycleStrings.DirectoryMissing : null));
        AddOpenInVsCode(entries, row);
        return entries;
    }

    private static List<TreeMenuEntry> Workspace(TreeRowState row)
    {
        var entries = First(row);
        entries.Add(TreeMenuEntry.Divider);
        if (row.DirectoryMissing)
        {
            // 5.3 "目录缺失": everything else goes; reveal stays disabled, and removal takes the missing-directory branch of 7.3.
            entries.Add(Gate(TreeMenuKey.Reveal, LifecycleStrings.DirectoryMissing));
            entries.Add(TreeMenuEntry.Divider);
            entries.Add(Gate(TreeMenuKey.RemoveWorkspace, OperationBlock(row)));
            return entries;
        }

        if (row.StaleReason is not null)
        {
            // A stale worktree: the folder is there to reveal, nothing that runs git in it can work; removal stays.
            entries.Add(new(TreeMenuKey.Reveal));
            AddOpenInVsCode(entries, row);
            entries.Add(TreeMenuEntry.Divider);
            entries.Add(Gate(TreeMenuKey.RemoveWorkspace, OperationBlock(row)));
            return entries;
        }

        var waits = WorkspaceBlock(row) ?? BusyReason(row);
        AddAbort(entries, row);
        if (row.ForkOfBranch is not null)
        {
            entries.Add(Gate(TreeMenuKey.MergeBack, waits));
            entries.Add(Gate(TreeMenuKey.Sync, waits));
            entries.Add(TreeMenuEntry.Divider);
        }

        // Hidden, not disabled, where it cannot exist: the default branch, a HEAD that is not on a branch, no branch known.
        if (row.HasBranch && !row.IsDefaultBranch && !row.HeadOffBranch)
        {
            entries.Add(Gate(TreeMenuKey.RenameBranch, waits ?? (row.MergeOrRebase ? TreeMenuStrings.MergeOrRebase : null)));
        }

        entries.Add(new(TreeMenuKey.Reveal));
        AddOpenInVsCode(entries, row);
        entries.Add(TreeMenuEntry.Divider);
        entries.Add(Gate(TreeMenuKey.RemoveWorkspace, waits));
        return entries;
    }

    /// <summary>5.3: new session / replace X / parallel fork, by the active session's state.</summary>
    private static List<TreeMenuEntry> First(TreeRowState row)
    {
        var block = row.DirectoryMissing ? LifecycleStrings.DirectoryMissing : row.StaleReason ?? WorkspaceBlock(row);
        var warning = row.HeadOffBranch ? TreeMenuStrings.HeadWarning : null;
        if (row.SessionTitle is null)
        {
            return
            [
                Gate(TreeMenuKey.NewSession, block, isDefault: true) with { Warning = warning },
                // An agent session hangs under an active root session; without one there is nothing to attach it to.
                Gate(TreeMenuKey.NewAgentSession, block ?? AgentSessionStrings.NoRoot),
            ];
        }

        var busy = BusyReason(row);
        var replaceReason = block ?? busy;
        var entries = new List<TreeMenuEntry>(3)
        {
            Gate(TreeMenuKey.ReplaceSession, replaceReason, isDefault: true) with { Warning = warning },
            // The root is busy or not: an agent session queues for a run slot like any other child, so only a workspace that
            // cannot take a new session at all disables this one.
            Gate(TreeMenuKey.NewAgentSession, block),
        };
        if (row.InRepository)
        {
            // A busy holder leaves "fork" as the one way on, so it becomes the default (5.3 "X 忙").
            var forkReason = block ?? (row.HeadOffBranch ? TreeMenuStrings.HeadNotOnBranch : null);
            entries.Add(Gate(TreeMenuKey.Fork, forkReason, isDefault: replaceReason is not null));
        }

        return entries;
    }

    private static List<TreeMenuEntry> ActiveSession(TreeRowState row)
    {
        // Handing over / an operation / a conflict stop everything; busy stops what would pull the session away (5.3).
        var block = WorkspaceBlock(row);
        var waits = block ?? BusyReason(row);
        var entries = new List<TreeMenuEntry>(3) { Gate(TreeMenuKey.RenameSession, block) };
        if (row.InRepository)
        {
            entries.Add(Gate(TreeMenuKey.ForkInherit, waits));
        }

        // No removal entry: only an archived (history) session can be removed, so the archive above is what the row offers.
        entries.Add(Gate(TreeMenuKey.Archive, waits));
        return entries;
    }

    /// <summary>An agent session beside its root: rename, promote it to the workspace's root, or archive it.</summary>
    private static List<TreeMenuEntry> AgentSession(TreeRowState row)
    {
        var block = WorkspaceBlock(row);
        var waits = block ?? BusyReason(row);
        return
        [
            Gate(TreeMenuKey.RenameSession, block),
            Gate(TreeMenuKey.Promote, waits),
            TreeMenuEntry.Divider,
            Gate(TreeMenuKey.Archive, waits),
        ];
    }

    private static List<TreeMenuEntry> HistorySession(TreeRowState row)
    {
        var block = WorkspaceBlock(row);
        var entries = new List<TreeMenuEntry> { new(TreeMenuKey.View, IsDefault: true) };
        if (!row.IsAgentSession)
        {
            // Only a root session continues (it resumes the workspace); an agent session's archived row is read-only.
            entries.Add(Gate(TreeMenuKey.Continue, row.DirectoryMissing ? LifecycleStrings.DirectoryMissing : row.StaleReason));
        }

        entries.Add(Gate(TreeMenuKey.RenameSession, block));
        entries.Add(TreeMenuEntry.Divider);
        entries.Add(Gate(TreeMenuKey.RemoveSession, block));
        return entries;
    }

    /// <summary>"Abort the merge" while the workspace is conflicted (6.6 step 5); refused only while another operation runs.</summary>
    private static void AddAbort(List<TreeMenuEntry> entries, TreeRowState row)
    {
        if (row.Conflicted)
        {
            entries.Add(Gate(TreeMenuKey.AbortMerge, row.Operating ? HandoverStrings.BusyWorkspaceOperating : null));
        }
    }

    /// <summary>
    /// "Open in VS Code" right after "reveal", for a project or workspace row whose directory exists. Without a resolvable VS Code
    /// executable the entry is left out rather than greyed: there is nothing the user could do to make it work from here.
    /// </summary>
    private static void AddOpenInVsCode(List<TreeMenuEntry> entries, TreeRowState row)
    {
        if (row.CanOpenInVsCode && !row.DirectoryMissing)
        {
            entries.Add(new(TreeMenuKey.OpenInVsCode));
        }
    }

    /// <summary>The states that stop every entry of a row: handing over, an operation, a merge conflict (5.3).</summary>
    private static string? WorkspaceBlock(TreeRowState row) =>
        row.HandingOver ? HandoverStrings.BusyHandingOver
        : row.Operating ? HandoverStrings.BusyWorkspaceOperating
        : row.Conflicted ? HandoverStrings.BusyMergeConflict
        : null;

    /// <summary>Removal of a missing directory only waits for an operation or a handover, not for a conflict git cannot see.</summary>
    private static string? OperationBlock(TreeRowState row) =>
        row.HandingOver ? HandoverStrings.BusyHandingOver
        : row.Operating ? HandoverStrings.BusyWorkspaceOperating
        : null;

    private static string? BusyReason(TreeRowState row) =>
        row.Busy.Count > 0 ? TreeMenuStrings.Busy(row.SessionTitle ?? string.Empty, row.Busy) : null;

    private static TreeMenuEntry Gate(TreeMenuKey key, string? reason, bool isDefault = false) =>
        new(key, reason is null, reason, isDefault && reason is null);

    /// <summary>No divider at the start or the end, and never two in a row.</summary>
    private static List<TreeMenuEntry> Tidy(List<TreeMenuEntry> entries)
    {
        var tidy = new List<TreeMenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (tidy.Count == 0 || tidy[^1].IsSeparator))
            {
                continue;
            }

            tidy.Add(entry);
        }

        while (tidy.Count > 0 && tidy[^1].IsSeparator)
        {
            tidy.RemoveAt(tidy.Count - 1);
        }

        return tidy;
    }

    /// <summary>The label of an entry, in the current language; the fork entries name the row's source branch.</summary>
    public static string Header(TreeMenuKey key, TreeRowState row) => key switch
    {
        TreeMenuKey.NewWorkspace => ProjectStrings.NewWorkspace,
        TreeMenuKey.LinkWorkspace => TreeMenuStrings.LinkWorkspace,
        TreeMenuKey.RenameProject => TreeMenuStrings.RenameProject,
        TreeMenuKey.Reveal => ProjectStrings.RevealInExplorer,
        TreeMenuKey.OpenInVsCode => ProjectStrings.OpenInVsCode,
        TreeMenuKey.CloseProject => RemovalStrings.MenuCloseProject,
        TreeMenuKey.NewSession => ProjectStrings.NewSession,
        TreeMenuKey.NewAgentSession => AgentSessionStrings.MenuNew,
        TreeMenuKey.ReplaceSession => TreeMenuStrings.ReplaceSession(row.SessionTitle ?? string.Empty),
        TreeMenuKey.Fork => ForkStrings.MenuFork,
        TreeMenuKey.ForkInherit => ForkStrings.MenuForkInherit,
        TreeMenuKey.MergeBack => ForkStrings.MenuMergeBack(row.ForkOfBranch ?? string.Empty),
        TreeMenuKey.Sync => ForkStrings.MenuSync(row.ForkOfBranch ?? string.Empty),
        TreeMenuKey.AbortMerge => ForkStrings.MenuAbort,
        TreeMenuKey.RenameBranch => TreeMenuStrings.RenameBranch,
        TreeMenuKey.RemoveWorkspace => RemovalStrings.MenuRemoveWorkspace,
        TreeMenuKey.RestoreWorkspace => RemovalStrings.MenuRestoreWorkspace,
        TreeMenuKey.PurgeAll => RemovalStrings.MenuPurgeAll,
        TreeMenuKey.RenameSession => ProjectStrings.Rename,
        TreeMenuKey.Archive => ProjectStrings.Archive,
        TreeMenuKey.Promote => ProjectStrings.PromoteToMain,
        TreeMenuKey.RemoveSession => RemovalStrings.MenuRemoveSession,
        TreeMenuKey.View => TreeMenuStrings.ViewReadOnly,
        TreeMenuKey.Continue => TreeMenuStrings.ContinueSession,
        TreeMenuKey.Restore => RemovalStrings.MenuRestore,
        TreeMenuKey.Purge => RemovalStrings.MenuPurge,
        _ => "-",
    };
}

/// <summary>
/// One entry of a rendered tree menu: the label, the command it runs and the generated state. A disabled entry shows its
/// reason as the tooltip, an enabled one its warning; the default entry is drawn in bold. A divider renders as "-".
/// </summary>
internal sealed class TreeMenuItemViewModel
{
    public TreeMenuItemViewModel(TreeMenuEntry entry, string header, ICommand? command)
    {
        Entry = entry;
        Header = entry.IsSeparator ? "-" : header;
        Command = entry.IsSeparator ? null : command;

        // Read once, when the menu is built: the command's own gate (in memory, e.g. already running) can still say no
        // where the generated state said yes, and such an entry must not be a disabled item without a reason (5.4 rule 2).
        IsEnabled = !IsSeparator && entry.IsEnabled && Command is not null && Command.CanExecute(null);
        Tip = IsSeparator ? null
            : IsEnabled ? entry.Warning
            : entry.IsEnabled ? TreeMenuStrings.Unavailable
            : entry.Reason;
    }

    public TreeMenuEntry Entry { get; }

    public TreeMenuKey Key => Entry.Key;

    public string Header { get; }

    public ICommand? Command { get; }

    public bool IsSeparator => Entry.IsSeparator;

    /// <summary>The generated state decides; an entry whose command the row does not have, or cannot run now, stays disabled.</summary>
    public bool IsEnabled { get; }

    public bool IsDefault => Entry.IsDefault;

    /// <summary>A disabled entry's reason (the generated one, else "not available now"); an enabled entry's warning.</summary>
    public string? Tip { get; }

    public bool HasTip => Tip is { Length: > 0 };

    /// <summary>Runs the entry when it can run; returns false otherwise.</summary>
    public bool Run()
    {
        if (!IsEnabled || !Command!.CanExecute(null))
        {
            return false;
        }

        Command.Execute(null);
        return true;
    }
}
