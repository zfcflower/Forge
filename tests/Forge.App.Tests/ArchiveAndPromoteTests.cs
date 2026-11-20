using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Archiving a root session hands its workspace over (forge-core "Agent 会话"): the newest agent session becomes the root,
/// the archived root is demoted to an archived agent session of it, and the root's other agent sessions follow the new root.
/// The three states the task tree and the removal rules depend on.
/// </summary>
public sealed class ArchiveAndPromoteTests : IDisposable
{
    private const string Directory = "/repo/worktree-a";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tests-archive-promote-" + Guid.NewGuid().ToString("N"));

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

    private static SessionCreate AgentSession(string rootId, string title) =>
        new(Directory, title)
        {
            ProjectId = "p1",
            WorktreePath = Directory,
            ParentSessionId = rootId,
            ChildDriver = AgentSessionKinds.ChildDriverUser,
        };

    private static SessionCreate RootSession(string title) =>
        new(Directory, title)
        {
            ProjectId = "p1",
            WorktreePath = Directory,
        };

    [Fact]
    public async Task Archiving_a_root_promotes_its_most_recently_active_agent_session()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(RootSession("root"), CancellationToken.None);
        var older = await store.CreateAsync(AgentSession(root.Id, "Worker 1"), CancellationToken.None);
        var newer = await store.CreateAsync(AgentSession(root.Id, "Worker 2"), CancellationToken.None);
        // Activity, not creation order, picks the successor.
        await store.TouchAsync(older.Id, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
        await store.TouchAsync(newer.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        var promoted = await store.ArchiveAndPromoteAsync(root.Id, null, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.Equal(newer.Id, promoted.Id);
        Assert.Null(promoted.ParentSessionId);
        Assert.Null(promoted.ChildDriver);
        Assert.Equal(SessionStatus.Active, promoted.Status);

        // The archived root is demoted to an archived agent session of the session it handed over to.
        var demoted = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.NotNull(demoted);
        Assert.Equal(SessionStatus.Archived, demoted.Status);
        Assert.Equal(newer.Id, demoted.ParentSessionId);
        Assert.Equal(AgentSessionKinds.ChildDriverUser, demoted.ChildDriver);

        // The root's other agent session is still live and now belongs to the promoted root: no read-only orphan.
        var sibling = await store.GetSessionAsync(older.Id, CancellationToken.None);
        Assert.NotNull(sibling);
        Assert.Equal(SessionStatus.Active, sibling.Status);
        Assert.Equal(newer.Id, sibling.ParentSessionId);

        // The promoted session holds the workspace as its root; a second root there is still refused.
        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(
            () => store.CreateAsync(RootSession("second root"), CancellationToken.None));
        Assert.Equal(newer.Id, refused.HolderSessionId);
    }

    [Fact]
    public async Task Archiving_a_root_without_agent_sessions_archives_it_alone()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(RootSession("root"), CancellationToken.None);

        var promoted = await store.ArchiveAndPromoteAsync(root.Id, null, CancellationToken.None);

        Assert.Null(promoted);
        var archived = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.NotNull(archived);
        Assert.Equal(SessionStatus.Archived, archived.Status);
        // With no successor there is nothing to demote it to: it stays a root row.
        Assert.Null(archived.ParentSessionId);
        Assert.Null(archived.ChildDriver);
    }

    [Fact]
    public async Task Promoting_a_named_agent_session_archives_the_root_into_it()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(RootSession("root"), CancellationToken.None);
        var older = await store.CreateAsync(AgentSession(root.Id, "Worker 1"), CancellationToken.None);
        var newer = await store.CreateAsync(AgentSession(root.Id, "Worker 2"), CancellationToken.None);
        await store.TouchAsync(older.Id, DateTimeOffset.UtcNow.AddMinutes(-5), CancellationToken.None);
        await store.TouchAsync(newer.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        // "Promote to main session" names the successor, so the newest one is not the one that wins.
        var promoted = await store.ArchiveAndPromoteAsync(root.Id, older.Id, CancellationToken.None);

        Assert.NotNull(promoted);
        Assert.Equal(older.Id, promoted.Id);
        Assert.Null(promoted.ParentSessionId);
        var demoted = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.Equal(older.Id, demoted!.ParentSessionId);
        Assert.Equal(SessionStatus.Active, (await store.GetSessionAsync(newer.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_session_that_is_not_an_active_agent_session_of_the_root_cannot_be_promoted()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(RootSession("root"), CancellationToken.None);
        var child = await store.CreateAsync(AgentSession(root.Id, "Worker 1"), CancellationToken.None);
        var stranger = await store.CreateAsync(RootSession("elsewhere") with { WorktreePath = "/repo/other" }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ArchiveAndPromoteAsync(root.Id, stranger.Id, CancellationToken.None));

        // The refused write rolls back: the root is still the workspace's active root.
        var unchanged = await store.GetSessionAsync(root.Id, CancellationToken.None);
        Assert.Equal(SessionStatus.Active, unchanged!.Status);
        Assert.Equal(SessionStatus.Active, (await store.GetSessionAsync(child.Id, CancellationToken.None))!.Status);
    }
}
