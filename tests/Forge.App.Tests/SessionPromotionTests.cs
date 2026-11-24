using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Who is the branch's root after a promotion or a restore (forge-core "Agent 会话"): every agent session of an archived
/// root follows the promoted root, so the tree keeps listing it (a non-root never expands its own agent sessions), and
/// restoring an agent session into a branch without an active root makes it that root - its archived parent becomes an
/// agent of it.
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
        // The old root is history: it stays the root it was, alone in the branch's past.
        var untouched = await store.GetSessionAsync(oldRoot.Id, CancellationToken.None);
        Assert.Null(untouched!.ParentSessionId);
        Assert.Equal(SessionStatus.Archived, untouched.Status);
    }

    [Fact]
    public async Task Restoring_a_root_while_another_root_is_active_is_refused()
    {
        await using var store = new SqliteSessionStore(directory);
        var archived = await store.CreateAsync(Session("archived root"), CancellationToken.None);
        Assert.Null(await store.ArchiveAndPromoteAsync(archived.Id, null, CancellationToken.None));
        var holder = await store.CreateAsync(Session("active root"), CancellationToken.None);

        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(
            () => store.ActivateAsync(archived.Id, CancellationToken.None));

        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(SessionStatus.Archived, (await store.GetSessionAsync(archived.Id, CancellationToken.None))!.Status);
    }
}
