using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins SqliteTodoStore: dense session-local ordering, afterId inserts, null-preserving
/// updates, clamping moves, re-densifying removes, session isolation and Changed semantics.</summary>
public sealed class SqliteTodoStoreTests
{
    [Fact]
    public async Task Adds_are_dense_and_zero_based()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var a = await store.AddAsync("sess-a", "A", null, null, default);
        var b = await store.AddAsync("sess-a", "B", "note-b", null, default);
        var c = await store.AddAsync("sess-a", "C", null, null, default);

        Assert.Equal([0, 1, 2], new[] { a.Order, b.Order, c.Order });
        Assert.Equal(["A", "B", "C"], (await store.ListAsync("sess-a", default)).Select(t => t.Title));
        Assert.Equal("note-b", (await store.ListAsync("sess-a", default))[1].Note);
        Assert.All(await store.ListAsync("sess-a", default), t => Assert.Equal(TodoStatus.Pending, t.Status));
    }

    [Fact]
    public async Task Add_after_id_inserts_directly_behind()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var a = await store.AddAsync("sess-a", "A", null, null, default);
        await store.AddAsync("sess-a", "B", null, null, default);
        await store.AddAsync("sess-a", "C", null, null, default);

        var x = await store.AddAsync("sess-a", "X", null, a.Id, default);
        Assert.Equal(1, x.Order);
        Assert.Equal(["A", "X", "B", "C"], (await store.ListAsync("sess-a", default)).Select(t => t.Title));
        Assert.Equal([0, 1, 2, 3], (await store.ListAsync("sess-a", default)).Select(t => t.Order));
    }

    [Fact]
    public async Task Add_with_unknown_or_foreign_after_id_throws_ArgumentException()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.AddAsync("sess-a", "A", null, null, default);
        var foreign = await store.AddAsync("sess-b", "Foreign", null, null, default);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("sess-a", "X", null, Guid.NewGuid().ToString("n"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("sess-a", "Y", null, foreign.Id, default));
        Assert.Single(await store.ListAsync("sess-a", default));
    }

    [Fact]
    public async Task Update_with_nulls_keeps_fields_and_missing_returns_null()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var item = await store.AddAsync("sess-a", "Title", "keep me", null, default);
        var updated = await store.UpdateAsync(item.Id, null, TodoStatus.InProgress, null, default);
        Assert.NotNull(updated);
        Assert.Equal("Title", updated.Title);
        Assert.Equal("keep me", updated.Note);
        Assert.Equal(TodoStatus.InProgress, updated.Status);
        Assert.Equal(item.Order, updated.Order);

        var renamed = await store.UpdateAsync(item.Id, "New title", null, "new note", default);
        Assert.NotNull(renamed);
        Assert.Equal("New title", renamed.Title);
        Assert.Equal("new note", renamed.Note);
        Assert.Equal(TodoStatus.InProgress, renamed.Status);

        Assert.Null(await store.UpdateAsync(Guid.NewGuid().ToString("n"), "T", null, null, default));
    }

    [Fact]
    public async Task Move_clamps_reorders_and_reports_missing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var a = await store.AddAsync("sess-a", "A", null, null, default);
        var b = await store.AddAsync("sess-a", "B", null, null, default);
        var c = await store.AddAsync("sess-a", "C", null, null, default);

        Assert.True(await store.MoveAsync(c.Id, 100, default));
        Assert.Equal(["A", "B", "C"], (await store.ListAsync("sess-a", default)).Select(t => t.Title));

        Assert.True(await store.MoveAsync(c.Id, 0, default));
        Assert.Equal(["C", "A", "B"], (await store.ListAsync("sess-a", default)).Select(t => t.Title));
        Assert.Equal([0, 1, 2], (await store.ListAsync("sess-a", default)).Select(t => t.Order));

        Assert.True(await store.MoveAsync(a.Id, 1, default));
        Assert.Equal(["C", "A", "B"], (await store.ListAsync("sess-a", default)).Select(t => t.Title));

        Assert.False(await store.MoveAsync(Guid.NewGuid().ToString("n"), 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.MoveAsync(b.Id, -1, default));
    }

    [Fact]
    public async Task Remove_re_densifies_and_missing_returns_false()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var a = await store.AddAsync("sess-a", "A", null, null, default);
        var b = await store.AddAsync("sess-a", "B", null, null, default);
        var c = await store.AddAsync("sess-a", "C", null, null, default);

        Assert.True(await store.RemoveAsync(b.Id, default));
        var remaining = await store.ListAsync("sess-a", default);
        Assert.Equal([a.Id, c.Id], remaining.Select(t => t.Id));
        Assert.Equal([0, 1], remaining.Select(t => t.Order));

        Assert.False(await store.RemoveAsync(b.Id, default));
        Assert.False(await store.RemoveAsync(Guid.NewGuid().ToString("n"), default));
    }

    [Fact]
    public async Task Sessions_are_isolated()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.AddAsync("sess-a", "A", null, null, default);
        await store.AddAsync("sess-b", "B", null, null, default);

        Assert.Equal("A", Assert.Single(await store.ListAsync("sess-a", default)).Title);
        Assert.Equal("B", Assert.Single(await store.ListAsync("sess-b", default)).Title);
        Assert.Empty(await store.ListAsync("sess-c", default));
    }

    [Fact]
    public async Task Changed_fires_after_commit_with_the_session_id()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        string? seen = null;
        var visibleInHandler = -1;
        store.Changed += sessionId =>
        {
            seen = sessionId;
            visibleInHandler = store.ListAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult().Count;
        };

        var item = await store.AddAsync("sess-a", "A", null, null, default);
        Assert.Equal("sess-a", seen);
        Assert.Equal(1, visibleInHandler);

        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, default);
        Assert.Equal("sess-a", seen);

        await store.MoveAsync(item.Id, 0, default);
        Assert.Equal("sess-a", seen);

        Assert.True(await store.RemoveAsync(item.Id, default));
        Assert.Equal("sess-a", seen);
        Assert.Equal(0, visibleInHandler);
    }

    [Fact]
    public async Task Handler_calling_ListAsync_synchronously_does_not_deadlock()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        store.Changed += sessionId =>
            _ = store.ListAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult();

        var task = store.AddAsync("sess-a", "A", null, null, default);
        Assert.Equal("A", (await task.WaitAsync(TimeSpan.FromSeconds(15))).Title);
    }

    [Fact]
    public async Task Throwing_handler_does_not_fail_the_write_and_others_still_run()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        store.Changed += static _ => throw new InvalidOperationException("subscriber boom");
        var seen = new List<string>();
        store.Changed += seen.Add;

        var item = await store.AddAsync("sess-a", "A", null, null, default);
        Assert.Equal("A", item.Title);
        Assert.Equal(["sess-a"], seen);
        Assert.Single(await store.ListAsync("sess-a", default));
    }

    [Fact]
    public async Task Failed_operations_do_not_raise_Changed()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var count = 0;
        store.Changed += static _ => { throw new InvalidOperationException("must not run"); };
        store.Changed += _ => count++;

        Assert.False(await store.RemoveAsync(Guid.NewGuid().ToString("n"), default));
        Assert.False(await store.MoveAsync(Guid.NewGuid().ToString("n"), 0, default));
        Assert.Null(await store.UpdateAsync(Guid.NewGuid().ToString("n"), "T", null, null, default));
        Assert.Equal(0, count);
    }
}
