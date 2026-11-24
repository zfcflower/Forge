using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A handover hands the workspace over, and the old root's still-active agent sessions follow it (the same rule an
/// archive-with-successor applies): left under the archived old root they would surface in the branch's history group
/// while never having been archived. The archived agent sessions stay with the old root - an archived root keeps its
/// archived family in history.
/// </summary>
public sealed class HandoverAgentRehomeTests : IDisposable
{
    private const string Worktree = "/repo/worktree-a";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tests-handover-rehome-" + Guid.NewGuid().ToString("N"));

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
    public async Task A_new_session_takes_the_roots_active_agent_sessions_with_it()
    {
        await using var store = new SqliteSessionStore(directory);
        var oldRoot = await store.CreateAsync(Session("old root"), CancellationToken.None);
        var active = await store.CreateAsync(Session("active worker", oldRoot.Id), CancellationToken.None);
        var archived = await store.CreateAsync(Session("archived worker", oldRoot.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(archived.Id, SessionStatus.Archived, CancellationToken.None));

        var taker = await store.HandoverAsync(
            oldRoot.Id,
            new HandoverTarget.NewSession(Session("new root")) { InitialMessages = [ChatMessage.User("over to you")] },
            CancellationToken.None);

        Assert.Equal(SessionStatus.Active, taker.Status);
        Assert.Null(taker.ParentSessionId);
        Assert.Equal(taker.Id, (await store.GetSessionAsync(active.Id, CancellationToken.None))!.ParentSessionId);
        Assert.Equal(SessionStatus.Active, (await store.GetSessionAsync(active.Id, CancellationToken.None))!.Status);
        // The archived worker never left the old root: it is history, not a live row gone astray.
        Assert.Equal(oldRoot.Id, (await store.GetSessionAsync(archived.Id, CancellationToken.None))!.ParentSessionId);
        Assert.Equal(SessionStatus.Archived, (await store.GetSessionAsync(oldRoot.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task A_resumed_session_takes_the_roots_active_agent_sessions_with_it()
    {
        await using var store = new SqliteSessionStore(directory);
        var resumed = await store.CreateAsync(Session("old root"), CancellationToken.None);
        Assert.Null(await store.ArchiveAndPromoteAsync(resumed.Id, null, CancellationToken.None));
        var holder = await store.CreateAsync(Session("current root"), CancellationToken.None);
        var active = await store.CreateAsync(Session("active worker", holder.Id), CancellationToken.None);
        var archived = await store.CreateAsync(Session("archived worker", holder.Id), CancellationToken.None);
        Assert.True(await store.SetStatusAsync(archived.Id, SessionStatus.Archived, CancellationToken.None));

        var taker = await store.HandoverAsync(holder.Id, new HandoverTarget.Resume(resumed.Id), CancellationToken.None);

        Assert.Equal(resumed.Id, taker.Id);
        Assert.Equal(SessionStatus.Active, taker.Status);
        Assert.Null(taker.ParentSessionId);
        Assert.Equal(resumed.Id, (await store.GetSessionAsync(active.Id, CancellationToken.None))!.ParentSessionId);
        Assert.Equal(holder.Id, (await store.GetSessionAsync(archived.Id, CancellationToken.None))!.ParentSessionId);
        Assert.Equal(SessionStatus.Archived, (await store.GetSessionAsync(holder.Id, CancellationToken.None))!.Status);
    }
}
