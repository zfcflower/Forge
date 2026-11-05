using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins the SqliteSessionStore side of ISessionCatalog: full field round trip, every
/// SessionQuery filter, activity ordering, missing-id results and malformed-row failures.</summary>
public sealed class SqliteSessionCatalogTests
{
    private static SessionCreate FullRequest(
        string workspace = "D:/ws",
        string title = "t",
        string? parent = null) => new(workspace, title)
    {
        ProjectId = "proj-1",
        WorktreePath = "D:/wt/1",
        Branch = "feature/x",
        ParentSessionId = parent,
        AgentKind = "Explore",
        Task = "find things",
    };

    [Fact]
    public async Task Create_and_get_round_trip_every_catalog_field()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;

        var created = await catalog.CreateAsync(FullRequest(), default);
        Assert.Equal(SessionStatus.Active, created.Status);
        Assert.Equal(created.CreatedAt, created.LastActiveAt);

        var loaded = await store.GetSessionAsync(created.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(created, loaded);
        Assert.Equal("proj-1", loaded.ProjectId);
        Assert.Equal("D:/wt/1", loaded.WorktreePath);
        Assert.Equal("feature/x", loaded.Branch);
        Assert.Null(loaded.ParentSessionId);
        Assert.Equal("Explore", loaded.AgentKind);
        Assert.Equal("find things", loaded.Task);

        var child = await catalog.CreateAsync(FullRequest(parent: created.Id), default);
        Assert.Equal(created.Id, (await store.GetSessionAsync(child.Id, default))?.ParentSessionId);
    }

    [Fact]
    public async Task Query_supports_every_filter()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;

        var main1 = await catalog.CreateAsync(new SessionCreate("D:/ws", "m1") { ProjectId = "p1", WorktreePath = "w1" }, default);
        var child = await catalog.CreateAsync(
            new SessionCreate("D:/ws", "c1") { ProjectId = "p1", WorktreePath = "w2", ParentSessionId = main1.Id }, default);
        var main2 = await catalog.CreateAsync(new SessionCreate("D:/ws", "m2") { ProjectId = "p2", WorktreePath = "w1" }, default);
        Assert.True(await catalog.SetStatusAsync(main2.Id, SessionStatus.Archived, default));

        Assert.Equal(
            new[] { child.Id, main1.Id }.OrderBy(static id => id),
            (await catalog.QueryAsync(new SessionQuery { ProjectId = "p1" }, default)).Select(s => s.Id).OrderBy(static id => id));
        Assert.Equal(
            new[] { main1.Id, main2.Id }.OrderBy(static id => id),
            (await catalog.QueryAsync(new SessionQuery { WorktreePath = "w1" }, default)).Select(s => s.Id).OrderBy(static id => id));
        Assert.Equal(
            [main2.Id],
            (await catalog.QueryAsync(new SessionQuery { Status = SessionStatus.Archived }, default)).Select(s => s.Id));
        Assert.Equal(
            new[] { child.Id, main1.Id }.OrderBy(static id => id),
            (await catalog.QueryAsync(new SessionQuery { Status = SessionStatus.Active, ProjectId = "p1" }, default)).Select(s => s.Id).OrderBy(static id => id));

        // Null parent = no filter; empty parent = main sessions only; an id = its children.
        Assert.Equal(3, (await catalog.QueryAsync(new SessionQuery(), default)).Count);
        Assert.Equal(
            new[] { main1.Id, main2.Id }.OrderBy(static id => id),
            (await catalog.QueryAsync(new SessionQuery { ParentSessionId = string.Empty }, default)).Select(s => s.Id).OrderBy(static id => id));
        Assert.Equal(
            [child.Id],
            (await catalog.QueryAsync(new SessionQuery { ParentSessionId = main1.Id }, default)).Select(s => s.Id));
        Assert.Empty(await catalog.QueryAsync(new SessionQuery { ProjectId = "missing" }, default));
    }

    [Fact]
    public async Task Query_orders_by_last_activity_descending_after_touch()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;

        var first = await catalog.CreateAsync(new SessionCreate("D:/ws", "first"), default);
        var second = await catalog.CreateAsync(new SessionCreate("D:/ws", "second"), default);
        Assert.Equal(second.Id, (await catalog.QueryAsync(new SessionQuery(), default))[0].Id);

        var later = DateTimeOffset.UtcNow.AddHours(1);
        await catalog.TouchAsync(first.Id, later, default);

        var ordered = await catalog.QueryAsync(new SessionQuery(), default);
        Assert.Equal([first.Id, second.Id], ordered.Select(s => s.Id));
        Assert.Equal(later, ordered[0].LastActiveAt);
    }

    [Fact]
    public async Task SetStatus_and_rename_return_false_for_missing_ids()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;

        var missing = Guid.NewGuid().ToString("n");
        Assert.False(await catalog.SetStatusAsync(missing, SessionStatus.Archived, default));
        Assert.False(await catalog.RenameAsync(missing, "new title", default));

        var session = await catalog.CreateAsync(new SessionCreate("D:/ws", "t"), default);
        Assert.True(await catalog.SetStatusAsync(session.Id, SessionStatus.Archived, default));
        Assert.True(await catalog.RenameAsync(session.Id, "renamed", default));
        var loaded = await store.GetSessionAsync(session.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(SessionStatus.Archived, loaded.Status);
        Assert.Equal("renamed", loaded.Title);
    }

    [Fact]
    public async Task Row_with_unknown_status_text_fails_with_InvalidDataException()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        await StorageTestHelpers.ExecuteAsync(
            store.ConnectionString,
            $"UPDATE sessions SET status = 'Bogus' WHERE id = '{session.Id}'");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSessionAsync(session.Id, default));
    }
}
