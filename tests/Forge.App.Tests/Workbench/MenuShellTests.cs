using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The tree menus' shell flows (A5, TASK-session-worktree-lifecycle 7.8-7.10) against a real composition root and a real
/// repository. Rename branch: git and the catalog renamed together; a store failure (an injected SQLite trigger) renames
/// the branch back and says so; the upstream warning and its cancel; invalid / taken names; the busy refusal before and
/// after the dialogs; a running merge seen only at execution time. Link existing workspace: the root and every directory
/// a session (removed ones included) points at are left out, a locked worktree is greyed out with its reason, the chosen
/// one gets an Active session on its branch, none left is a notice, a detached HEAD needs the warning, and a session that
/// takes the directory while the dialog is open ends in the occupied dialog. Sparse checkout: the dialog lists the start
/// point's top-level directories and needs one chosen; the created worktree holds only the chosen ones.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MenuShellTests
{
    // ---------------------------------------------------------------- 7.8 rename branch

    [Fact]
    public async Task Rename_branch_renames_the_git_branch_and_the_records_without_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue("  feature-b  ");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Equal([TreeMenuStrings.RenameBranchTitle], harness.Dialogs.Prompts.Select(static p => p.Title));
                Assert.Equal("feature-a", Assert.Single(harness.Dialogs.Prompts).Initial);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.True(harness.BranchExists("feature-b"));
                Assert.False(harness.BranchExists("feature-a"));
                Assert.Equal("feature-b", (await harness.InfoAsync(session.Id)).Branch);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_store_failure_renames_the_branch_back_and_says_so()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.InjectFailure(Harness.BranchUpdateTrigger);
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(TreeMenuStrings.RenameBranchTitle, notice.Title);
                var shape = TreeMenuStrings.RenameBranchRolledBack("feature-a", "").Split('');
                Assert.StartsWith(shape[0], notice.Message, StringComparison.Ordinal);
                Assert.EndsWith(shape[1], notice.Message, StringComparison.Ordinal);
                Assert.Contains(Harness.InjectedFailure, notice.Message, StringComparison.Ordinal);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.BranchExists("feature-b"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task An_upstream_is_warned_about_first_and_cancelling_the_warning_changes_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Git(harness.Repository, "config", "branch.feature-a.remote", "origin");
                harness.Git(harness.Repository, "config", "branch.feature-a.merge", "refs/heads/feature-a");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                var warning = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal(TreeMenuStrings.RenameBranchUpstream("feature-a", "origin"), warning.Message);
                Assert.Equal([TreeMenuStrings.RenameBranchContinue, ShellStrings.Cancel], warning.Options);
                Assert.Empty(harness.Dialogs.Prompts);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.True(harness.BranchExists("feature-a"));
            });
    }

    [Fact]
    public async Task Continuing_past_the_upstream_warning_renames()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Git(harness.Repository, "config", "branch.feature-a.remote", "origin");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Single(harness.Dialogs.Choices);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.True(harness.BranchExists("feature-b"));
                Assert.Equal("feature-b", (await harness.InfoAsync(session.Id)).Branch);
            });
    }

    [Theory]
    [InlineData("bad..name", false)]
    [InlineData("main", true)]
    public async Task An_invalid_or_taken_name_is_refused_with_a_notice(string name, bool taken)
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue(name);

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                var expected = taken ? TreeMenuStrings.RenameBranchTaken(name) : TreeMenuStrings.RenameBranchInvalid(name);
                Assert.Equal(expected, Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("feature-a")]
    public async Task An_empty_or_unchanged_name_does_nothing(string name)
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue(name);

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Single(harness.Dialogs.Prompts);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task The_default_branch_is_refused_from_a_live_read_even_when_the_menu_offered_the_entry()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Git(harness.Repository, "update-ref", "refs/remotes/origin/feature-a", "feature-a");
                harness.Git(harness.Repository, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/feature-a");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Equal(TreeMenuStrings.RenameBranchDefault("feature-a"), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.Empty(harness.Dialogs.Prompts);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
            });
    }

    [Fact]
    public async Task A_failed_upstream_read_is_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");

                // A leading '-' is refused by the git layer before any process runs: the read throws.
                await harness.Shell.RenameBranchAsync(harness.Target(directory, "-x"), CancellationToken.None);

                AssertFailedNotice(harness, TreeMenuStrings.RenameBranchFailed);
                Assert.Empty(harness.Dialogs.Prompts);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_failed_name_check_is_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");
                harness.Dialogs.OnPrompt = () => GitTestFiles.DeleteTree(directory);

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                AssertFailedNotice(harness, TreeMenuStrings.RenameBranchFailed);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_failed_git_rename_is_a_notice_and_changes_no_record()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");
                // A stale lock on the new ref: the name is free and valid, but git cannot write the ref.
                var lockFile = Path.Combine(harness.Repository, ".git", "refs", "heads", "feature-b.lock");
                harness.Dialogs.OnPrompt = () => File.WriteAllText(lockFile, string.Empty);

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                AssertFailedNotice(harness, TreeMenuStrings.RenameBranchFailed);
                File.Delete(lockFile);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.BranchExists("feature-b"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_store_failure_whose_rollback_also_fails_names_both_branches_and_both_errors()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.InjectFailure(Harness.BranchUpdateTrigger);
                harness.BlockRefUpdates("feature-b");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                var message = Assert.Single(harness.Dialogs.Notices).Message;
                var shape = TreeMenuStrings.RenameBranchRollbackFailed("feature-a", "feature-b", "\u0001", "\u0001").Split('\u0001');
                Assert.StartsWith(shape[0], message, StringComparison.Ordinal);
                Assert.Contains(shape[1], message, StringComparison.Ordinal);
                Assert.EndsWith(shape[2], message, StringComparison.Ordinal);
                Assert.Contains(Harness.InjectedFailure, message, StringComparison.Ordinal);
                Assert.True(harness.BranchExists("feature-b"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_cancelled_rename_asks_nothing_and_holds_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), cancelled.Token));

                Assert.Empty(harness.Dialogs.Prompts);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_window_without_dialogs_reports_rename_and_link_in_the_status_bar()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                using var bare = new MainWindowViewModel(harness.Composition);

                await bare.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);
                Assert.Equal(RemovalStrings.Unavailable, bare.Status.HostDetail);

                bare.Status.SetWorkspaceProblem(LocText.Literal(string.Empty));
                await bare.LinkWorkspaceAsync(harness.Project, CancellationToken.None);
                Assert.Equal(RemovalStrings.Unavailable, bare.Status.HostDetail);
                Assert.True(harness.BranchExists("feature-a"));
            });
    }

    [Fact]
    public async Task A_busy_workspace_is_refused_before_any_dialog()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                Assert.True(harness.Composition.WorkspaceStates.TryBeginOperation(directory));
                try
                {
                    await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);
                }
                finally
                {
                    harness.Composition.WorkspaceStates.EndOperation(directory);
                }

                Assert.Equal(
                    TreeMenuStrings.RenameBranchBusy([HandoverStrings.BusyWorkspaceOperating]),
                    Assert.Single(harness.Dialogs.Notices).Message);
                Assert.Empty(harness.Dialogs.Prompts);
                Assert.True(harness.BranchExists("feature-a"));
            });
    }

    [Fact]
    public async Task A_workspace_that_turns_busy_while_the_name_is_typed_is_refused()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");
                harness.Dialogs.OnPrompt = () => Assert.True(harness.Composition.WorkspaceStates.TryBeginOperation(directory));
                try
                {
                    await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);
                }
                finally
                {
                    harness.Composition.WorkspaceStates.EndOperation(directory);
                }

                Assert.Equal(
                    TreeMenuStrings.RenameBranchBusy([HandoverStrings.BusyWorkspaceOperating]),
                    Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.BranchExists("feature-b"));
            });
    }

    [Fact]
    public async Task A_running_merge_found_at_execution_refuses_the_rename()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.CommitIn(directory, "clash.txt", "fork side\n", "fork side");
                harness.CommitIn(harness.Repository, "clash.txt", "main side\n", "main side");
                // The merge stops on the conflict (git exits non-zero), leaving MERGE_HEAD behind.
                Assert.ThrowsAny<Exception>(() => harness.Git(directory, "merge", "main"));
                Assert.True(File.Exists(Path.Combine(harness.GitDirectory(directory), "MERGE_HEAD")));
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Equal(
                    TreeMenuStrings.RenameBranchBusy([TreeMenuStrings.MergeOrRebase]),
                    Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_branch_switched_away_meanwhile_is_reported_as_moved()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.PromptAnswers.Enqueue("feature-b");
                harness.Dialogs.OnPrompt = () => harness.Git(directory, "switch", "-q", "-c", "elsewhere");

                await harness.Shell.RenameBranchAsync(harness.Target(directory, "feature-a"), CancellationToken.None);

                Assert.Equal(TreeMenuStrings.RenameBranchMoved("feature-a"), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(harness.BranchExists("feature-a"));
                Assert.False(harness.BranchExists("feature-b"));
                Assert.Equal("feature-a", (await harness.InfoAsync(session.Id)).Branch);
            });
    }

    // ---------------------------------------------------------------- 7.10 link existing workspace

    [Fact]
    public async Task Link_lists_only_unused_worktrees_and_greys_out_a_locked_one_with_its_reason()
    {
        await RunAsync(
            async harness =>
            {
                var free = harness.AddBareWorktree("wt-free", "free");
                var locked = harness.AddBareWorktree("wt-locked", "locked");
                harness.Git(harness.Repository, "worktree", "lock", "--reason", "on usb", locked);
                await harness.AddWorktreeAsync("wt-used", "used", "used");
                var (removed, removedSession) = await harness.AddWorktreeAsync("wt-removed", "removed", "removed");
                Assert.True(await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(
                    removedSession.Id, SessionStatus.Deleted, CancellationToken.None)));

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                var pick = Assert.Single(harness.Dialogs.Picks);
                Assert.Equal(TreeMenuStrings.LinkTitle, pick.Title);
                Assert.Equal(TreeMenuStrings.LinkMessage, pick.Message);
                Assert.Equal(2, pick.Entries.Count);
                var lockedIndex = pick.Entries.ToList().FindIndex(e => e.Label.StartsWith(Path.GetFullPath(locked), StringComparison.OrdinalIgnoreCase));
                var freeIndex = 1 - lockedIndex;
                Assert.Equal(TreeMenuStrings.LinkLocked("on usb"), pick.Entries[lockedIndex].DisabledReason);
                Assert.Equal(Path.GetFullPath(free) + "  ·  free", pick.Entries[freeIndex].Label);
                Assert.Null(pick.Entries[freeIndex].DisabledReason);
                Assert.DoesNotContain(pick.Entries, e => e.Label.Contains("wt-removed", StringComparison.OrdinalIgnoreCase));

                // Cancelled: nothing is linked.
                Assert.Null(await harness.FindActiveSessionInAsync(free));
                Assert.Null(await harness.FindActiveSessionInAsync(locked));
                Assert.Null(await harness.FindActiveSessionInAsync(removed));
                Assert.Empty(harness.Dialogs.Notices);
            });
    }

    [Fact]
    public async Task Linking_the_free_worktree_creates_an_active_session_on_its_branch_and_opens_it()
    {
        await RunAsync(
            async harness =>
            {
                var free = harness.AddBareWorktree("wt-free", "free");
                harness.Dialogs.PickAnswers.Enqueue(0);

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                var linked = await harness.ActiveSessionInAsync(free);
                Assert.Equal("free", linked.Branch);
                Assert.Equal(harness.Project.Id, linked.ProjectId);
                Assert.Equal(linked.Id, harness.ActiveView().SessionId);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Empty(harness.Dialogs.Confirms);
            });
    }

    [Fact]
    public async Task Picking_a_locked_worktree_links_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var locked = harness.AddBareWorktree("wt-locked", "locked");
                harness.Git(harness.Repository, "worktree", "lock", locked);
                harness.Dialogs.PickAnswers.Enqueue(0);

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.Equal(TreeMenuStrings.LinkLocked(null), Assert.Single(Assert.Single(harness.Dialogs.Picks).Entries).DisabledReason);
                Assert.Null(await harness.FindActiveSessionInAsync(locked));
            });
    }

    [Fact]
    public async Task No_unused_worktree_is_a_notice_without_a_picker()
    {
        await RunAsync(
            async harness =>
            {
                await harness.AddWorktreeAsync("wt-used", "used", "used");

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Picks);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(TreeMenuStrings.LinkTitle, notice.Title);
                Assert.Equal(TreeMenuStrings.LinkNone, notice.Message);
            });
    }

    [Fact]
    public async Task A_detached_worktree_needs_the_warning_and_links_without_a_branch()
    {
        await RunAsync(
            async harness =>
            {
                var detached = Path.Combine(harness.SandboxRoot, "wt-detached");
                harness.Git(harness.Repository, "worktree", "add", "-q", "--detach", detached, "main");
                harness.Dialogs.PickAnswers.Enqueue(0);

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.Equal(
                    Path.GetFullPath(detached) + "  ·  " + TreeMenuStrings.LinkDetachedLabel,
                    Assert.Single(Assert.Single(harness.Dialogs.Picks).Entries).Label);
                Assert.Equal(TreeMenuStrings.LinkDetachedWarning(Path.GetFullPath(detached)), Assert.Single(harness.Dialogs.Confirms).Message);
                Assert.Null(await harness.FindActiveSessionInAsync(detached));

                harness.Dialogs.PickAnswers.Enqueue(0);
                harness.Dialogs.ConfirmAnswers.Enqueue(true);
                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                var linked = await harness.ActiveSessionInAsync(detached);
                Assert.Null(linked.Branch);
                Assert.Empty(harness.Dialogs.Notices);
            });
    }

    [Fact]
    public async Task A_session_that_takes_the_directory_while_the_picker_is_open_ends_in_the_occupied_dialog()
    {
        await RunAsync(
            async harness =>
            {
                var free = harness.AddBareWorktree("wt-free", "free");
                harness.Dialogs.PickAnswers.Enqueue(0);
                SessionInfo? rival = null;
                harness.Dialogs.OnPick = async () => rival = await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(Path.GetFullPath(free), "rival")
                    {
                        ProjectId = harness.Project.Id,
                        WorktreePath = Path.GetFullPath(free),
                        Branch = "free",
                    },
                    CancellationToken.None));

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.NotNull(rival);
                var occupied = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal(LifecycleStrings.OccupiedTitle, occupied.Title);
                Assert.Equal(HandoverStrings.OccupiedQuestion("rival"), occupied.Message);
                Assert.Equal(rival!.Id, (await harness.ActiveSessionInAsync(free)).Id);
            });
    }

    [Fact]
    public async Task A_failed_worktree_listing_is_a_notice()
    {
        await RunAsync(
            async harness =>
            {
                var gone = harness.Project with { RootPath = Path.Combine(harness.Root, "no-such-repository") };

                await harness.Shell.LinkWorkspaceAsync(gone, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Picks);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(TreeMenuStrings.LinkTitle, notice.Title);
                AssertFailedNotice(harness, TreeMenuStrings.LinkFailed);
            });
    }

    [Fact]
    public async Task A_failed_insert_is_a_notice_and_opens_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var free = harness.AddBareWorktree("wt-free", "free");
                harness.InjectFailure(Harness.InsertTrigger);
                harness.Dialogs.PickAnswers.Enqueue(0);
                var before = harness.ActiveView().SessionId;

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                AssertFailedNotice(harness, TreeMenuStrings.LinkFailed);
                Assert.Contains(Harness.InjectedFailure, harness.Dialogs.Notices[0].Message, StringComparison.Ordinal);
                Assert.Null(await harness.FindActiveSessionInAsync(free));
                Assert.Equal(before, harness.ActiveView().SessionId);
            });
    }

    [Fact]
    public async Task An_out_of_range_pick_links_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var free = harness.AddBareWorktree("wt-free", "free");
                harness.Dialogs.PickAnswers.Enqueue(5);

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.Null(await harness.FindActiveSessionInAsync(free));
                Assert.Empty(harness.Dialogs.Notices);
            });
    }

    [Fact]
    public async Task A_directory_another_project_uses_is_offered_but_ends_in_the_occupied_dialog()
    {
        await RunAsync(
            async harness =>
            {
                var free = Path.GetFullPath(harness.AddBareWorktree("wt-free", "free"));
                var other = await Task.Run(() => harness.Composition.Projects.AddAsync(free, "other", ProjectKind.Repository, CancellationToken.None));
                var rival = await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(free, "rival") { ProjectId = other.Id, WorktreePath = free, Branch = "free" },
                    CancellationToken.None));
                harness.Dialogs.PickAnswers.Enqueue(0);

                await harness.Shell.LinkWorkspaceAsync(harness.Project, CancellationToken.None);

                Assert.Single(Assert.Single(harness.Dialogs.Picks).Entries);
                var occupied = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal(LifecycleStrings.OccupiedTitle, occupied.Title);
                Assert.Equal(HandoverStrings.OccupiedQuestion("rival"), occupied.Message);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(rival.Id)).Status);
                Assert.Null(await harness.FindActiveSessionInAsync(free));
            });
    }

    // ---------------------------------------------------------------- 7.9 sparse checkout

    [Fact]
    public async Task The_new_workspace_dialog_lists_top_level_directories_and_needs_one_chosen_for_a_sparse_checkout()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                harness.CommitIn(harness.Repository, "beta/b.txt", "b\n", "beta");
                using var dialog = new NewBranchDialogViewModel(
                    harness.Composition.Git,
                    new NewBranchRequest(harness.Repository, "sparse-a", OfferSparse: true),
                    CancellationToken.None);
                await dialog.InitializeAsync();
                dialog.Picker.SelectedBranch = Assert.Single(dialog.Picker.LocalBranches, static b => b.Name == "main");
                await WaitUntilAsync(() => dialog.CanCreate, "Create to become available");
                Assert.True(dialog.OfferSparse);
                Assert.Null(dialog.Result!.Sparse);

                dialog.IsSparse = true;
                Assert.False(dialog.CanCreate);
                await WaitUntilAsync(() => dialog.SparseDirectories.Count > 0, "the directories to be listed");
                Assert.Contains("alpha", dialog.SparseDirectories.Select(static d => d.Name));
                Assert.Contains("beta", dialog.SparseDirectories.Select(static d => d.Name));
                Assert.False(dialog.CanCreate);
                Assert.Equal(TreeMenuStrings.SparseNoneSelected, dialog.Hint);

                dialog.SparseDirectories.Single(static d => d.Name == "alpha").IsSelected = true;
                Assert.True(dialog.CanCreate);
                Assert.Equal(["alpha"], dialog.Result!.Sparse!.Directories);

                dialog.IsSparse = false;
                Assert.Empty(dialog.SparseDirectories);
                Assert.Null(dialog.Result!.Sparse);
            });
    }

    [Fact]
    public async Task The_sparse_option_is_not_offered_unless_asked_for()
    {
        await RunAsync(
            async harness =>
            {
                using var dialog = new NewBranchDialogViewModel(
                    harness.Composition.Git,
                    new NewBranchRequest(harness.Repository, "plain"),
                    CancellationToken.None);
                await dialog.InitializeAsync();

                dialog.IsSparse = true;

                Assert.False(dialog.OfferSparse);
                Assert.False(dialog.IsSparse);
            });
    }

    [Fact]
    public async Task A_sparse_workspace_holds_only_the_chosen_directories_and_leaves_the_main_checkout_alone()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                harness.CommitIn(harness.Repository, "beta/b.txt", "b\n", "beta");

                var created = await Task.Run(() => harness.Composition.Worktrees.CreateAsync(
                    harness.Repository, "sparse", "sparse-a", "main", new SparseCheckout(["alpha"]), CancellationToken.None));

                Assert.True(File.Exists(Path.Combine(created.Path, "alpha", "a.txt")));
                Assert.False(Directory.Exists(Path.Combine(created.Path, "beta")));
                Assert.True(File.Exists(Path.Combine(harness.Repository, "beta", "b.txt")));
                Assert.DoesNotContain(
                    "core.sparsecheckout=true",
                    harness.Git(harness.Repository, "config", "--local", "--list"),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(
                    ["alpha"],
                    (await Task.Run(() => harness.Composition.Git.GetSparseConfigAsync(created.Path, CancellationToken.None)))!.Directories);
            });
    }

    [Fact]
    public async Task A_start_point_that_cannot_be_listed_shows_the_failure_and_blocks_create()
    {
        await RunAsync(
            async harness =>
            {
                harness.Git(harness.Repository, "branch", "gone", "main");
                using var dialog = await OpenSparseDialogAsync(harness, "gone");
                harness.Git(harness.Repository, "branch", "-D", "gone");

                dialog.IsSparse = true;
                await WaitUntilAsync(
                    () => dialog.Hint is { } hint && hint.StartsWith(TreeMenuStrings.SparseLoadFailed(string.Empty), StringComparison.Ordinal),
                    "the listing failure hint");

                Assert.False(dialog.CanCreate);
                Assert.Null(dialog.Result);
                Assert.Empty(dialog.SparseDirectories);
            });
    }

    [Fact]
    public async Task A_start_point_without_directories_says_so_and_blocks_create()
    {
        await RunAsync(
            async harness =>
            {
                harness.AddEmptyBranch("empty");
                using var dialog = await OpenSparseDialogAsync(harness, "empty");

                dialog.IsSparse = true;
                await WaitUntilAsync(() => dialog.Hint == TreeMenuStrings.SparseNoDirectories, "the no-directories hint");

                Assert.False(dialog.CanCreate);
                Assert.Empty(dialog.SparseDirectories);
            });
    }

    [Fact]
    public async Task Changing_the_start_point_while_listing_drops_the_stale_answer()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                harness.AddEmptyBranch("empty");
                using var dialog = await OpenSparseDialogAsync(harness, "main");

                dialog.IsSparse = true;
                dialog.Picker.SelectedBranch = Assert.Single(dialog.Picker.LocalBranches, static b => b.Name == "empty");
                await WaitUntilAsync(() => dialog.Hint == TreeMenuStrings.SparseNoDirectories, "the empty start point's answer");
                await Task.Delay(300);

                Assert.Empty(dialog.SparseDirectories);
                Assert.Equal(TreeMenuStrings.SparseNoDirectories, dialog.Hint);
            });
    }

    [Fact]
    public async Task Turning_the_option_off_or_closing_the_dialog_while_listing_keeps_nothing()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                using var toggled = await OpenSparseDialogAsync(harness, "main");
                var closed = await OpenSparseDialogAsync(harness, "main");

                toggled.IsSparse = true;
                toggled.IsSparse = false;
                closed.IsSparse = true;
                closed.Dispose();

                // No fixed sleep: wait for the name validation, then for a control dialog's listing of the same start
                // point. Once that answer is in, the two dropped listings (started earlier) have had their chance to land.
                await WaitUntilAsync(() => toggled.CanCreate, "the branch name to validate");
                using var control = await OpenSparseDialogAsync(harness, "main");
                control.IsSparse = true;
                await WaitUntilAsync(() => control.SparseDirectories.Count > 0, "the control listing");

                Assert.Empty(toggled.SparseDirectories);
                Assert.Null(toggled.Hint);
                Assert.True(toggled.CanCreate);
                Assert.Null(toggled.Result!.Sparse);
                Assert.Empty(closed.SparseDirectories);
            });
    }

    [Fact]
    public async Task The_option_turned_on_before_a_start_point_lists_once_one_is_selected()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                using var dialog = new NewBranchDialogViewModel(
                    harness.Composition.Git,
                    new NewBranchRequest(harness.Repository, "sparse-early", OfferSparse: true),
                    CancellationToken.None);

                dialog.IsSparse = true;
                Assert.False(dialog.CanCreate);
                await dialog.InitializeAsync();
                if (dialog.Picker.SelectedBranch is null)
                {
                    dialog.Picker.SelectedBranch = Assert.Single(dialog.Picker.LocalBranches, static b => b.Name == "main");
                }

                await WaitUntilAsync(() => dialog.SparseDirectories.Count > 0, "the directories to be listed");
                Assert.Contains("alpha", dialog.SparseDirectories.Select(static d => d.Name));
            });
    }

    [Fact]
    public async Task The_project_plus_creates_the_sparse_workspace_the_dialog_chose()
    {
        await RunAsync(
            async harness =>
            {
                harness.CommitIn(harness.Repository, "alpha/a.txt", "a\n", "alpha");
                harness.CommitIn(harness.Repository, "beta/b.txt", "b\n", "beta");
                harness.Dialogs.NewBranchAnswer = new NewBranchChoice("main", "sparse-e2e", new SparseCheckout(["alpha"]));
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                Assert.True(harness.ProjectRow().TryRunPlus());
                SessionInfo? created = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (created is null)
                {
                    var sessions = await Task.Run(() => harness.Composition.Catalog.QueryAsync(
                        new SessionQuery { ProjectId = harness.Project.Id, Status = SessionStatus.Active },
                        CancellationToken.None));
                    created = sessions.FirstOrDefault(static s => s.Branch == "sparse-e2e");
                    if (created is null && DateTime.UtcNow > deadline)
                    {
                        Assert.Fail(
                            "No sparse workspace session; notices: " + string.Join(" | ", harness.Dialogs.Notices.Select(static n => n.Message))
                            + "; status: " + harness.Shell.Status.HostDetail);
                    }

                    await Task.Delay(50);
                }

                var path = created!.WorktreePath!;
                Assert.True(harness.Dialogs.NewBranchRequests.Single().OfferSparse);
                Assert.True(File.Exists(Path.Combine(path, "alpha", "a.txt")));
                Assert.False(Directory.Exists(Path.Combine(path, "beta")));
            });
    }

    private static async Task<NewBranchDialogViewModel> OpenSparseDialogAsync(Harness harness, string startPoint)
    {
        var dialog = new NewBranchDialogViewModel(
            harness.Composition.Git,
            new NewBranchRequest(harness.Repository, "sparse-" + Guid.NewGuid().ToString("N")[..6], OfferSparse: true),
            CancellationToken.None);
        await dialog.InitializeAsync();
        dialog.Picker.SelectedBranch = Assert.Single(dialog.Picker.LocalBranches, b => b.Name == startPoint);
        await WaitUntilAsync(() => dialog.CanCreate, "Create to become available");
        return dialog;
    }

    /// <summary>The single notice is <paramref name="format"/> of some reason: its fixed text around the reason matches.</summary>
    private static void AssertFailedNotice(Harness harness, Func<string, string> format)
    {
        var message = Assert.Single(harness.Dialogs.Notices).Message;
        var shape = format("\u0001").Split('\u0001');
        Assert.StartsWith(shape[0], message, StringComparison.Ordinal);
        Assert.EndsWith(shape[1], message, StringComparison.Ordinal);
        Assert.True(message.Length > shape[0].Length + shape[1].Length, "the notice carries the reason");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for " + what + ".");
            }

            await Task.Delay(20);
        }
    }

    private static async Task RunAsync(Func<Harness, Task> body)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync();
                try
                {
                    await body(harness);
                }
                finally
                {
                    await harness.DisposeAsync();
                }

                return 0;
            },
            CancellationToken.None);
    }

    private sealed record NotifyCall(string Title, string Message);

    private sealed record ChooseCall(string Title, string Message, IReadOnlyList<string> Options);

    private sealed record ConfirmCall(string Title, string Message, string ConfirmText);

    private sealed record PromptCall(string Title, string Initial);

    private sealed record PickCall(string Title, string Message, IReadOnlyList<PickEntry> Entries);

    /// <summary>Records every dialog; each kind is answered from its queue (closed / no / cancelled when empty).</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public Queue<int> ChooseAnswers { get; } = new();

        public Queue<bool> ConfirmAnswers { get; } = new();

        public Queue<string?> PromptAnswers { get; } = new();

        public Queue<int> PickAnswers { get; } = new();

        public List<ChooseCall> Choices { get; } = [];

        public List<ConfirmCall> Confirms { get; } = [];

        public List<PromptCall> Prompts { get; } = [];

        public List<PickCall> Picks { get; } = [];

        public List<NotifyCall> Notices { get; } = [];

        /// <summary>Runs while the text prompt is "open", to change the world under the flow.</summary>
        public Action? OnPrompt { get; set; }

        /// <summary>Runs while the picker is "open", to change the world under the flow.</summary>
        public Func<Task>? OnPick { get; set; }

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options)
        {
            Choices.Add(new ChooseCall(title, message, options));
            return Task.FromResult(ChooseAnswers.Count > 0 ? ChooseAnswers.Dequeue() : -1);
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
        {
            Confirms.Add(new ConfirmCall(title, message, confirmText));
            return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
        }

        public Task<string?> PromptTextAsync(string title, string initial)
        {
            Prompts.Add(new PromptCall(title, initial));
            OnPrompt?.Invoke();
            return Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null);
        }

        public async Task<int> PickAsync(string title, string message, IReadOnlyList<PickEntry> entries)
        {
            Picks.Add(new PickCall(title, message, entries));
            if (OnPick is { } onPick)
            {
                await onPick();
            }

            return PickAnswers.Count > 0 ? PickAnswers.Dequeue() : -1;
        }

        /// <summary>What the new-workspace dialog answers; null = cancelled.</summary>
        public NewBranchChoice? NewBranchAnswer { get; set; }

        public List<NewBranchRequest> NewBranchRequests { get; } = [];

        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct)
        {
            NewBranchRequests.Add(request);
            return Task.FromResult(NewBranchAnswer);
        }

        public Task NotifyAsync(string title, string message)
        {
            Notices.Add(new NotifyCall(title, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A throw-away data root, a real composition root, and a real clone registered as a repository project with one
    /// Active session in its root checkout, open as a live tab.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string InjectedFailure = "injected branch update failure";

        private readonly EnvironmentSnapshot previous;
        private readonly GitTestSandbox sandbox;

        private Harness(string root, EnvironmentSnapshot previous, GitTestSandbox sandbox, AppComposition composition, ProjectInfo project, MainWindowViewModel shell, RecordingDialogs dialogs)
        {
            Root = root;
            this.previous = previous;
            this.sandbox = sandbox;
            Composition = composition;
            Project = project;
            Shell = shell;
            Dialogs = dialogs;
        }

        public string Root { get; }

        public string SandboxRoot => sandbox.Root;

        public string Repository => Project.RootPath;

        public AppComposition Composition { get; }

        public ProjectInfo Project { get; }

        public MainWindowViewModel Shell { get; }

        public RecordingDialogs Dialogs { get; }

        public static async Task<Harness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-menu-shell-" + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(root, "data");
            var workspace = Path.Combine(root, "workspace");
            var general = Path.Combine(root, "general");
            foreach (var directory in new[] { data, workspace, general })
            {
                Directory.CreateDirectory(directory);
            }

            var previous = EnvironmentSnapshot.Capture();
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);

            var sandbox = GitTestSandbox.Create();
            var repository = sandbox.CloneTracked("repo");
            var composition = AppComposition.Create();
            var project = await Task.Run(() => composition.Projects.AddAsync(repository, "repo", ProjectKind.Repository, CancellationToken.None));
            var source = await Task.Run(() => composition.Catalog.CreateAsync(
                new SessionCreate(repository, "source") { ProjectId = project.Id },
                CancellationToken.None));

            var dialogs = new RecordingDialogs();
            var shell = new MainWindowViewModel(composition);
            shell.AttachDialogs(dialogs);
            await shell.InitializeAsync();
            var harness = new Harness(root, previous, sandbox, composition, project, shell, dialogs);
            await shell.OpenSessionByIdAsync(source.Id, CancellationToken.None);
            Assert.Equal(source.Id, harness.ActiveView().SessionId);
            return harness;
        }

        public WorkspaceTarget Target(string directory, string branch) => new(Project, directory, branch, null);

        /// <summary>A worktree on a new branch from <c>main</c> with one Active session in it.</summary>
        public async Task<(string Directory, SessionInfo Session)> AddWorktreeAsync(string name, string branch, string title)
        {
            var directory = AddBareWorktree(name, branch);
            var session = await Task.Run(() => Composition.Catalog.CreateAsync(
                new SessionCreate(directory, title)
                {
                    ProjectId = Project.Id,
                    WorktreePath = directory,
                    Branch = branch,
                },
                CancellationToken.None));
            await Shell.Projects.RefreshAsync(CancellationToken.None);
            return (directory, session);
        }

        /// <summary>A worktree on a new branch from <c>main</c> that no session points at.</summary>
        public string AddBareWorktree(string name, string branch)
        {
            var directory = Path.Combine(sandbox.Root, name);
            Git(Repository, "worktree", "add", "-q", "-b", branch, directory, "main");
            return directory;
        }

        /// <summary>Every later update of a session's <c>branch</c> column fails: a store failure mid-rename.</summary>
        public const string BranchUpdateTrigger =
            "CREATE TRIGGER fail_rename BEFORE UPDATE OF branch ON sessions BEGIN SELECT RAISE(ABORT, '" + InjectedFailure + "'); END;";

        /// <summary>Every later session insert fails: a store failure of the conditional insert.</summary>
        public const string InsertTrigger =
            "CREATE TRIGGER fail_insert BEFORE INSERT ON sessions BEGIN SELECT RAISE(ABORT, '" + InjectedFailure + "'); END;";

        /// <summary>Installs <paramref name="trigger"/> in the throw-away <c>forge.db</c>.</summary>
        public void InjectFailure(string trigger)
        {
            using (var connection = new SqliteConnection("Data Source=" + Composition.Paths.DatabasePath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = trigger;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
        }

        /// <summary>
        /// A <c>reference-transaction</c> hook that refuses every transaction touching <c>refs/heads/<paramref name="branch"/></c>.
        /// <c>git branch -m</c> deletes the old name in a transaction but writes the new one outside it, so the rename to
        /// <paramref name="branch"/> still goes through while the rename back from it (which deletes it) fails.
        /// </summary>
        public void BlockRefUpdates(string branch)
        {
            var hooks = Path.Combine(Repository, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            File.WriteAllText(
                Path.Combine(hooks, "reference-transaction"),
                "#!/bin/sh\n"
                + "[ \"$1\" = prepared ] || exit 0\n"
                + "while read old new ref; do\n"
                + "  [ \"$ref\" = refs/heads/" + branch + " ] && { echo 'blocked by the test hook' >&2; exit 1; }\n"
                + "done\n"
                + "exit 0\n");
        }

        /// <summary>A branch on a commit of the empty tree: a start point without any top-level directory.</summary>
        public void AddEmptyBranch(string branch)
        {
            var commit = Git(Repository, "commit-tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "-m", "empty").Trim();
            Git(Repository, "branch", branch, commit);
        }

        public TreeNodeViewModel ProjectRow() =>
            Assert.Single(Enumerate(Shell.Projects.Sections), node => node.Key == "project:" + Project.Id);

        private static IEnumerable<TreeNodeViewModel> Enumerate(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                foreach (var child in Enumerate(node.Children))
                {
                    yield return child;
                }
            }
        }

        public bool BranchExists(string branch) =>
            Git(Repository, "branch", "--list", branch).Trim().Length > 0;

        public string GitDirectory(string directory) =>
            Path.GetFullPath(Git(directory, "rev-parse", "--git-dir").Trim(), directory);

        public void CommitIn(string directory, string relativePath, string content, string message) =>
            sandbox.CommitFile(directory, relativePath, content, message);

        public string Git(string directory, params string[] arguments) => GitTestShell.RunChecked(directory, arguments);

        public SessionWorkspaceViewModel ActiveView() => Assert.IsType<SessionWorkspaceViewModel>(Shell.ActiveSession);

        public async Task<SessionInfo> InfoAsync(string sessionId) =>
            Assert.IsType<SessionInfo>(await Task.Run(() => Composition.Sessions.GetSessionAsync(sessionId, CancellationToken.None)));

        public async Task<SessionInfo?> FindActiveSessionInAsync(string directory)
        {
            var sessions = await Task.Run(() => Composition.Catalog.QueryAsync(
                new SessionQuery { ProjectId = Project.Id, Status = SessionStatus.Active },
                CancellationToken.None));
            return sessions.SingleOrDefault(session => session.WorktreePath is { } path
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase));
        }

        public async Task<SessionInfo> ActiveSessionInAsync(string directory) =>
            Assert.IsType<SessionInfo>(await FindActiveSessionInAsync(directory));

        public async ValueTask DisposeAsync()
        {
            Shell.Files.Attach(PanelContext.Empty);
            Shell.Dispose();
            await Composition.DisposeAsync();
            SqliteConnection.ClearAllPools();
            previous.Restore();
            sandbox.Dispose();
            GitTestFiles.DeleteTree(Root);
        }
    }

    private readonly record struct EnvironmentSnapshot(string DataRoot, string Workspace, string GeneralWorkspace)
    {
        public static EnvironmentSnapshot Capture() =>
            new(
                Environment.GetEnvironmentVariable(AppPaths.DataVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.GeneralVariable) ?? string.Empty);

        public void Restore()
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, DataRoot);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, GeneralWorkspace);
        }
    }
}
