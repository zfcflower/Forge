using System.Text;
using System.Windows.Input;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// The menu generator of TASK-session-worktree-lifecycle 5.4 rule 1, table-driven over every row of 5.2 and every state
/// of 5.3: which entries exist (hidden = structurally impossible), which are enabled, why not, and which is the default.
/// A shape reads "*Key" for the default, "!Key" for disabled and "|" for a divider.
/// </summary>
public sealed class TreeMenuTests
{
    private static readonly IReadOnlyList<string> Turn = ["turn 3"];

    private static readonly Dictionary<string, (TreeRowState Row, string Shape)> Cases = new()
    {
        // 5.2 project rows.
        ["repository project"] = (new(TreeRowKind.RepositoryProject), "NewWorkspace LinkWorkspace | RenameProject Reveal | CloseProject"),
        ["plain project"] = (new(TreeRowKind.PlainProject), "RenameProject Reveal | CloseProject"),

        // 5.3 on the root checkout: the first group, then reveal; never rename branch or remove workspace.
        ["root, no session"] = (Root(), "*NewSession | Reveal"),
        ["root, X idle"] = (Root() with { SessionTitle = "X" }, "*ReplaceSession Fork | Reveal"),
        ["root, X busy"] = (Root() with { SessionTitle = "X", Busy = Turn }, "!ReplaceSession *Fork | Reveal"),
        ["root, handing over"] = (Root() with { SessionTitle = "X", HandingOver = true }, "!ReplaceSession !Fork | Reveal"),
        ["root, operating"] = (Root() with { SessionTitle = "X", Operating = true }, "!ReplaceSession !Fork | Reveal"),
        ["root, conflict"] = (Root() with { SessionTitle = "X", Conflicted = true }, "!ReplaceSession !Fork | AbortMerge Reveal"),
        ["root, detached"] = (Root() with { SessionTitle = "X", Head = GitHeadKind.Detached }, "*ReplaceSession !Fork | Reveal"),
        ["root, unborn, no session"] = (Root() with { Head = GitHeadKind.UnbornBranch }, "*NewSession | Reveal"),
        ["root, default branch"] = (Root() with { SessionTitle = "X", IsDefaultBranch = true }, "*ReplaceSession Fork | Reveal"),
        ["root, missing"] = (Root() with { SessionTitle = "X", DirectoryMissing = true }, "!ReplaceSession !Fork | !Reveal"),
        ["root, not a repository"] = (Root() with { SessionTitle = "X", InRepository = false }, "*ReplaceSession | Reveal"),
        ["root, not a repository, no session"] = (Root() with { InRepository = false, Head = null, HasBranch = false }, "*NewSession | Reveal"),
        ["root, merge or rebase"] = (Root() with { SessionTitle = "X", MergeOrRebase = true }, "*ReplaceSession Fork | Reveal"),

        // 5.3 on a linked workspace.
        ["workspace, no session"] = (Linked(), "*NewSession | RenameBranch Reveal | RemoveWorkspace"),
        ["workspace, X idle"] = (Linked() with { SessionTitle = "X" }, "*ReplaceSession Fork | RenameBranch Reveal | RemoveWorkspace"),
        ["workspace, X busy"] = (Linked() with { SessionTitle = "X", Busy = Turn }, "!ReplaceSession *Fork | !RenameBranch Reveal | !RemoveWorkspace"),
        ["workspace, handing over"] = (Linked() with { SessionTitle = "X", HandingOver = true }, "!ReplaceSession !Fork | !RenameBranch Reveal | !RemoveWorkspace"),
        ["workspace, operating"] = (Linked() with { SessionTitle = "X", Operating = true }, "!ReplaceSession !Fork | !RenameBranch Reveal | !RemoveWorkspace"),
        ["workspace, conflict"] = (Linked() with { SessionTitle = "X", Conflicted = true }, "!ReplaceSession !Fork | AbortMerge !RenameBranch Reveal | !RemoveWorkspace"),
        ["workspace, conflict while operating"] = (Linked() with { Conflicted = true, Operating = true }, "!NewSession | !AbortMerge !RenameBranch Reveal | !RemoveWorkspace"),
        ["workspace, detached"] = (Linked() with { SessionTitle = "X", Head = GitHeadKind.Detached }, "*ReplaceSession !Fork | Reveal | RemoveWorkspace"),
        ["workspace, unborn"] = (Linked() with { SessionTitle = "X", Head = GitHeadKind.UnbornBranch }, "*ReplaceSession !Fork | Reveal | RemoveWorkspace"),
        ["workspace, default branch"] = (Linked() with { SessionTitle = "X", IsDefaultBranch = true }, "*ReplaceSession Fork | Reveal | RemoveWorkspace"),
        ["workspace, default branch, X busy"] = (Linked() with { SessionTitle = "X", IsDefaultBranch = true, Busy = Turn }, "!ReplaceSession *Fork | Reveal | !RemoveWorkspace"),
        ["workspace, branch unknown"] = (Linked() with { HasBranch = false }, "*NewSession | Reveal | RemoveWorkspace"),
        ["workspace, head not read"] = (Linked() with { Head = null }, "*NewSession | RenameBranch Reveal | RemoveWorkspace"),
        ["workspace, merge or rebase"] = (Linked() with { SessionTitle = "X", MergeOrRebase = true }, "*ReplaceSession Fork | !RenameBranch Reveal | RemoveWorkspace"),
        ["workspace, missing"] = (Linked() with { SessionTitle = "X", DirectoryMissing = true }, "!ReplaceSession !Fork | !Reveal | RemoveWorkspace"),
        ["workspace, missing while operating"] = (Linked() with { DirectoryMissing = true, Operating = true }, "!NewSession | !Reveal | !RemoveWorkspace"),
        ["workspace, missing in conflict"] = (Linked() with { DirectoryMissing = true, Conflicted = true }, "!NewSession | !Reveal | RemoveWorkspace"),

        // 5.2 fork workspace: merge back / sync after the first-group divider.
        ["fork, X idle"] = (Fork() with { SessionTitle = "X" }, "*ReplaceSession Fork | MergeBack Sync | RenameBranch Reveal | RemoveWorkspace"),
        ["fork, X busy"] = (Fork() with { SessionTitle = "X", Busy = Turn }, "!ReplaceSession *Fork | !MergeBack !Sync | !RenameBranch Reveal | !RemoveWorkspace"),
        ["fork, detached"] = (Fork() with { SessionTitle = "X", Head = GitHeadKind.Detached }, "*ReplaceSession !Fork | MergeBack Sync | Reveal | RemoveWorkspace"),
        ["fork, unborn"] = (Fork() with { SessionTitle = "X", Head = GitHeadKind.UnbornBranch }, "*ReplaceSession !Fork | MergeBack Sync | Reveal | RemoveWorkspace"),
        ["fork, default branch"] = (Fork() with { SessionTitle = "X", IsDefaultBranch = true }, "*ReplaceSession Fork | MergeBack Sync | Reveal | RemoveWorkspace"),
        ["fork, merge or rebase"] = (Fork() with { SessionTitle = "X", MergeOrRebase = true }, "*ReplaceSession Fork | MergeBack Sync | !RenameBranch Reveal | RemoveWorkspace"),
        ["fork, missing"] = (Fork() with { SessionTitle = "X", DirectoryMissing = true }, "!ReplaceSession !Fork | !Reveal | RemoveWorkspace"),

        // A stale worktree (git marks it prunable or no longer lists it): the folder is there, git cannot work in it.
        ["workspace, stale"] = (Linked() with { SessionTitle = "X", StaleReason = "stale" }, "!ReplaceSession !Fork | Reveal | RemoveWorkspace"),
        ["workspace, stale, no session"] = (Linked() with { StaleReason = "stale" }, "!NewSession | Reveal | RemoveWorkspace"),
        ["workspace, stale while operating"] = (Linked() with { StaleReason = "stale", Operating = true }, "!NewSession | Reveal | !RemoveWorkspace"),
        ["fork, stale"] = (Fork() with { SessionTitle = "X", StaleReason = "stale" }, "!ReplaceSession !Fork | Reveal | RemoveWorkspace"),
        ["workspace, missing and stale"] = (Linked() with { SessionTitle = "X", DirectoryMissing = true, StaleReason = "stale" }, "!ReplaceSession !Fork | !Reveal | RemoveWorkspace"),
        ["fork, conflict"] = (Fork() with { Conflicted = true }, "!NewSession | AbortMerge !MergeBack !Sync | !RenameBranch Reveal | !RemoveWorkspace"),

        // 5.2 recycled workspace.
        ["recycled workspace"] = (new(TreeRowKind.RecycledWorkspace), "RestoreWorkspace | PurgeAll"),
        ["recycled workspace, operating"] = (new(TreeRowKind.RecycledWorkspace) { Operating = true }, "!RestoreWorkspace | PurgeAll"),

        // 5.2 / 5.3 session rows.
        ["active session (repository)"] = (Active(), "RenameSession ForkInherit Archive | RemoveSession"),
        ["active session, busy"] = (Active() with { Busy = Turn }, "RenameSession !ForkInherit !Archive | !RemoveSession"),
        ["active session, handing over"] = (Active() with { HandingOver = true }, "!RenameSession !ForkInherit !Archive | !RemoveSession"),
        ["active session (not a repository)"] = (Active() with { InRepository = false }, "RenameSession Archive | RemoveSession"),
        ["active session (not a repository), busy"] = (Active() with { InRepository = false, Busy = Turn }, "RenameSession !Archive | !RemoveSession"),
        ["history session"] = (new(TreeRowKind.HistorySession) { SessionTitle = "H" }, "*View Continue RenameSession | RemoveSession"),
        ["history session, directory missing"] = (new(TreeRowKind.HistorySession) { DirectoryMissing = true }, "*View !Continue RenameSession | RemoveSession"),
        ["history session, workspace operating"] = (new(TreeRowKind.HistorySession) { Operating = true }, "*View Continue !RenameSession | !RemoveSession"),
        ["removed session"] = (new(TreeRowKind.RemovedSession), "*View Restore | Purge"),
        ["removed session, workspace operating"] = (new(TreeRowKind.RemovedSession) { Operating = true }, "*View !Restore | Purge"),

        // 5.2: sub-agents, the history container and the general panel have no menu.
        ["no menu"] = (new(TreeRowKind.None) { SessionTitle = "X", Busy = Turn }, string.Empty),
    };

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Cases.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_row_and_state_generates_its_menu(string name)
    {
        var (row, shape) = Cases[name];
        Assert.Equal(shape, Shape(TreeMenu.Build(row)));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_disabled_entry_says_why_and_only_enabled_entries_are_defaults(string name)
    {
        foreach (var entry in TreeMenu.Build(Cases[name].Row))
        {
            if (entry.IsSeparator)
            {
                continue;
            }

            Assert.Equal(entry.IsEnabled, entry.Reason is null);
            Assert.False(entry.IsDefault && !entry.IsEnabled, entry.Key + " is a disabled default");
            Assert.False(string.IsNullOrWhiteSpace(TreeMenu.Header(entry.Key, Cases[name].Row)), entry.Key + " has no label");
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_first_group_is_the_menu_up_to_its_first_divider_on_checkout_rows_only(string name)
    {
        var row = Cases[name].Row;
        var menu = TreeMenu.Build(row);
        var first = TreeMenu.FirstGroup(row);
        if (row.Kind is TreeRowKind.RootCheckout or TreeRowKind.Workspace)
        {
            Assert.Equal(menu.TakeWhile(static entry => !entry.IsSeparator), first);
        }
        else
        {
            Assert.Empty(first);
        }
    }

    [Fact]
    public void A_busy_holder_names_itself_and_its_reasons_on_every_entry_it_blocks()
    {
        var row = Fork() with { SessionTitle = "X", Busy = ["turn 3", "2 queued"] };
        var reason = TreeMenuStrings.Busy("X", row.Busy);
        var menu = TreeMenu.Build(row);

        foreach (var key in new[] { TreeMenuKey.ReplaceSession, TreeMenuKey.MergeBack, TreeMenuKey.Sync, TreeMenuKey.RenameBranch, TreeMenuKey.RemoveWorkspace })
        {
            Assert.Equal(reason, Entry(menu, key).Reason);
        }

        Assert.Null(Entry(menu, TreeMenuKey.Fork).Reason);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Handing_over_beats_an_operation_beats_a_conflict_as_the_reason(bool handingOver, bool operating)
    {
        // The workspace is always conflicted here: the stronger states must win over it as the reason shown.
        var row = Linked() with { SessionTitle = "X", HandingOver = handingOver, Operating = operating, Conflicted = true };
        var expected = handingOver
            ? HandoverStrings.BusyHandingOver
            : operating ? HandoverStrings.BusyWorkspaceOperating : HandoverStrings.BusyMergeConflict;
        var menu = TreeMenu.Build(row);

        Assert.Equal(expected, Entry(menu, TreeMenuKey.ReplaceSession).Reason);
        Assert.Equal(expected, Entry(menu, TreeMenuKey.Fork).Reason);
        Assert.Equal(expected, Entry(menu, TreeMenuKey.RemoveWorkspace).Reason);
    }

    [Theory]
    [InlineData(GitHeadKind.Detached)]
    [InlineData(GitHeadKind.UnbornBranch)]
    public void A_head_off_a_branch_warns_on_new_and_replace_and_names_itself_on_fork(GitHeadKind head)
    {
        var empty = TreeMenu.Build(Linked() with { Head = head });
        var held = TreeMenu.Build(Root() with { SessionTitle = "X", Head = head });

        Assert.Equal(TreeMenuStrings.HeadWarning, Entry(empty, TreeMenuKey.NewSession).Warning);
        Assert.True(Entry(empty, TreeMenuKey.NewSession).IsEnabled);
        Assert.Equal(TreeMenuStrings.HeadWarning, Entry(held, TreeMenuKey.ReplaceSession).Warning);
        Assert.Equal(TreeMenuStrings.HeadNotOnBranch, Entry(held, TreeMenuKey.Fork).Reason);
    }

    [Fact]
    public void A_missing_directory_and_a_merge_say_so()
    {
        var missing = TreeMenu.Build(Linked() with { SessionTitle = "X", DirectoryMissing = true });
        var merging = TreeMenu.Build(Linked() with { MergeOrRebase = true });
        var history = TreeMenu.Build(new TreeRowState(TreeRowKind.HistorySession) { DirectoryMissing = true });

        Assert.Equal(LifecycleStrings.DirectoryMissing, Entry(missing, TreeMenuKey.ReplaceSession).Reason);
        Assert.Equal(LifecycleStrings.DirectoryMissing, Entry(missing, TreeMenuKey.Reveal).Reason);
        Assert.Equal(TreeMenuStrings.MergeOrRebase, Entry(merging, TreeMenuKey.RenameBranch).Reason);
        Assert.Equal(LifecycleStrings.DirectoryMissing, Entry(history, TreeMenuKey.Continue).Reason);
    }

    [Fact]
    public void A_stale_worktree_says_why_and_a_missing_directory_wins_over_it()
    {
        var prunable = LifecycleStrings.WorktreePrunable("gitdir file points to non-existent location");
        var stale = TreeMenu.Build(Linked() with { SessionTitle = "X", StaleReason = prunable });
        var both = TreeMenu.Build(Linked() with { SessionTitle = "X", DirectoryMissing = true, StaleReason = prunable });

        Assert.Equal(prunable, Entry(stale, TreeMenuKey.ReplaceSession).Reason);
        Assert.Equal(prunable, Entry(stale, TreeMenuKey.Fork).Reason);
        Assert.Contains("gitdir file points to non-existent location", prunable, StringComparison.Ordinal);
        Assert.Equal(LifecycleStrings.DirectoryMissing, Entry(both, TreeMenuKey.ReplaceSession).Reason);
        Assert.NotEqual(LifecycleStrings.WorktreePrunable(null), prunable);

        var staleHistory = TreeMenu.Build(new TreeRowState(TreeRowKind.HistorySession) { StaleReason = LifecycleStrings.WorktreeUnregistered });
        var missingHistory = TreeMenu.Build(new TreeRowState(TreeRowKind.HistorySession) { DirectoryMissing = true, StaleReason = prunable });
        var healthyHistory = TreeMenu.Build(new TreeRowState(TreeRowKind.HistorySession));
        Assert.Equal(LifecycleStrings.WorktreeUnregistered, Entry(staleHistory, TreeMenuKey.Continue).Reason);
        Assert.Equal(LifecycleStrings.DirectoryMissing, Entry(missingHistory, TreeMenuKey.Continue).Reason);
        Assert.Null(Entry(healthyHistory, TreeMenuKey.Continue).Reason);
        Assert.Null(Entry(staleHistory, TreeMenuKey.RemoveSession).Reason);
    }

    [Fact]
    public void A_menu_item_runs_only_when_generated_enabled_and_backed_by_a_command()
    {
        var command = new CountingCommand();
        var enabled = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.Reveal), "reveal", command);
        var disabled = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.Reveal, false, "why"), "reveal", command);
        var orphan = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.Reveal), "reveal", null);
        var refused = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.Reveal), "reveal", new CountingCommand { Allowed = false });
        var divider = new TreeMenuItemViewModel(TreeMenuEntry.Divider, "ignored", command);

        Assert.True(enabled.Run());
        Assert.False(disabled.Run());
        Assert.False(orphan.Run());
        Assert.False(refused.Run());
        Assert.False(divider.Run());
        Assert.Equal(1, command.Runs);

        Assert.Equal("why", disabled.Tip);
        Assert.True(disabled.HasTip);
        Assert.False(orphan.IsEnabled);
        Assert.Equal(TreeMenuStrings.Unavailable, orphan.Tip);
        Assert.False(refused.IsEnabled);
        Assert.Equal(TreeMenuStrings.Unavailable, refused.Tip);
        Assert.True(enabled.IsEnabled);
        Assert.Null(enabled.Tip);
        Assert.Equal("-", divider.Header);
        Assert.True(divider.IsSeparator);
        Assert.Null(divider.Command);
        Assert.Null(divider.Tip);
    }

    [Fact]
    public void An_enabled_item_shows_its_warning_and_a_disabled_one_its_reason()
    {
        var warned = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.NewSession) { Warning = "careful" }, "new", new CountingCommand());
        var blocked = new TreeMenuItemViewModel(new TreeMenuEntry(TreeMenuKey.NewSession, false, "busy") { Warning = "careful" }, "new", new CountingCommand());

        Assert.Equal("careful", warned.Tip);
        Assert.Equal("busy", blocked.Tip);
    }

    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("main", "origin/main", true)]
    [InlineData("feature/main", "origin/main", false)]
    [InlineData("feature", "origin/main", false)]
    [InlineData("main", null, false)]
    [InlineData(null, "main", false)]
    [InlineData("", "main", false)]
    [InlineData("main", "/main", false)]
    public void The_default_branch_matches_its_local_or_remote_tracking_name(string? branch, string? defaultBranch, bool expected) =>
        Assert.Equal(expected, ProjectTreeViewModel.IsDefaultBranchName(branch, defaultBranch));

    private static TreeRowState Root() =>
        new(TreeRowKind.RootCheckout) { InRepository = true, Head = GitHeadKind.Branch, HasBranch = true };

    private static TreeRowState Linked() =>
        new(TreeRowKind.Workspace) { InRepository = true, Head = GitHeadKind.Branch, HasBranch = true };

    private static TreeRowState Fork() => Linked() with { ForkOfBranch = "main" };

    private static TreeRowState Active() =>
        new(TreeRowKind.ActiveSession) { InRepository = true, SessionTitle = "S" };

    private static TreeMenuEntry Entry(IReadOnlyList<TreeMenuEntry> menu, TreeMenuKey key) =>
        Assert.Single(menu, entry => entry.Key == key);

    internal static string Shape(IEnumerable<TreeMenuEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(entry.IsSeparator ? "|" : (entry.IsDefault ? "*" : entry.IsEnabled ? string.Empty : "!") + entry.Key);
        }

        return text.ToString();
    }

    private sealed class CountingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool Allowed { get; init; } = true;

        public int Runs { get; private set; }

        public bool CanExecute(object? parameter) => Allowed;

        public void Execute(object? parameter) => Runs++;
    }
}
