using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Who is the branch's root after a takeover or a restore (forge-core "Agent 会话"): every agent session of an archived
/// root follows the promoted root, so the tree keeps listing it (a non-root never expands its own agent sessions),
/// restoring an agent session into a branch without an active root makes it that root - its archived parent becomes an
/// agent of it - and the branch's root rows never stack up: whatever new root takes a workspace (a promotion, a
/// create, a handover) demotes the root rows its history still holds under it.
/// </summary>
public sealed class SessionPromotionTests : IDisposable
{
    private const string Worktree = "/repo/worktree-a";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tests-session-promotion-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    private static SessionCreate Session(string title, string? parentId = null, string worktree = Worktree) =>
        new(worktree, title)
        {
            ProjectId = "p1",
            WorktreePath = worktree,
            ParentSessionId = parentId,
            ChildDriver = parentId is null ? null : AgentSessionKinds.ChildDriverUser,
        };

    [Fact]
    public async Task Promoting_an_agent_moves_the_roots_archived_agent_sessions_too()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(Session("root"), CancellationToken.None);
        var successor = await store.CreateAsync(Session("worker", root.Id), CancellationToken.None);
        var archived = await store.CreateAsync(Session("older worker", root.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(archived.Id, SessionStatus.Archived, CancellationToken.None));

        var promoted = await store.ArchiveAndPromoteAsync(root.Id, null, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.Equal(successor.Id, promoted.Id);
        Assert.Equal(successor.Id, (await store.GetSessionAsync(archived.Id, CancellationToken.None))!.ParentSessionId);
        Assert.Equal(successor.Id, (await store.GetSessionAsync(root.Id, CancellationToken.None))!.ParentSessionId);
    }

    [Fact]
    public async Task Restoring_an_agent_session_without_an_active_root_makes_it_the_root()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(Session("root"), CancellationToken.None);
        var restored = await store.CreateAsync(Session("worker", root.Id), CancellationToken.None);
        var sibling = await store.CreateAsync(Session("other worker", root.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(restored.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.True(await store.SetStatusAsync(sibling.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(root.Id, null, CancellationToken.None));

        Assert.True(await store.ActivateAsync(restored.Id, CancellationToken.None));

        var promoted = await store.GetSessionAsync(restored.Id, CancellationToken.None);
        Assert.NotNull(promoted);
        Assert.Equal(SessionStatus.Active, promoted.Status);
        Assert.Null(promoted.ParentSessionId);
        Assert.Null(promoted.ChildDriver);

        // The archived root it hung under is an agent session of it now, and the other agent sessions follow that root.
        var demoted = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, demoted!.Status);
        Assert.Equal(promoted.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);
        Assert.Equal(promoted.Id, (await store.GetSessionAsync(sibling.Id, CancellationToken.None))!.ParentSessionId);
    }

    [Fact]
    public async Task Restoring_an_agent_session_under_an_active_root_joins_that_root()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("old root"), CancellationToken.None);
        var restored = await store.CreateAsync(Session("worker", oldRoot.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(restored.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(oldRoot.Id, null, CancellationToken.None));
        var activeRoot = await store.CreateAsync(Session("new root"), CancellationToken.None);

        Assert.True(await store.ActivateAsync(restored.Id, CancellationToken.None));

        var joined = await store.GetSessionAsync(restored.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Active, joined!.Status);
        Assert.Equal(activeRoot.Id, joined.ParentSessionId);
        // The old root was demoted under the new root when the new root took the workspace: the branch's root rows
        // do not stack up in history, and the new root is the only one it has.
        var demoted = await store.GetSessionAsync(oldRoot.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, demoted!.Status);
        Assert.Equal(activeRoot.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);
    }

    [Fact]
    public async Task Creating_a_new_root_demotes_the_roots_the_branch_still_holds()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("root"), CancellationToken.None);
        var worker = await store.CreateAsync(Session("worker", oldRoot.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(worker.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(oldRoot.Id, null, CancellationToken.None));

        var taken = await store.CreateAsync(Session("new root"), CancellationToken.None);

        // The branch's one root is the new session; the archived root it still held is an agent session of it now,
        // its own agent family following - nothing is left behind a row the tree never expands.
        var demoted = await store.GetSessionAsync(oldRoot.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, demoted!.Status);
        Assert.Equal(taken.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);
        Assert.Equal(taken.Id, (await store.GetSessionAsync(worker.Id, CancellationToken.None))!.ParentSessionId);
    }

    [Fact]
    public async Task A_demoted_root_row_always_lands_as_archived()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("old root"), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(oldRoot.Id, SessionStatus.Deleted, CancellationToken.None));

        var taken = await store.CreateAsync(Session("new root"), CancellationToken.None);

        // A removed root row the sweep picks up lands as an archived agent session of the new root: the demotion is
        // uniform, so the branch's history never holds a "removed root" that is in fact just another agent of it.
        var demoted = await store.GetSessionAsync(oldRoot.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, demoted!.Status);
        Assert.Equal(taken.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);
    }

    [Fact]
    public async Task Promoting_an_agent_session_lands_its_deleted_parent_as_archived()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(Session("root"), CancellationToken.None);
        var restored = await store.CreateAsync(Session("worker", root.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(root.Id, SessionStatus.Deleted, CancellationToken.None));
        Assert.True(await store.SetStatusAsync(restored.Id, SessionStatus.Deleted, CancellationToken.None));

        Assert.True(await store.ActivateAsync(restored.Id, CancellationToken.None));

        // The restored worker takes the branch, and the self-heal-removed root it hung under lands as an archived
        // agent row of it: the demotion is uniform, so no "removed root" survives as anything but an agent's history.
        var demoted = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, demoted!.Status);
        Assert.Equal(restored.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);
    }

