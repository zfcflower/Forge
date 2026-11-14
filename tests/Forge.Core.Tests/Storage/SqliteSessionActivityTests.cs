using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// What moves <see cref="SessionInfo.LastActiveAt"/> in <see cref="SqliteSessionStore"/>. The idle-archive step of housekeeping
/// trusts that field as the only "is this session in use" signal for a session no tab has open, so the writers of it matter.
/// </summary>
public sealed class SqliteSessionActivityTests
{
    [Fact]
    public async Task Touch_sets_the_last_active_time_that_a_fresh_read_returns_and_reorders_the_list()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        var idle = await catalog.CreateAsync(new SessionCreate("D:/ws/idle", "idle"), default);
        var busy = await catalog.CreateAsync(new SessionCreate("D:/ws/busy", "busy"), default);
        var created = idle.LastActiveAt;
        var later = created.AddDays(3);

        await catalog.TouchAsync(idle.Id, later, default);

        Assert.Equal(later, (await store.GetSessionAsync(idle.Id, default))?.LastActiveAt);
        Assert.Equal(busy.LastActiveAt, (await store.GetSessionAsync(busy.Id, default))?.LastActiveAt);
        Assert.Equal([idle.Id, busy.Id], (await catalog.QueryAsync(new SessionQuery(), default)).Select(static s => s.Id));
    }

    [Fact]
    public async Task Touching_a_missing_session_changes_nothing_and_does_not_throw()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        var existing = await catalog.CreateAsync(new SessionCreate("D:/ws/one", "one"), default);

        await catalog.TouchAsync("no-such-session", DateTimeOffset.UtcNow.AddDays(1), default);

        Assert.Equal(existing.LastActiveAt, (await store.GetSessionAsync(existing.Id, default))?.LastActiveAt);
        Assert.Null(await store.GetSessionAsync("no-such-session", default));
    }

    /// <summary>
    /// DEFECT (reported, src not changed): <c>AppendOp</c> in <c>SqliteSessionStore.cs</c> only inserts the message row and
    /// never updates <c>sessions.last_active_at</c>; only <c>TouchAsync</c> does, and the agent loop calls it after each
    /// completed assistant message, not when the user message is persisted. A session whose agent just started a turn is
    /// therefore still "idle for N days" until its first model response completes.
    /// </summary>
    [Fact]
    public async Task Appending_a_message_updates_the_last_active_time()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        var session = await catalog.CreateAsync(new SessionCreate("D:/ws/chat", "chat"), default);
        var before = (await store.GetSessionAsync(session.Id, default))!.LastActiveAt;
        await Task.Delay(20);

        await store.AppendAsync(session.Id, ChatMessage.Assistant("hello"), null, default);

        Assert.True((await store.GetSessionAsync(session.Id, default))!.LastActiveAt > before);
    }
}
