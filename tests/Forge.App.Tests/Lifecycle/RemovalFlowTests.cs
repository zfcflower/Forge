using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// <see cref="RemovalFlow"/> without a shell (TASK-session-worktree-lifecycle 7.2-7.5): the races between the dialog's
/// plan and its execution, the record write failing after the trash, a prune failure staying silent, the purge cleanup
/// and purge failures, directories Forge never recycles (the general root, a repository's root checkout), the cascade to
/// sub-agent sessions, the root-directory red line, and guard reasons that appeared while the dialog was open. The
/// catalog, git and trash are in-memory fakes; directories are real temporary folders.
/// </summary>
public sealed class RemovalFlowTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-removal-flow-");
    private readonly FakeCatalog catalog = new();
    private readonly FakeGit git = new();
    private readonly List<string> trashed = [];
    private readonly List<string> cleaned = [];

    public RemovalFlowTests()
    {
        Directory.CreateDirectory(root);
        Repository = new ProjectInfo("p1", "repo", Path.Combine(root, "repo"), ProjectKind.Repository, 0, DateTimeOffset.UnixEpoch);
        General = new ProjectInfo("g1", "general", Path.Combine(root, "general"), ProjectKind.General, 1, DateTimeOffset.UnixEpoch);
        Directory.CreateDirectory(Repository.RootPath);
        Directory.CreateDirectory(General.RootPath);
        git.Root = root;
    }

    private ProjectInfo Repository { get; }

    private ProjectInfo General { get; }

    private Exception? TrashFailure { get; set; }

    private Exception? CleanupFailure { get; set; }

    private string? FailCleanupOf { get; set; }

    public void Dispose() => GitTestFiles.DeleteTree(root);

    [Fact]
    public async Task A_session_already_removed_when_the_dialog_closes_removes_nothing()
    {
        var (directory, session) = Worktree("wt-a", "A");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        catalog.Replace(session with { Status = SessionStatus.Deleted });

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.Changed, outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_directory_that_would_now_go_to_the_trash_although_the_dialog_said_kept_removes_nothing()
    {
        var (directory, session) = Worktree("wt-a", "A");
        var other = catalog.Add(Session("B", directory, SessionStatus.Archived) with { WorktreePath = directory, Branch = "wt-a" });
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Equal(DirectoryFate.Shared, plan.Fate);
        catalog.Replace(other with { Status = SessionStatus.Deleted });

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.Changed, outcome.Error);
        Assert.Empty(trashed);
        Assert.Equal(SessionStatus.Active, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task A_workspace_whose_session_became_active_during_the_dialog_is_not_removed()
    {
        var (directory, session) = Worktree("wt-a", "A", SessionStatus.Archived);
        var plan = await Flow().PlanWorkspaceRemovalAsync(Repository, directory, "wt-a", null, CancellationToken.None);
        Assert.Null(plan.Active);
        catalog.Replace(session with { Status = SessionStatus.Active });

        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.Changed, outcome.Error);
        Assert.Empty(trashed);
        Assert.Equal(0, git.Prunes);
    }

    [Fact]
    public async Task A_missing_directory_that_reappears_during_the_dialog_is_not_removed()
    {
        var (directory, _) = Worktree("wt-a", "A", SessionStatus.Archived);
        Directory.Delete(directory);
        var plan = await Flow().PlanWorkspaceRemovalAsync(Repository, directory, "wt-a", null, CancellationToken.None);
        Assert.True(plan.Missing);
        Directory.CreateDirectory(directory);

        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.Changed, outcome.Error);
        Assert.Empty(trashed);
        Assert.Equal(0, git.Prunes);
    }

    [Fact]
    public async Task A_record_failure_after_the_trash_says_the_directory_is_gone_and_never_brings_it_back()
    {
        var (directory, session) = Worktree("wt-a", "A");
        catalog.SoftDeleteFailure = new IOException("disk full");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(RemovalStrings.RecordFailed("disk full"), outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Empty(outcome.RemovedSessionIds);
        Assert.Equal(SessionStatus.Active, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task A_record_failure_when_the_directory_was_already_missing_only_names_the_record()
    {
        var (directory, session) = Worktree("wt-a", "A");
        Directory.Delete(directory);
        catalog.SoftDeleteFailure = new IOException("locked");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.RecordOnlyFailed("locked"), outcome.Error);
        Assert.Empty(trashed);
        Assert.Equal(1, git.Prunes);
    }

    [Fact]
    public async Task A_prune_failure_is_not_a_removal_failure()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.PruneFailure = new InvalidOperationException("prune broke");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Equal(1, git.Prunes);
        Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task The_general_root_and_the_root_checkout_are_kept_and_only_the_record_goes()
    {
        var general = catalog.Add(Session("G", General.RootPath, SessionStatus.Active) with { ProjectId = General.Id });
        var rootCheckout = catalog.Add(Session("R", Repository.RootPath, SessionStatus.Active));

        foreach (var (session, project) in new[] { (general, General), (rootCheckout, Repository) })
        {
            var plan = await Flow().PlanSessionRemovalAsync(session, project, CancellationToken.None);
            Assert.Equal(DirectoryFate.Kept, plan.Fate);
            Assert.Empty(plan.Reasons);
            var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);
            Assert.True(outcome.Succeeded);
            Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
        }

        Assert.Empty(trashed);
        Assert.Equal(0, git.GuardCalls);
        Assert.True(Directory.Exists(General.RootPath));
        Assert.True(Directory.Exists(Repository.RootPath));
    }

    [Fact]
    public async Task A_general_scratch_folder_goes_to_the_trash_without_git()
    {
        var folder = Path.Combine(General.RootPath, "chat-1");
        Directory.CreateDirectory(folder);
        var session = catalog.Add(Session("G", folder, SessionStatus.Active) with { ProjectId = General.Id });
        var plan = await Flow().PlanSessionRemovalAsync(session, General, CancellationToken.None);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal([folder], trashed);
        Assert.Equal(0, git.GuardCalls);
        Assert.Equal(0, git.Prunes);
    }

    [Fact]
    public async Task The_outcome_names_the_sub_agent_sessions_removed_with_their_root()
    {
        var (_, session) = Worktree("wt-a", "A");
        var child = catalog.Add(Session("child", session.WorkspacePath, SessionStatus.Active) with { WorktreePath = session.WorktreePath, ParentSessionId = session.Id });
        var grandchild = catalog.Add(Session("grandchild", session.WorkspacePath, SessionStatus.Active) with { WorktreePath = session.WorktreePath, ParentSessionId = child.Id });
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Equal(2, plan.ChildCount);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(
            new[] { session.Id, child.Id, grandchild.Id }.Order(StringComparer.Ordinal),
            outcome.RemovedSessionIds.Order(StringComparer.Ordinal));
        Assert.Equal(SessionStatus.Deleted, catalog.Get(grandchild.Id).Status);
    }

    [Fact]
    public async Task The_project_root_is_never_a_removable_workspace()
    {
        catalog.Add(Session("R", Repository.RootPath, SessionStatus.Archived));

        var planning = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Flow().PlanWorkspaceRemovalAsync(Repository, Repository.RootPath, "main", null, CancellationToken.None));
        Assert.Equal(RemovalStrings.RootRefused(Repository.RootPath), planning.Message);

        // A plan built by hand (any caller) is refused again at execution.
        var forged = new WorkspaceRemovalPlan(Repository, Repository.RootPath, "main", null, false, [], [], null, []);
        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(forged, CancellationToken.None);

        Assert.Equal(RemovalStrings.RootRefused(Repository.RootPath), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(Repository.RootPath));
    }

    [Fact]
    public async Task A_general_project_has_no_removable_workspace()
    {
        var folder = Path.Combine(General.RootPath, "chat-1");
        Directory.CreateDirectory(folder);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Flow().PlanWorkspaceRemovalAsync(General, folder, null, null, CancellationToken.None));
        Assert.Empty(trashed);
    }

    [Fact]
    public async Task Guard_reasons_that_appeared_while_the_dialog_was_open_stop_the_removal_and_are_named()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Reasons.Enqueue(["dirty file"]);
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Equal(["dirty file"], plan.Reasons);
        git.Reasons.Enqueue(["dirty file", "unpushed commit"]);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.ReasonsGrew(["unpushed commit"]), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(SessionStatus.Active, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task The_same_reasons_at_execution_go_through_and_a_guard_that_throws_then_stops_the_removal()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Reasons.Enqueue(["dirty file"]);
        git.Reasons.Enqueue(["dirty file"]);
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.True((await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None)).Succeeded);
        Assert.Equal([directory], trashed);

        var (other, second) = Worktree("wt-b", "B");
        var secondPlan = await Flow().PlanWorkspaceRemovalAsync(Repository, other, "wt-b", null, CancellationToken.None);
        catalog.Replace(second with { Status = SessionStatus.Archived });

        // Not a git failure (those are reasons, see below): the re-check cannot judge, so nothing moves.
        git.GuardFailure = new ArgumentException("git died");

        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(secondPlan with { Active = null }, CancellationToken.None);

        Assert.Equal(RemovalStrings.CheckFailed("git died"), outcome.Error);
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task A_purge_cleanup_failure_keeps_the_purge_and_warns()
    {
        var session = catalog.Add(Session("A", Path.Combine(root, "gone"), SessionStatus.Deleted));
        CleanupFailure = new IOException("artifact locked");

        var outcome = await Flow().PurgeAsync(session, CancellationToken.None);

        Assert.Equal([session.Id], outcome.PurgedIds);
        Assert.Equal(RemovalStrings.PurgePartial("artifact locked"), outcome.Warning);
        Assert.Null(outcome.Error);
        Assert.Null(catalog.Find(session.Id));
    }

    [Fact]
    public async Task A_purge_that_the_store_cannot_do_is_a_visible_failure()
    {
        var session = catalog.Add(Session("A", Path.Combine(root, "gone"), SessionStatus.Deleted));
        catalog.PurgeFailure = new NotSupportedException("no purge here");

        var outcome = await Flow().PurgeAsync(session, CancellationToken.None);

        Assert.Equal(RemovalStrings.PurgeFailed("A", "no purge here"), outcome.Error);
        Assert.Empty(outcome.PurgedIds);
        Assert.Empty(cleaned);

        catalog.PurgeFailure = new OperationCanceledException();
        Assert.Equal(RemovalStrings.PurgeFailed("A", RemovalStrings.Cancelled), (await Flow().PurgeAsync(session, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task A_purge_cleans_every_purged_id()
    {
        var session = catalog.Add(Session("A", Path.Combine(root, "gone"), SessionStatus.Deleted));
        var child = catalog.Add(Session("child", Path.Combine(root, "gone"), SessionStatus.Deleted) with { ParentSessionId = session.Id });

        var outcome = await Flow().PurgeAsync(session, CancellationToken.None);

        Assert.Null(outcome.Warning);
        Assert.Equal([session.Id, child.Id], cleaned);
    }

    [Fact]
    public async Task A_purge_of_several_ids_names_the_one_whose_cleanup_failed()
    {
        var session = catalog.Add(Session("A", Path.Combine(root, "gone"), SessionStatus.Deleted));
        var child = catalog.Add(Session("child", Path.Combine(root, "gone"), SessionStatus.Deleted) with { ParentSessionId = session.Id });
        FailCleanupOf = child.Id;

        var outcome = await Flow().PurgeAsync(session, CancellationToken.None);

        Assert.Equal(RemovalStrings.PurgePartial(child.Id + ": artifact locked"), outcome.Warning);
        Assert.Equal([session.Id], cleaned);
    }

    [Fact]
    public async Task A_prunable_worktree_is_guarded_by_what_git_says_and_never_asked_about_its_changes()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory) with { IsPrunable = true, PrunableReason = "gitdir file points to non-existent location" }];

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Equal([RemovalStrings.GuardPrunable("gitdir file points to non-existent location")], plan.Reasons);
        Assert.Equal(0, git.GuardCalls);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Equal(0, git.GuardCalls);
        Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task An_unregistered_workspace_is_guarded_and_goes_to_the_trash_when_confirmed()
    {
        var (directory, session) = Worktree("wt-a", "A", SessionStatus.Archived);
        git.Worktrees = [Listed(Repository.RootPath)];

        var plan = await Flow().PlanWorkspaceRemovalAsync(Repository, directory, "wt-a", null, CancellationToken.None);

        Assert.Equal([RemovalStrings.GuardUnregistered], plan.Reasons);
        Assert.False(plan.Missing);
        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Equal(1, git.Prunes);
        Assert.Equal(0, git.GuardCalls);
        Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task A_registered_worktree_still_gets_the_change_check_and_a_spelling_difference_does_not_hide_it()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory.Replace(Path.DirectorySeparatorChar, '/') + "/")];
        git.Reasons.Enqueue(["1 uncommitted change"]);

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Equal(["1 uncommitted change"], plan.Reasons);
        Assert.Equal(1, git.GuardCalls);
    }

    [Fact]
    public async Task A_list_that_fails_or_comes_back_empty_is_unknown_the_usual_check_runs_and_the_removal_asks()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.ListFailure = new GitException("fatal: not a git repository", ["worktree", "list"], 128, "fatal: not a git repository");
        git.Reasons.Enqueue(["1 uncommitted change"]);

        var failed = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        // Unknown is not "stale" (the change check runs) and not "healthy" (a clean answer may be an enclosing
        // repository's, so the unverified state is a reason of its own and the dialog asks "remove anyway").
        Assert.Equal([RemovalStrings.GuardUnverified("fatal: not a git repository"), "1 uncommitted change"], failed.Reasons);
        Assert.Equal(1, git.GuardCalls);

        git.ListFailure = null;
        git.Worktrees = [];
        var empty = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Equal([RemovalStrings.GuardUnverified(RemovalStrings.WorktreeListEmpty)], empty.Reasons);
        Assert.Equal(2, git.GuardCalls);

        // Confirmed, it goes to the trash like any guarded removal.
        var outcome = await Flow().ExecuteSessionRemovalAsync(empty, CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
    }

    [Fact]
    public async Task An_unknown_list_with_a_failing_check_names_both_and_the_confirmed_removal_goes_to_the_trash()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.ListFailure = new GitException("list failed", ["worktree", "list"], 128, "list failed");
        git.GuardFailure = new GitException("status failed", ["status"], 128, "status failed");

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        // No list, so no HEAD to check the commits by: both unknowns are named and the dialog asks "remove anyway".
        Assert.Equal([RemovalStrings.GuardUnverified("list failed"), RemovalStrings.GuardCheckFailed("status failed")], plan.Reasons);
        Assert.Equal(0, git.CommitCalls);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task A_registered_worktree_git_cannot_run_inside_has_its_commits_checked_from_the_repository()
    {
        var (directory, session) = Worktree("wt-a", "A", SessionStatus.Archived);
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory) with { HeadCommit = "def456", IsDetached = true }];
        git.GuardFailure = new GitException("fatal: not a git repository", ["status"], 128, "fatal: not a git repository");
        git.CommitReasons.Enqueue(["1 commit not merged into 'main'"]);

        var plan = await Flow().PlanWorkspaceRemovalAsync(Repository, directory, "wt-a", "main", CancellationToken.None);

        Assert.Equal(
            [RemovalStrings.GuardChangesUnchecked("fatal: not a git repository"), "1 commit not merged into 'main'"],
            plan.Reasons);
        Assert.Equal(("def456", true, "main"), git.LastCommitCheck);

        // The same answers at execution: confirmed, it goes through.
        git.CommitReasons.Enqueue(["1 commit not merged into 'main'"]);
        var outcome = await Flow().ExecuteWorkspaceRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
        Assert.Equal(2, git.CommitCalls);
        Assert.Equal(SessionStatus.Deleted, catalog.Get(session.Id).Status);
    }

    [Fact]
    public async Task A_check_that_fails_inside_and_from_the_repository_is_one_reason_and_other_words_later_do_not_block()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory)];
        git.GuardFailure = new GitException("status failed", ["status"], 128, "status failed");
        git.CommitFailure = new IOException("pack unreadable");

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Equal([RemovalStrings.GuardCheckFailed("status failed")], plan.Reasons);
        Assert.Equal(1, git.CommitCalls);

        // The check fails again in other words: the dialog already said it could not check, so this is not new.
        git.GuardFailure = new System.ComponentModel.Win32Exception("git could not start");
        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Equal([directory], trashed);
    }

    [Fact]
    public async Task A_listed_worktree_without_a_head_is_not_checked_from_the_repository()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory) with { HeadCommit = null }];
        git.GuardFailure = new TimeoutException("git timed out");

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Equal([RemovalStrings.GuardCheckFailed("git timed out")], plan.Reasons);
        Assert.Equal(0, git.CommitCalls);
    }

    [Fact]
    public async Task A_failed_check_that_finds_work_at_execution_stops_the_removal_and_names_it()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.GuardFailure = new GitException("status failed", ["status"], 128, "status failed");
        git.CommitFailure = new IOException("pack unreadable");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        git.GuardFailure = null;
        git.Reasons.Enqueue(["2 uncommitted changes"]);

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.ReasonsGrew(["2 uncommitted changes"]), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_clean_check_that_fails_at_execution_stops_the_removal_and_names_the_failure()
    {
        var (directory, session) = Worktree("wt-a", "A");
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Empty(plan.Reasons);
        git.GuardFailure = new GitException("status failed", ["status"], 128, "status failed");
        git.CommitFailure = new IOException("pack unreadable");

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.ReasonsGrew([RemovalStrings.GuardCheckFailed("status failed")]), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task Gits_wording_of_an_unverified_or_prunable_state_may_change_before_execution()
    {
        var (first, a) = Worktree("wt-a", "A");
        git.ListFailure = new GitException("index.lock exists", ["worktree", "list"], 128, "index.lock exists");
        var unverified = await Flow().PlanSessionRemovalAsync(a, Repository, CancellationToken.None);
        git.ListFailure = new GitException("permission denied", ["worktree", "list"], 128, "permission denied");

        Assert.True((await Flow().ExecuteSessionRemovalAsync(unverified, CancellationToken.None)).Succeeded);

        var (second, b) = Worktree("wt-b", "B");
        git.ListFailure = null;
        git.Worktrees = [Listed(Repository.RootPath), Listed(second) with { IsPrunable = true, PrunableReason = "gitdir file points to non-existent location" }];
        var prunable = await Flow().PlanSessionRemovalAsync(b, Repository, CancellationToken.None);
        git.Worktrees = [Listed(Repository.RootPath), Listed(second) with { IsPrunable = true, PrunableReason = "gitdir file does not point to a worktree" }];

        Assert.True((await Flow().ExecuteSessionRemovalAsync(prunable, CancellationToken.None)).Succeeded);
        Assert.Equal([first, second], trashed);
    }

    [Fact]
    public async Task An_exception_that_is_not_a_git_failure_still_fails_the_plan_and_removes_nothing()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.GuardFailure = new ArgumentException("bad path");

        await Assert.ThrowsAsync<ArgumentException>(() => Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None));
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void A_shown_reason_covers_found_work_by_text_and_the_unknown_states_by_kind()
    {
        GuardReason Of(GuardReasonKind kind, string text = "x") => new(text, kind);

        Assert.True(GuardReason.Change("1 uncommitted change").Covers(GuardReason.Change("1 uncommitted change")));
        Assert.False(GuardReason.Change("1 uncommitted change").Covers(GuardReason.Change("2 uncommitted changes")));
        Assert.False(Of(GuardReasonKind.CheckFailed).Covers(GuardReason.Change("1 uncommitted change")));
        Assert.True(Of(GuardReasonKind.CheckFailed, "a").Covers(Of(GuardReasonKind.CheckFailed, "b")));
        Assert.True(Of(GuardReasonKind.CheckFailed).Covers(Of(GuardReasonKind.ChangesUnchecked)));
        Assert.True(Of(GuardReasonKind.ChangesUnchecked, "a").Covers(Of(GuardReasonKind.ChangesUnchecked, "b")));
        Assert.False(Of(GuardReasonKind.ChangesUnchecked).Covers(Of(GuardReasonKind.CheckFailed)));
        Assert.True(Of(GuardReasonKind.Unverified, "a").Covers(Of(GuardReasonKind.Unverified, "b")));
        Assert.True(Of(GuardReasonKind.Prunable, "a").Covers(Of(GuardReasonKind.Prunable, "b")));
        Assert.False(Of(GuardReasonKind.Prunable).Covers(Of(GuardReasonKind.Unregistered)));
        Assert.False(Of(GuardReasonKind.Unverified).Covers(Of(GuardReasonKind.CheckFailed)));
    }

    [Fact]
    public async Task A_registered_worktree_with_no_changes_has_no_reasons()
    {
        var (_, session) = Worktree("wt-a", "A");

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Empty(plan.Reasons);
        Assert.Equal(1, git.GuardCalls);
    }

    [Fact]
    public async Task A_worktree_that_went_stale_while_the_dialog_was_open_stops_the_removal()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory)];
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        Assert.Empty(plan.Reasons);
        git.Worktrees = [Listed(Repository.RootPath)];

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, CancellationToken.None);

        Assert.Equal(RemovalStrings.ReasonsGrew([RemovalStrings.GuardUnregistered]), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_cancelled_re_check_is_reported_as_a_failed_check_and_removes_nothing()
    {
        var (directory, session) = Worktree("wt-a", "A");
        git.Worktrees = [Listed(Repository.RootPath), Listed(directory)];
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var outcome = await Flow().ExecuteSessionRemovalAsync(plan, cancelled.Token);

        Assert.Equal(RemovalStrings.CheckFailed(RemovalStrings.Cancelled), outcome.Error);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_plan_whose_caller_cancelled_is_cancelled_not_turned_into_a_reason()
    {
        var (directory, session) = Worktree("wt-a", "A");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        git.ListFailure = new OperationCanceledException(cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Flow().PlanSessionRemovalAsync(session, Repository, cancelled.Token));

        // A cancellation from inside git while the caller's token is live is a timeout: a reason (commits still checked).
        git.ListFailure = null;
        git.GuardFailure = new OperationCanceledException(cancelled.Token);
        using var live = new CancellationTokenSource();
        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, live.Token);
        Assert.Equal([RemovalStrings.GuardChangesUnchecked(new OperationCanceledException().Message)], plan.Reasons);
        Assert.Empty(trashed);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_list_read_cancelled_inside_git_while_the_caller_was_not_is_a_timeout_and_reads_as_unknown()
    {
        var (_, session) = Worktree("wt-a", "A");
        git.ListFailure = new OperationCanceledException("git timed out");

        var plan = await Flow().PlanSessionRemovalAsync(session, Repository, CancellationToken.None);

        Assert.Equal([RemovalStrings.GuardUnverified("git timed out")], plan.Reasons);
        Assert.Equal(1, git.GuardCalls);
    }

    [Fact]
    public void The_list_judge_tells_registered_prunable_unregistered_and_unknown_apart()
    {
        var directory = Path.Combine(root, "wt-a");
        Directory.CreateDirectory(directory);
        var main = Listed(Repository.RootPath);

        Assert.Equal(WorktreeRegistration.Unknown, WorktreeList.Judge(null, directory).State);
        Assert.Equal(WorktreeRegistration.Unknown, WorktreeList.Judge([], directory).State);
        Assert.Equal(WorktreeRegistration.Unregistered, WorktreeList.Judge([main], directory).State);
        Assert.Equal(WorktreeRegistration.Registered, WorktreeList.Judge([main, Listed(directory)], directory).State);
        Assert.Equal(
            (WorktreeRegistration.Prunable, "gone"),
            WorktreeList.Judge([main, Listed(directory) with { IsPrunable = true, PrunableReason = "gone" }], directory));
    }

    [Fact]
    public void A_link_resolved_spelling_matches_and_joins_the_list_under_forges_spelling()
    {
        var target = Path.Combine(root, "real-wt");
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "linked-wt");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege (no developer mode): a junction links a directory without it.
            Assert.True(OperatingSystem.IsWindows(), "symbolic link failed: " + exception.Message);
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            junction.WaitForExit();
            Assert.True(junction.ExitCode == 0, "mklink /J failed: " + junction.StandardError.ReadToEnd());
        }

        IReadOnlyList<GitWorktree> list = [Listed(Repository.RootPath), Listed(target)];

        Assert.Equal(target, WorktreeList.Find(list, link)?.Path);
        var spelled = WorktreeList.WithSpellings(list, [link, Path.Combine(root, "elsewhere")]);
        Assert.Equal(3, spelled.Count);
        Assert.Contains(spelled, worktree => worktree.Path == link);
        Assert.Same(list, WorktreeList.WithSpellings(list, [target]));
    }

    private static GitWorktree Listed(string path) =>
        new(path, "abc123", null, IsDetached: false, IsLocked: false, LockReason: null, IsPrunable: false, PrunableReason: null, IsBare: false);

    private RemovalFlow Flow() => new(
        catalog,
        catalog,
        git,
        (directory, _) =>
        {
            if (TrashFailure is { } failure)
            {
                throw failure;
            }

            trashed.Add(directory);
            Directory.Delete(directory, recursive: true);
            return Task.CompletedTask;
        },
        (id, _) =>
        {
            if (CleanupFailure is { } failure)
            {
                throw failure;
            }

            if (string.Equals(id, FailCleanupOf, StringComparison.Ordinal))
            {
                throw new IOException("artifact locked");
            }

            cleaned.Add(id);
            return Task.CompletedTask;
        });

    private (string Directory, SessionInfo Session) Worktree(string name, string title, SessionStatus status = SessionStatus.Active)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var session = catalog.Add(Session(title, directory, status) with { WorktreePath = directory, Branch = name });
        return (directory, session);
    }

    private SessionInfo Session(string title, string directory, SessionStatus status) =>
        new(Guid.NewGuid().ToString("N"), directory, title, DateTimeOffset.UnixEpoch)
        {
            ProjectId = Repository.Id,
            Status = status,
        };

    private sealed class FakeGit : IRemovalGit
    {
        public Queue<IReadOnlyList<string>> Reasons { get; } = new();

        public Exception? GuardFailure { get; set; }

        /// <summary>The answers of the commit check from the repository, in order; none left answers clean.</summary>
        public Queue<IReadOnlyList<string>> CommitReasons { get; } = new();

        public Exception? CommitFailure { get; set; }

        public int CommitCalls { get; private set; }

        public (string Head, bool Detached, string? BaseBranch)? LastCommitCheck { get; private set; }

        public Exception? PruneFailure { get; set; }

        /// <summary>The test's temporary root: with no <see cref="Worktrees"/> set, every folder under it is registered.</summary>
        public string Root { get; set; } = "";

        /// <summary>The worktree list; null (the default) registers every existing folder under <see cref="Root"/>.</summary>
        public IReadOnlyList<GitWorktree>? Worktrees { get; set; }

        public Exception? ListFailure { get; set; }

        public int GuardCalls { get; private set; }

        public int Prunes { get; private set; }

        public Task<IReadOnlyList<string>> CheckRemovableAsync(string repositoryRoot, string directory, string? baseBranch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            GuardCalls++;
            if (GuardFailure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(Reasons.Count > 0 ? Reasons.Dequeue() : (IReadOnlyList<string>)[]);
        }

        public Task<IReadOnlyList<string>> CheckCommitsAsync(string repositoryRoot, string head, bool detached, string? baseBranch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CommitCalls++;
            LastCommitCheck = (head, detached, baseBranch);
            if (CommitFailure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(CommitReasons.Count > 0 ? CommitReasons.Dequeue() : (IReadOnlyList<string>)[]);
        }

        public Task PruneAsync(string repositoryRoot, CancellationToken ct)
        {
            Prunes++;
            return PruneFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public Task<bool> IsAncestorAsync(string repositoryRoot, string ancestor, string descendant, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryRoot, string branch, CancellationToken ct) => Task.FromResult(true);

        public Task AttachWorktreeAsync(string repositoryRoot, string directory, string branch, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string repositoryRoot, CancellationToken ct) =>
            ct.IsCancellationRequested
                ? Task.FromCanceled<IReadOnlyList<GitWorktree>>(ct)
                : ListFailure is { } failure
                ? Task.FromException<IReadOnlyList<GitWorktree>>(failure)
                : Task.FromResult(Worktrees ?? [.. Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).Select(Listed)]);
    }

    /// <summary>In-memory catalog and store: the members <see cref="RemovalFlow"/> uses, with injectable failures.</summary>
    private sealed class FakeCatalog : ISessionCatalog, ISessionStore
    {
        private readonly List<SessionInfo> rows = [];

        public Exception? SoftDeleteFailure { get; set; }

        public Exception? PurgeFailure { get; set; }

        public SessionInfo Add(SessionInfo session)
        {
            rows.Add(session);
            return session;
        }

        public void Replace(SessionInfo session) => rows[rows.FindIndex(r => r.Id == session.Id)] = session;

        public SessionInfo? Find(string id) => rows.Find(r => r.Id == id);

        public SessionInfo Get(string id) => Assert.IsType<SessionInfo>(Find(id));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>(rows
                .Where(r => r.Status != SessionStatus.Deleted && (query.ProjectId is null || r.ProjectId == query.ProjectId))
                .ToArray());

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult(Find(sessionId));

        public Task<int> SoftDeleteAsync(IReadOnlyList<string> sessionIds, CancellationToken ct)
        {
            if (SoftDeleteFailure is { } failure)
            {
                throw failure;
            }

            var pending = new Queue<string>(sessionIds);
            var changed = 0;
            while (pending.Count > 0)
            {
                var id = pending.Dequeue();
                Replace(Get(id) with { Status = SessionStatus.Deleted });
                changed++;
                foreach (var child in rows.Where(r => r.ParentSessionId == id).ToArray())
                {
                    pending.Enqueue(child.Id);
                }
            }

            return Task.FromResult(changed);
        }

        public Task<IReadOnlyList<string>> PurgeAsync(string sessionId, CancellationToken ct)
        {
            if (PurgeFailure is { } failure)
            {
                throw failure;
            }

            var purged = new List<string> { sessionId };
            purged.AddRange(rows.Where(r => r.ParentSessionId == sessionId).Select(static r => r.Id));
            rows.RemoveAll(r => purged.Contains(r.Id));
            return Task.FromResult<IReadOnlyList<string>>(purged);
        }

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) => throw new NotSupportedException();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