    [Fact]
    public async Task Restoring_a_root_the_branch_still_holds_joins_the_active_root()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("old root"), CancellationToken.None);
        var worker = await store.CreateAsync(Session("worker", oldRoot.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(worker.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(oldRoot.Id, null, CancellationToken.None));
        var holder = await store.CreateAsync(Session("active root"), CancellationToken.None);
        // A database from before the one-root rule: the archived root is still a root row of the occupied workspace,
        // with its agent family still under it. No public write produces that shape any more, so the row is shaped
        // directly here - the restore is what repairs it.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var shape = connection.CreateCommand();
            shape.CommandText = "UPDATE sessions SET parent_session_id = NULL, child_driver = NULL WHERE id = $old; "
                + "UPDATE sessions SET parent_session_id = $old WHERE id = $worker";
            shape.Parameters.AddWithValue("$old", oldRoot.Id);
            shape.Parameters.AddWithValue("$worker", worker.Id);
            await shape.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.True(await store.ActivateAsync(oldRoot.Id, CancellationToken.None));

        // The restored root joins the branch's active root as an agent session - a restore never refuses on
        // occupancy - and the family it still held follows it in.
        var joined = await store.GetSessionAsync(oldRoot.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Active, joined!.Status);
        Assert.Equal(holder.Id, joined.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, joined.ChildDriver);
        Assert.Equal(holder.Id, (await store.GetSessionAsync(worker.Id, CancellationToken.None))!.ParentSessionId);
    }

    [Fact]
    public async Task Restoring_a_root_revives_its_deleted_children_before_they_follow()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("old root"), CancellationToken.None);
        var worker = await store.CreateAsync(Session("worker", oldRoot.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(worker.Id, SessionStatus.Deleted, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(oldRoot.Id, null, CancellationToken.None));
        var holder = await store.CreateAsync(Session("active root"), CancellationToken.None);
        // The legacy shape again: the archived root is still a root row, its removed worker still under it.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var shape = connection.CreateCommand();
            shape.CommandText = "UPDATE sessions SET parent_session_id = NULL, child_driver = NULL WHERE id = $old; "
                + "UPDATE sessions SET parent_session_id = $old WHERE id = $worker";
            shape.Parameters.AddWithValue("$old", oldRoot.Id);
            shape.Parameters.AddWithValue("$worker", worker.Id);
            await shape.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.True(await store.ActivateAsync(oldRoot.Id, CancellationToken.None));

        // The removed worker comes back as an archived row of the root it followed in - the revival cannot depend
        // on which way the adoption moves send it, so the same restore never leaves it removed one way and
        // revived the other.
        var revived = await store.GetSessionAsync(worker.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Archived, revived!.Status);
        Assert.Equal(holder.Id, revived.ParentSessionId);
    }
}
