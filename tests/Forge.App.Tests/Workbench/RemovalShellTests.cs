using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.IO;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Remove / restore / purge / close project (A4, TASK-session-worktree-lifecycle 7.2-7.7) through the shell, against a
/// real composition root and a real repository; the trash is a fake that moves into a temporary folder (or refuses), so
/// no test touches the real recycle bin. Covered: guard reasons shown verbatim and "remove anyway"; a session that is not
/// the last of its directory leaves it alone; the root checkout survives its last session; a missing directory is only
/// pruned and soft-deleted; a trash failure leaves the records untouched; the two-step restore failure; a branch gone;
/// the inheritance guard's heirs; purge cleanup; the busy refusal of close project and the re-add; the "archive X and
/// remove" second-step failure; the recycled workspace row; the merge-back removal with the source branch.
/// </summary>
[Collection("forge-app-ui")]
public sealed class RemovalShellTests
{
    private const string SourceTitle = "source";

    [Fact]
    public async Task Guard_reasons_are_shown_verbatim_and_remove_anyway_trashes_the_directory_but_keeps_the_branch()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                File.WriteAllText(Path.Combine(directory, "scratch.txt"), "unsaved work\n");
                var expected = await Task.Run(() => harness.Composition.Worktrees.Manager.CheckRemovableAsync(
                    harness.Repository, directory, null, CancellationToken.None));
                Assert.NotEmpty(expected);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal([RemovalStrings.RemoveAnyway, ShellStrings.Cancel], dialog.Options);
                Assert.Contains(RemovalStrings.SessionTrash(directory), dialog.Message, StringComparison.Ordinal);
                foreach (var reason in expected)
                {
                    Assert.Contains("• " + reason, dialog.Message, StringComparison.Ordinal);
                }

                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal([directory], harness.Trash.Moved);
                Assert.False(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);
                Assert.NotEmpty(harness.Git(harness.Repository, "rev-parse", "--verify", "refs/heads/feature-a").Trim());
                Assert.DoesNotContain(
                    Path.GetFileName(directory),
                    harness.Git(harness.Repository, "worktree", "list", "--porcelain"),
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task Cancelling_the_removal_dialog_changes_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                Assert.Equal([RemovalStrings.Remove, ShellStrings.Cancel], Assert.Single(harness.Dialogs.Choices).Options);
                Assert.Empty(harness.Trash.Moved);
                Assert.True(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(session.Id)).Status);
            });
    }

    [Fact]
    public async Task A_session_that_is_not_the_last_of_its_directory_leaves_the_directory_alone()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, active) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var archived = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Archived);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(archived, harness.Project, CancellationToken.None);

                Assert.Contains(RemovalStrings.SessionKeptShared, Assert.Single(harness.Dialogs.Choices).Message, StringComparison.Ordinal);
                Assert.Empty(harness.Trash.Moved);
                Assert.True(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(archived.Id)).Status);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(active.Id)).Status);
            });
    }

    [Fact]
    public async Task Removing_the_last_session_of_the_root_checkout_keeps_the_repository_root()
    {
        await RunAsync(
            async harness =>
            {
                var source = await harness.InfoAsync(harness.SourceId);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(source, harness.Project, CancellationToken.None);

                Assert.Contains(RemovalStrings.SessionKeptRoot, Assert.Single(harness.Dialogs.Choices).Message, StringComparison.Ordinal);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Empty(harness.Trash.Moved);
                Assert.True(Directory.Exists(harness.Repository));
                Assert.True(File.Exists(Path.Combine(harness.Repository, "file.txt")));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(harness.SourceId)).Status);
                Assert.DoesNotContain(harness.SourceId, harness.Shell.OpenTabIds);
            });
    }

    [Fact]
    public async Task A_missing_directory_is_only_pruned_and_soft_deleted()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                GitTestFiles.DeleteTree(directory);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Contains(RemovalStrings.SessionMissing, dialog.Message, StringComparison.Ordinal);
                Assert.Equal([RemovalStrings.Remove, ShellStrings.Cancel], dialog.Options);
                Assert.Empty(harness.Trash.Moved);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);
                Assert.DoesNotContain(
                    Path.GetFileName(directory),
                    harness.Git(harness.Repository, "worktree", "list", "--porcelain"),
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    [Fact]
    public async Task A_trash_failure_leaves_the_records_untouched_and_says_why()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Trash.Fail = true;
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(RemovalStrings.RemoveFailedTitle, notice.Title);
                Assert.Equal(RemovalStrings.TrashFailed(FakeTrash.Refusal), notice.Message);
                Assert.True(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(session.Id)).Status);
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task A_busy_session_is_not_removed_and_its_queue_is_kept()
    {
        await RunAsync(
            async harness =>
            {
                harness.SourceView.RestoreComposerState((string.Empty, ["queued one"]));

                await harness.Shell.RemoveSessionAsync(await harness.InfoAsync(harness.SourceId), harness.Project, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Choices);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.StartsWith(RemovalStrings.Busy(string.Empty), notice.Message, StringComparison.Ordinal);
                Assert.Contains(SourceTitle, notice.Message, StringComparison.Ordinal);
                Assert.True(harness.SourceView.HasQueued);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SourceId)).Status);
            });
    }

    [Fact]
    public async Task A_restore_that_rebuilt_the_directory_but_could_not_activate_says_so_and_a_retry_skips_the_rebuild()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);
                Assert.False(Directory.Exists(directory));

                // Another session took the workspace key meanwhile: the restore's activation ends in the occupied dialog,
                // which the user closes.
                var holder = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Active);
                harness.Dialogs.Choices.Clear();
                await harness.Shell.RestoreSessionAsync(await harness.InfoAsync(removed.Id), harness.Project, CancellationToken.None);

                Assert.True(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(removed.Id)).Status);
                Assert.Equal(LifecycleStrings.OccupiedTitle, Assert.Single(harness.Dialogs.Choices).Title);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(RemovalStrings.RestoreFailedTitle, notice.Title);
                Assert.Equal(RemovalStrings.NotActivatedCancelled, notice.Message);

                // The retry finds the directory: no rebuild, and with the holder archived the session is Active again.
                await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(holder.Id, SessionStatus.Archived, CancellationToken.None));
                harness.Dialogs.Notices.Clear();
                await harness.Shell.RestoreSessionAsync(await harness.InfoAsync(removed.Id), harness.Project, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(removed.Id)).Status);
                Assert.Equal(removed.Id, harness.ActiveView().SessionId);
            });
    }

    [Fact]
    public async Task A_restore_rebuilds_the_worktree_from_its_branch_and_says_the_sparse_settings_are_not_restored()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);

                await harness.Shell.RestoreSessionAsync(await harness.InfoAsync(removed.Id), harness.Project, CancellationToken.None);

                Assert.Equal(RemovalStrings.RebuiltWorktree(directory), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(File.Exists(Path.Combine(directory, "file.txt")));
                Assert.Equal("feature-a", harness.Git(directory, "branch", "--show-current").Trim());
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(removed.Id)).Status);
            });
    }

    [Fact]
    public async Task A_restore_whose_branch_is_gone_reports_it_and_the_session_stays_removed()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);
                harness.Git(harness.Repository, "branch", "-D", "feature-a");

                await harness.Shell.RestoreSessionAsync(await harness.InfoAsync(removed.Id), harness.Project, CancellationToken.None);

                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(RemovalStrings.RestoreFailedTitle, notice.Title);
                Assert.Equal(RemovalStrings.BranchGone("feature-a"), notice.Message);
                Assert.False(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(removed.Id)).Status);
            });
    }

    [Fact]
    public async Task A_purge_refused_by_the_inheritance_guard_lists_the_heirs_and_keeps_the_session()
    {
        await RunAsync(
            async harness =>
            {
                var (_, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);
                var heirDirectory = Path.Combine(harness.SandboxRoot, "wt-c");
                harness.Git(harness.Repository, "worktree", "add", "-q", "-b", "feature-c", heirDirectory, "main");
                await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(heirDirectory, "heir")
                    {
                        ProjectId = harness.Project.Id,
                        WorktreePath = heirDirectory,
                        Branch = "feature-c",
                        InheritsFromSessionId = removed.Id,
                    },
                    CancellationToken.None));
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.PurgeSessionAsync(await harness.InfoAsync(removed.Id), CancellationToken.None);

                var confirm = Assert.Single(harness.Dialogs.Confirms);
                Assert.Equal(RemovalStrings.PurgeQuestion("A"), confirm.Message);
                Assert.Equal(RemovalStrings.PurgeRefused("A", ["heir"]), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(removed.Id)).Status);
            });
    }

    [Fact]
    public async Task A_purge_whose_files_could_not_go_warns_and_the_next_start_cleans_them_up()
    {
        await RunAsync(
            async harness =>
            {
                var (_, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);
                // A file where the session's artifact folder belongs: the folder part of the cleanup fails.
                var artifacts = Path.Combine(harness.Composition.Paths.UserDataDirectory, "artifacts", removed.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(artifacts)!);
                File.WriteAllText(artifacts, "in the way\n");
                var journal = new PurgeCleanupJournal(Path.Combine(harness.Composition.Paths.UserDataDirectory, "purge-pending.txt"));
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.PurgeSessionAsync(await harness.InfoAsync(removed.Id), CancellationToken.None);

                Assert.Null(await Task.Run(() => harness.Composition.Sessions.GetSessionAsync(removed.Id, CancellationToken.None)));
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Contains(RemovalStrings.PurgePartial("\u0001").Split('\u0001')[0], notice.Message, StringComparison.Ordinal);
                Assert.Equal([removed.Id], await journal.ReadAsync(CancellationToken.None));

                // A start while it still fails says so and keeps it.
                await harness.Shell.RetryPurgeCleanupAsync();
                Assert.Contains(RemovalStrings.PurgeRetryFailed("\u0001").Split('\u0001')[0], harness.Shell.Status.HostDetail, StringComparison.Ordinal);
                Assert.Equal([removed.Id], await journal.ReadAsync(CancellationToken.None));

                // A start after the obstacle went: cleaned up, nothing pending.
                File.Delete(artifacts);
                Directory.CreateDirectory(artifacts);
                File.WriteAllText(Path.Combine(artifacts, "note.txt"), "artifact\n");
                await harness.Shell.RetryPurgeCleanupAsync();
                Assert.False(Directory.Exists(artifacts));
                Assert.Empty(await journal.ReadAsync(CancellationToken.None));
            });
    }

    [Fact]
    public async Task A_purge_deletes_the_records_and_the_artifact_folder()
    {
        await RunAsync(
            async harness =>
            {
                var (_, removed) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.Shell.RemoveSessionAsync(removed, harness.Project, CancellationToken.None);
                var artifacts = Path.Combine(harness.Composition.Paths.UserDataDirectory, "artifacts", removed.Id);
                Directory.CreateDirectory(artifacts);
                File.WriteAllText(Path.Combine(artifacts, "note.txt"), "artifact\n");
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.PurgeSessionAsync(await harness.InfoAsync(removed.Id), CancellationToken.None);

                Assert.Empty(harness.Dialogs.Notices);
                Assert.Null(await Task.Run(() => harness.Composition.Sessions.GetSessionAsync(removed.Id, CancellationToken.None)));
                Assert.False(Directory.Exists(artifacts));
            });
    }

    [Fact]
    public async Task Close_project_is_refused_while_a_session_is_busy_and_adding_the_path_again_brings_the_sessions_back()
    {
        await RunAsync(
            async harness =>
            {
                harness.SourceView.RestoreComposerState((string.Empty, ["queued one"]));
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.CloseProjectAsync(harness.Project, CancellationToken.None);

                Assert.Equal(RemovalStrings.CloseQuestion("repo"), Assert.Single(harness.Dialogs.Confirms).Message);
                var refused = Assert.Single(harness.Dialogs.Notices);
                Assert.StartsWith(RemovalStrings.CloseBusy(string.Empty), refused.Message, StringComparison.Ordinal);
                Assert.Contains(SourceTitle, refused.Message, StringComparison.Ordinal);
                Assert.Contains(harness.Project.Id, await harness.ProjectIdsAsync());
                Assert.True(harness.SourceView.HasQueued);

                // The user deletes the queued message (its own remove command); the session is idle again.
                Assert.Single(harness.SourceView.Queued).RemoveCommand.Execute(null);
                Assert.False(harness.SourceView.HasQueued);
                harness.Dialogs.Notices.Clear();
                harness.Dialogs.ConfirmAnswers.Enqueue(true);
                await harness.Shell.CloseProjectAsync(harness.Project, CancellationToken.None);

                Assert.Empty(harness.Dialogs.Notices.Select(static n => n.Message));
                Assert.DoesNotContain(harness.Project.Id, await harness.ProjectIdsAsync());
                Assert.DoesNotContain(harness.SourceId, harness.Shell.OpenTabIds);
                Assert.True(File.Exists(Path.Combine(harness.Repository, "file.txt")));

                var again = await Task.Run(() => harness.Composition.Projects.AddAsync(harness.Repository, "repo", ProjectKind.Repository, CancellationToken.None));
                Assert.Equal(harness.Project.Id, again.Id);
                Assert.Equal(SessionStatus.Active, (await harness.InfoAsync(harness.SourceId)).Status);
            });
    }

    [Fact]
    public async Task Close_project_is_refused_while_a_workspace_of_it_is_in_a_merge_conflict()
    {
        await RunAsync(
            async harness =>
            {
                harness.Composition.WorkspaceStates.SetConflicted(harness.Repository, true);
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.CloseProjectAsync(harness.Project, CancellationToken.None);

                var refused = Assert.Single(harness.Dialogs.Notices);
                Assert.StartsWith(RemovalStrings.CloseBusy(string.Empty), refused.Message, StringComparison.Ordinal);
                Assert.Contains(HandoverStrings.BusyMergeConflict, refused.Message, StringComparison.Ordinal);
                Assert.Contains(harness.Project.Id, await harness.ProjectIdsAsync());
                Assert.Contains(harness.SourceId, harness.Shell.OpenTabIds);
            });
    }

    [Fact]
    public async Task The_removed_session_leaves_the_window_before_its_directory_goes_to_the_trash()
    {
        await RunAsync(
            async harness =>
            {
                var (_, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                await harness.Shell.OpenSessionByIdAsync(session.Id, CancellationToken.None);
                Assert.Contains(session.Id, harness.Shell.OpenTabIds);
                bool? openAtTrash = null;
                harness.Trash.BeforeMove = _ => openAtTrash = harness.Shell.OpenTabIds.Contains(session.Id);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(await harness.InfoAsync(session.Id), harness.Project, CancellationToken.None);

                Assert.False(openAtTrash);
                Assert.DoesNotContain(session.Id, harness.Shell.OpenTabIds);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);
            });
    }

    [Fact]
    public async Task Archive_and_remove_keeps_the_archive_and_says_so_when_the_removal_fails()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, active) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                harness.Trash.Fail = true;
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.RemoveWorkspaceAsync(directory, "feature-a");

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal([RemovalStrings.ArchiveAndRemove("A"), ShellStrings.Cancel], dialog.Options);
                Assert.Contains(RemovalStrings.WorkspaceActive("A"), dialog.Message, StringComparison.Ordinal);
                var notice = Assert.Single(harness.Dialogs.Notices);
                Assert.Equal(RemovalStrings.ArchivedNotRemoved("A", RemovalStrings.TrashFailed(FakeTrash.Refusal)), notice.Message);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(active.Id)).Status);
                Assert.True(Directory.Exists(directory));
                Assert.False(harness.Composition.WorkspaceStates.IsOperating(directory));
            });
    }

    [Fact]
    public async Task Removing_a_workspace_removes_all_its_sessions_and_leaves_a_recycled_row_that_can_be_restored_or_purged()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, active) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var archived = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Archived);
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.RemoveWorkspaceAsync(directory, "feature-a");

                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal([directory], harness.Trash.Moved);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(active.Id)).Status);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(archived.Id)).Status);
                Assert.NotEmpty(harness.Git(harness.Repository, "rev-parse", "--verify", "refs/heads/feature-a").Trim());

                var row = harness.WorkspaceRow(directory);
                Assert.Equal(TreeRowMark.Recycled, row.RowMark);
                Assert.True(row.IsRecycled);
                Assert.Equal(RemovalStrings.RowRecycled, row.RowMarkText);
                Assert.True(row.HasRestore);
                Assert.True(row.HasPurge);
                Assert.False(row.HasRemove);
                Assert.False(row.HasFork);
                Assert.Null(row.NewSessionCommand);
                var removedRow = harness.SessionRow(active.Id);
                Assert.Equal(TreeRowMark.DirectoryRecycled, removedRow.RowMark);
                Assert.True(removedRow.HasRestore);
                Assert.True(removedRow.HasPurge);
                Assert.Null(removedRow.RenameCommand);

                // Restore workspace rebuilds the directory only; the sessions stay removed.
                await harness.Shell.RestoreWorkspaceAsync(new WorkspaceTarget(harness.Project, directory, "feature-a", null), CancellationToken.None);

                Assert.Equal(RemovalStrings.WorkspaceRestored(directory), Assert.Single(harness.Dialogs.Notices).Message);
                Assert.True(Directory.Exists(directory));
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(active.Id)).Status);
                Assert.Equal(TreeRowMark.Removed, harness.SessionRow(active.Id).RowMark);
            });
    }

    [Fact]
    public async Task Purge_all_purges_every_removed_session_of_the_workspace_and_the_row_disappears()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, active) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var archived = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Archived);
                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.RemoveWorkspaceAsync(directory, "feature-a");
                harness.Dialogs.ConfirmAnswers.Enqueue(true);

                await harness.Shell.PurgeAllAsync(new WorkspaceTarget(harness.Project, directory, "feature-a", null), CancellationToken.None);

                Assert.Equal(RemovalStrings.PurgeAllQuestion(2), Assert.Single(harness.Dialogs.Confirms).Message);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Null(await Task.Run(() => harness.Composition.Sessions.GetSessionAsync(active.Id, CancellationToken.None)));
                Assert.Null(await Task.Run(() => harness.Composition.Sessions.GetSessionAsync(archived.Id, CancellationToken.None)));
                Assert.Null(harness.FindWorkspaceRow(directory));
            });
    }

    [Fact]
    public async Task A_workspace_whose_directory_is_missing_is_marked_and_only_offers_its_removal()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                GitTestFiles.DeleteTree(directory);
                await harness.RefreshTreesAsync();

                var row = harness.WorkspaceRow(directory);
                Assert.Equal(TreeRowMark.Missing, row.RowMark);
                Assert.Equal(RemovalStrings.RowMissing, row.RowMarkText);
                Assert.False(row.IsRecycled);
                Assert.False(row.NewSessionCommand!.CanExecute(null));
                Assert.False(row.ForkCommand!.CanExecute(null));
                Assert.False(row.RevealCommand!.CanExecute(null));
                Assert.True(row.RemoveCommand!.CanExecute(null));

                harness.Dialogs.ChooseAnswers.Enqueue(0);
                await harness.RemoveWorkspaceAsync(directory, "feature-a");

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Contains(RemovalStrings.WorkspaceMissing, dialog.Message, StringComparison.Ordinal);
                Assert.Empty(harness.Trash.Moved);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal(TreeRowMark.Recycled, harness.WorkspaceRow(directory).RowMark);
            });
    }

    [Fact]
    public async Task A_workspace_git_marks_prunable_is_marked_stale_not_missing_and_keeps_reveal_and_removal()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                Assert.Equal(TreeRowMark.None, harness.WorkspaceRow(directory).RowMark);

                // The folder stays, its .git link goes: git now reports the worktree prunable.
                File.Delete(Path.Combine(directory, ".git"));
                Assert.Contains("prunable", harness.Git(harness.Repository, "worktree", "list", "--porcelain"), StringComparison.Ordinal);
                await harness.RefreshTreesAfterGitChangeAsync();

                var row = harness.WorkspaceRow(directory);
                Assert.Equal(TreeRowMark.Prunable, row.RowMark);
                Assert.Equal(RemovalStrings.RowPrunable, row.RowMarkText);
                Assert.NotEqual(RemovalStrings.RowMissing, row.RowMarkText);
                Assert.False(row.IsRecycled);
                Assert.False(row.NewSessionCommand!.CanExecute(null));
                Assert.False(row.ForkCommand!.CanExecute(null));
                Assert.True(row.RevealCommand!.CanExecute(null));
                Assert.True(row.RemoveCommand!.CanExecute(null));

                var items = row.MenuItems();
                var prefix = LifecycleStrings.WorktreePrunable("\u0001").Split('\u0001')[0];
                var replace = Assert.Single(items, static item => item.Key == TreeMenuKey.ReplaceSession);
                Assert.False(replace.IsEnabled);
                Assert.StartsWith(prefix, replace.Tip, StringComparison.Ordinal);
                Assert.NotEqual(LifecycleStrings.WorktreePrunable(null), replace.Tip);
                var fork = Assert.Single(items, static item => item.Key == TreeMenuKey.Fork);
                Assert.False(fork.IsEnabled);
                Assert.StartsWith(prefix, fork.Tip, StringComparison.Ordinal);
                Assert.True(Assert.Single(items, static item => item.Key == TreeMenuKey.Reveal).IsEnabled);
                Assert.True(Assert.Single(items, static item => item.Key == TreeMenuKey.RemoveWorkspace).IsEnabled);
                Assert.DoesNotContain(items, static item => item.Key is TreeMenuKey.RenameBranch or TreeMenuKey.MergeBack or TreeMenuKey.Sync);
            });
    }

    [Fact]
    public async Task An_archived_session_of_a_stale_worktree_cannot_continue_from_the_tree_the_click_or_its_view()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var archived = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Archived);
                Assert.True(harness.SessionRow(archived.Id).ContinueCommand!.CanExecute(null));

                File.Delete(Path.Combine(directory, ".git"));
                await harness.RefreshTreesAfterGitChangeAsync();

                var prefix = LifecycleStrings.WorktreePrunable("\u0001").Split('\u0001')[0];
                var row = harness.SessionRow(archived.Id);
                Assert.False(row.ContinueCommand!.CanExecute(null));
                var item = Assert.Single(row.MenuItems(), static entry => entry.Key == TreeMenuKey.Continue);
                Assert.False(item.IsEnabled);
                Assert.StartsWith(prefix, item.Tip, StringComparison.Ordinal);

                // The click re-checks git itself (the tree may be stale): it refuses and says why.
                await harness.Shell.ContinueSessionAsync(archived.Id, CancellationToken.None);
                Assert.Equal(SessionStatus.Archived, (await harness.InfoAsync(archived.Id)).Status);
                Assert.Contains(prefix, harness.Shell.Status.HostDetail, StringComparison.Ordinal);

                // The read-only view's own "continue" button learns the same reason.
                await harness.Shell.OpenSessionByIdAsync(archived.Id, CancellationToken.None);
                var view = harness.ActiveView();
                Assert.True(view.IsReadOnlyView);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (view.ContinueBlockedReason is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }

                Assert.StartsWith(prefix, view.ContinueBlockedReason, StringComparison.Ordinal);
                Assert.False(view.ContinueCommand!.CanExecute(null));
                Assert.Equal(LifecycleStrings.ContinueBlocked(view.ContinueBlockedReason!), view.ContinueLabel);
            });
    }

    [Fact]
    public async Task Continue_is_judged_from_the_worktree_list_and_an_unreadable_list_blocks_nothing()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var archived = await harness.AddSessionAsync(directory, "feature-a", "B", SessionStatus.Archived);
                var info = await harness.InfoAsync(archived.Id);

                Assert.Null(await harness.Shell.ContinueBlockAsync(info, CancellationToken.None));

                // The repository's own git folder away: the list read fails, which is unknown, neither healthy nor stale.
                var gitFolder = Path.Combine(harness.Repository, ".git");
                var away = gitFolder + "-away";
                Directory.Move(gitFolder, away);
                try
                {
                    Assert.Null(await harness.Shell.ContinueBlockAsync(info, CancellationToken.None));
                }
                finally
                {
                    Directory.Move(away, gitFolder);
                }

                // Unregistered (git's record of it gone) blocks, and a missing folder blocks with its own reason.
                GitTestFiles.DeleteTree(Path.Combine(gitFolder, "worktrees", "wt-a"));
                Assert.Equal(LifecycleStrings.WorktreeUnregistered, await harness.Shell.ContinueBlockAsync(info, CancellationToken.None));
                GitTestFiles.DeleteTree(directory);
                Assert.Equal(LifecycleStrings.DirectoryMissing, await harness.Shell.ContinueBlockAsync(info, CancellationToken.None));
            });
    }

    [Fact]
    public async Task An_unregistered_workspace_can_be_removed_after_remove_anyway_and_goes_to_the_trash()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                GitTestFiles.DeleteTree(Path.Combine(harness.Repository, ".git", "worktrees", "wt-a"));
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal([RemovalStrings.RemoveAnyway, ShellStrings.Cancel], dialog.Options);
                Assert.Contains("• " + RemovalStrings.GuardUnregistered, dialog.Message, StringComparison.Ordinal);
                Assert.Equal([directory], harness.Trash.Moved);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);
                Assert.Empty(harness.Dialogs.Notices);
            });
    }

    [Fact]
    public async Task A_listed_workspace_git_cannot_run_inside_asks_remove_anyway_with_its_commits_checked_from_the_repository()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, session) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                File.WriteAllText(Path.Combine(directory, "work.txt"), "work\n");
                harness.Git(directory, "add", "-A");
                harness.Git(directory, "commit", "-q", "-m", "work in progress");

                // The .git link points nowhere: git still lists the worktree (its record in the repository is intact),
                // but every git command inside the folder fails. (git hides the file on Windows: delete before writing.)
                var link = Path.Combine(directory, ".git");
                File.Delete(link);
                File.WriteAllText(link, "gitdir: " + Path.Combine(harness.Repository, "no-such-gitdir") + "\n");
                harness.Dialogs.ChooseAnswers.Enqueue(0);

                await harness.Shell.RemoveSessionAsync(session, harness.Project, CancellationToken.None);

                var dialog = Assert.Single(harness.Dialogs.Choices);
                Assert.Equal([RemovalStrings.RemoveAnyway, ShellStrings.Cancel], dialog.Options);
                Assert.Contains(
                    "• " + RemovalStrings.GuardChangesUnchecked("\u0001").Split('\u0001')[0],
                    dialog.Message,
                    StringComparison.Ordinal);
                Assert.Contains("• 1 commit not merged into ", dialog.Message, StringComparison.Ordinal);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal([directory], harness.Trash.Moved);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(session.Id)).Status);
                Assert.NotEmpty(harness.Git(harness.Repository, "rev-parse", "--verify", "refs/heads/feature-a").Trim());
            });
    }

    [Fact]
    public async Task A_workspace_missing_from_the_worktree_list_is_marked_unregistered()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                // git's own record of the worktree goes; the folder (with its now dangling .git link) stays.
                GitTestFiles.DeleteTree(Path.Combine(harness.Repository, ".git", "worktrees", "wt-a"));
                await harness.RefreshTreesAfterGitChangeAsync();

                var row = harness.WorkspaceRow(directory);
                Assert.Equal(TreeRowMark.Unregistered, row.RowMark);
                Assert.Equal(RemovalStrings.RowUnregistered, row.RowMarkText);
                Assert.False(row.NewSessionCommand!.CanExecute(null));
                Assert.False(row.ForkCommand!.CanExecute(null));
                Assert.True(row.RemoveCommand!.CanExecute(null));
                var replace = Assert.Single(row.MenuItems(), static item => item.Key == TreeMenuKey.ReplaceSession);
                Assert.Equal(LifecycleStrings.WorktreeUnregistered, replace.Tip);
            });
    }

    [Fact]
    public async Task A_failed_or_empty_worktree_list_leaves_the_workspace_unverified_neither_normal_nor_stale()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var calls = 0;
                harness.Shell.Projects.ReadWorktrees = (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("git worktree list failed");
                };
                await harness.RefreshTreesAfterGitChangeAsync();

                var row = harness.WorkspaceRow(directory);
                Assert.True(calls > 0);
                Assert.Equal(TreeRowMark.Unverified, row.RowMark);
                Assert.Equal(RemovalStrings.RowUnverified, row.RowMarkText);
                // Unknown is not stale: nothing is disabled for it, the flows check git after the click.
                Assert.True(row.NewSessionCommand!.CanExecute(null));
                Assert.True(row.ForkCommand!.CanExecute(null));
                Assert.True(Assert.Single(row.MenuItems(), static item => item.Key == TreeMenuKey.ReplaceSession).IsEnabled);

                // An answer without even the main worktree is not one to judge by either.
                harness.Shell.Projects.ReadWorktrees = (_, _) => Task.FromResult<IReadOnlyList<Forge.Workspace.GitWorktree>>([]);
                await harness.RefreshTreesAfterGitChangeAsync();
                Assert.Equal(TreeRowMark.Unverified, harness.WorkspaceRow(directory).RowMark);

                // A directory that is gone is "directory missing" whatever git could say.
                GitTestFiles.DeleteTree(directory);
                await harness.RefreshTreesAfterGitChangeAsync();
                Assert.Equal(TreeRowMark.Missing, harness.WorkspaceRow(directory).RowMark);
            });
    }

    [Fact]
    public async Task The_worktree_list_is_read_once_per_lifetime_even_for_an_unregistered_folder_and_never_by_a_menu()
    {
        await RunAsync(
            async harness =>
            {
                var (directory, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                GitTestFiles.DeleteTree(Path.Combine(harness.Repository, ".git", "worktrees", "wt-a"));
                var real = harness.Shell.Projects.ReadWorktrees!;
                var calls = 0;
                harness.Shell.Projects.ReadWorktrees = (root, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    return real(root, ct);
                };

                await harness.RefreshTreesAfterGitChangeAsync();
                Assert.Equal(TreeRowMark.Unregistered, harness.WorkspaceRow(directory).RowMark);
                Assert.Equal(1, calls);

                // Within the lifetime: the folder was already judged against this list, so no new git call.
                await harness.RefreshTreesAsync();
                await harness.RefreshTreesAsync();
                var row = harness.WorkspaceRow(directory);
                Assert.Equal(TreeRowMark.Unregistered, row.RowMark);
                Assert.NotEmpty(row.MenuItems());
                Assert.Equal(1, calls);
            });
    }

    [Fact]
    public async Task A_workspace_made_after_the_cached_worktree_list_is_not_reported_unregistered()
    {
        await RunAsync(
            async harness =>
            {
                // Each add refreshes the tree; the second one runs inside the first list's lifetime.
                var (first, _) = await harness.AddWorktreeAsync("wt-a", "feature-a", "A");
                var (second, _) = await harness.AddWorktreeAsync("wt-b", "feature-b", "B");

                Assert.Equal(TreeRowMark.None, harness.WorkspaceRow(first).RowMark);
                Assert.Equal(TreeRowMark.None, harness.WorkspaceRow(second).RowMark);
            });
    }

    [Fact]
    public async Task The_merge_back_removal_goes_through_the_workspace_removal_with_the_source_branch()
    {
        await RunAsync(
            async harness =>
            {
                var directory = Path.Combine(harness.SandboxRoot, "fork-wt");
                harness.Git(harness.Repository, "worktree", "add", "-q", "-b", "fork", directory, "main");
                var fork = await Task.Run(() => harness.Composition.Catalog.CreateAsync(
                    new SessionCreate(directory, "fork")
                    {
                        ProjectId = harness.Project.Id,
                        WorktreePath = directory,
                        Branch = "fork",
                        ForkOfBranch = "main",
                    },
                    CancellationToken.None));
                harness.CommitIn(directory, "feature.txt", "feature\n", "feature work");
                harness.Dialogs.ChooseAnswers.Enqueue(0); // merge
                harness.Dialogs.ChooseAnswers.Enqueue(0); // remove the fork
                harness.Dialogs.ChooseAnswers.Enqueue(0); // archive "fork" and remove

                await harness.Shell.MergeForkAsync(
                    new ForkRelation(harness.Project, directory, "fork", "main", null),
                    MergeDirection.MergeBack,
                    CancellationToken.None);

                Assert.Equal([RemovalStrings.ArchiveAndRemove("fork"), ShellStrings.Cancel], harness.Dialogs.Choices[2].Options);
                Assert.Empty(harness.Dialogs.Notices);
                Assert.Equal([directory], harness.Trash.Moved);
                Assert.Equal(SessionStatus.Deleted, (await harness.InfoAsync(fork.Id)).Status);
                Assert.NotEmpty(harness.Git(harness.Repository, "rev-parse", "--verify", "refs/heads/fork").Trim());
            });
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

    /// <summary>A trash that moves into a temporary folder, or refuses with <see cref="Refusal"/>; records what it moved.</summary>
    private sealed class FakeTrash(string root) : ITrashService
    {
        public const string Refusal = "trash refused";

        public bool Fail { get; set; }

        public List<string> Moved { get; } = [];

        /// <summary>Runs first on every move (on the caller's thread), to observe the window at that moment.</summary>
        public Action<string>? BeforeMove { get; set; }

        public Task MoveToTrashAsync(string path, CancellationToken ct)
        {
            BeforeMove?.Invoke(path);
            if (Fail)
            {
                throw new IOException(Refusal);
            }

            Directory.CreateDirectory(root);
            Directory.Move(path, Path.Combine(root, Guid.NewGuid().ToString("N")));
            lock (Moved)
            {
                Moved.Add(path);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Records every dialog; choices and confirmations are answered from the queues (closed / no when empty).</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public Queue<int> ChooseAnswers { get; } = new();

        public Queue<bool> ConfirmAnswers { get; } = new();

        public List<ChooseCall> Choices { get; } = [];

        public List<ConfirmCall> Confirms { get; } = [];

        public List<NotifyCall> Notices { get; } = [];

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

        public Task<string?> PromptTextAsync(string title, string initial) => Task.FromResult<string?>(null);

        public Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct) =>
            Task.FromResult<NewBranchChoice?>(null);

        public Task NotifyAsync(string title, string message)
        {
            Notices.Add(new NotifyCall(title, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A throw-away data root, a real composition root with a fake trash, and a real clone registered as a repository
    /// project with one Active session in its root checkout, open as a live tab.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly EnvironmentSnapshot previous;
        private readonly GitTestSandbox sandbox;

        private Harness(string root, EnvironmentSnapshot previous, GitTestSandbox sandbox, AppComposition composition, ProjectInfo project, string sourceId, MainWindowViewModel shell, RecordingDialogs dialogs, FakeTrash trash)
        {
            Root = root;
            this.previous = previous;
            this.sandbox = sandbox;
            Composition = composition;
            Project = project;
            SourceId = sourceId;
            Shell = shell;
            Dialogs = dialogs;
            Trash = trash;
        }

        public string Root { get; }

        public string SandboxRoot => sandbox.Root;

        public string Repository => Project.RootPath;

        public AppComposition Composition { get; }

        public ProjectInfo Project { get; }

        public string SourceId { get; }

        public MainWindowViewModel Shell { get; }

        public RecordingDialogs Dialogs { get; }

        public FakeTrash Trash { get; }

        public SessionWorkspaceViewModel SourceView { get; private set; } = null!;

        public static async Task<Harness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-removal-shell-" + Guid.NewGuid().ToString("N"));
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
            var trash = new FakeTrash(Path.Combine(root, "trash"));
            composition.Trash = trash;
            var project = await Task.Run(() => composition.Projects.AddAsync(repository, "repo", ProjectKind.Repository, CancellationToken.None));
            var source = await Task.Run(() => composition.Catalog.CreateAsync(
                new SessionCreate(repository, SourceTitle) { ProjectId = project.Id },
                CancellationToken.None));

            var dialogs = new RecordingDialogs();
            var shell = new MainWindowViewModel(composition);
            shell.AttachDialogs(dialogs);
            await shell.InitializeAsync();
            var harness = new Harness(root, previous, sandbox, composition, project, source.Id, shell, dialogs, trash);
            await shell.OpenSessionByIdAsync(source.Id, CancellationToken.None);
            harness.SourceView = harness.ActiveView();
            Assert.Equal(source.Id, harness.SourceView.SessionId);
            return harness;
        }

        /// <summary>A worktree on a new branch from <c>main</c> with one Active session in it.</summary>
        public async Task<(string Directory, SessionInfo Session)> AddWorktreeAsync(string name, string branch, string title)
        {
            var directory = Path.Combine(sandbox.Root, name);
            Git(Repository, "worktree", "add", "-q", "-b", branch, directory, "main");
            var session = await AddSessionAsync(directory, branch, title, SessionStatus.Active);
            return (directory, session);
        }

        public async Task<SessionInfo> AddSessionAsync(string directory, string branch, string title, SessionStatus status)
        {
            // A session is created Active (conditional insert), so an Archived one is made by stepping the current
            // holder of the directory aside, creating, archiving, and activating the holder again.
            var catalog = Composition.Catalog;
            var holders = status == SessionStatus.Archived
                ? await Task.Run(() => catalog.QueryAsync(
                    new SessionQuery { ProjectId = Project.Id, WorktreePath = directory, Status = SessionStatus.Active, ParentSessionId = string.Empty },
                    CancellationToken.None))
                : [];
            foreach (var holder in holders)
            {
                Assert.True(await Task.Run(() => catalog.SetStatusAsync(holder.Id, SessionStatus.Archived, CancellationToken.None)));
            }

            var created = await Task.Run(() => catalog.CreateAsync(
                new SessionCreate(directory, title)
                {
                    ProjectId = Project.Id,
                    WorktreePath = directory,
                    Branch = branch,
                },
                CancellationToken.None));
            if (status != SessionStatus.Active)
            {
                Assert.True(await Task.Run(() => catalog.SetStatusAsync(created.Id, status, CancellationToken.None)));
            }

            foreach (var holder in holders)
            {
                Assert.True(await Task.Run(() => catalog.ActivateAsync(holder.Id, CancellationToken.None)));
            }
            await RefreshTreesAsync();
            return await InfoAsync(created.Id);
        }

        public Task RemoveWorkspaceAsync(string directory, string branch) =>
            Shell.Projects.RemoveWorkspace!(new WorkspaceTarget(Project, directory, branch, null), CancellationToken.None);

        public Task RefreshTreesAsync() => Shell.Projects.RefreshAsync(CancellationToken.None);

        /// <summary>A refresh after the cached git reads ran out (the tree reuses them for a few seconds otherwise).</summary>
        public Task RefreshTreesAfterGitChangeAsync()
        {
            Shell.Projects.InvalidateGitState();
            return RefreshTreesAsync();
        }

        public TreeNodeViewModel WorkspaceRow(string directory) =>
            Assert.IsType<TreeNodeViewModel>(FindWorkspaceRow(directory));

        public TreeNodeViewModel? FindWorkspaceRow(string directory) =>
            Enumerate(Shell.Projects.Sections).FirstOrDefault(
                node => string.Equals(node.Key, "worktree:" + Project.Id + "|" + directory, StringComparison.OrdinalIgnoreCase));

        public TreeNodeViewModel SessionRow(string sessionId) =>
            Assert.Single(Enumerate(Shell.Projects.Sections), node => node.Key == "session:" + sessionId);

        public void CommitIn(string directory, string relativePath, string content, string message) =>
            sandbox.CommitFile(directory, relativePath, content, message);

        public string Git(string directory, params string[] arguments) => GitTestShell.RunChecked(directory, arguments);

        public SessionWorkspaceViewModel ActiveView() => Assert.IsType<SessionWorkspaceViewModel>(Shell.ActiveSession);

        public async Task<SessionInfo> InfoAsync(string sessionId) =>
            Assert.IsType<SessionInfo>(await Task.Run(() => Composition.Sessions.GetSessionAsync(sessionId, CancellationToken.None)));

        public async Task<IReadOnlyList<string>> ProjectIdsAsync() =>
            (await Task.Run(() => Composition.Projects.ListAsync(CancellationToken.None))).Select(static p => p.Id).ToArray();

        public async ValueTask DisposeAsync()
        {
            Shell.Files.Attach(PanelContext.Empty);
            Shell.Dispose();
            await Composition.DisposeAsync();
            previous.Restore();
            sandbox.Dispose();
            GitTestFiles.DeleteTree(Root);
        }

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
