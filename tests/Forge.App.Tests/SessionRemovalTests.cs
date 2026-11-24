using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Removing a history session for good (7.2): an archived session is purged with its agent sessions, while an active one is
/// refused. This is the store contract behind the tree's "remove session", which a workspace removal reuses for every
/// session on the workspace.
/// </summary>
public sealed class SessionRemovalTests : IDisposable
{
    private const string Worktree = "/repo/worktree-a";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tests-session-removal-" + Guid.NewGuid().ToString("N"));

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

    private static SessionCreate Session(string title, string? parentId = null) =>
        new(Worktree, title)
        {
            ProjectId = "p1",
            WorktreePath = Worktree,
            ParentSessionId = parentId,
            ChildDriver = parentId is null ? null : AgentSessionKinds.ChildDriverUser,
        };

    [Fact]
    public async Task Purging_an_archived_session_removes_its_archived_agent_sessions_too()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(Session("root"), CancellationToken.None);
        var child = await store.CreateAsync(Session("worker", root.Id), CancellationToken.None);

        // The child leaves Active first, so archiving the root has no successor and only the root leaves the workspace.
        Assert.True(await store.SetStatusAsync(child.Id, SessionStatus.Archived, CancellationToken.None));
        Assert.Null(await store.ArchiveAndPromoteAsync(root.Id, null, CancellationToken.None));
        Assert.Equal(SessionStatus.Archived, (await store.GetSessionAsync(root.Id, CancellationToken.None))!.Status);

        var purged = await store.PurgeAsync(root.Id, CancellationToken.None);

        Assert.Equal([root.Id, child.Id], purged);
        Assert.Null(await store.GetSessionAsync(root.Id, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(child.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Purging_an_active_session_is_refused_and_changes_nothing()
    {
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(Session("root"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurgeAsync(root.Id, CancellationToken.None));

        Assert.Contains("only an archived or removed session", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionStatus.Active, (await store.GetSessionAsync(root.Id, CancellationToken.None))!.Status);
    }
}
