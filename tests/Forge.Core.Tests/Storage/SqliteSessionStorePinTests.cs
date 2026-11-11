using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

public sealed class SqliteSessionStorePinTests
{
    [Fact]
    public async Task Sessions_round_trip_all_persisted_fields()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);

        var created = await store.CreateSessionAsync("D:/ws/a", "first", default);
        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal("D:/ws/a", created.WorkspacePath);
        Assert.Equal("first", created.Title);
        Assert.True(DateTimeOffset.UtcNow - created.CreatedAt < TimeSpan.FromMinutes(5));

        var loaded = await store.GetSessionAsync(created.Id, default);
        Assert.NotNull(loaded);
        Assert.Equal(created, loaded);
        Assert.Equal("first", loaded.Title);
        Assert.Null(await store.GetSessionAsync(Guid.NewGuid().ToString("n"), default));
    }

    [Fact]
    public async Task ListSessions_orders_by_creation_descending_and_filters_by_workspace()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);

        var first = await store.CreateSessionAsync("D:/ws/a", "first", default);
        await Task.Delay(20);
        var second = await store.CreateSessionAsync("D:/ws/b", "second", default);

        var all = await store.ListSessionsAsync(null, default);
        Assert.Equal(2, all.Count);
        Assert.Equal(second.Id, all[0].Id);
        Assert.Equal(first.Id, all[1].Id);

        var filtered = await store.ListSessionsAsync("D:/ws/a", default);
        Assert.Single(filtered);
        Assert.Equal(first.Id, filtered[0].Id);
        Assert.Empty(await store.ListSessionsAsync("D:/ws/missing", default));
    }

    [Fact]
    public async Task Append_is_monotonic_per_session_and_independent_across_sessions()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var first = await store.CreateSessionAsync("D:/ws/one", "one", default);
        var second = await store.CreateSessionAsync("D:/ws/two", "two", default);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(i, await store.AppendAsync(first.Id, Text($"a{i}"), null, default));
        }

        Assert.Equal(1, await store.AppendAsync(second.Id, Text("b1"), null, default));
        Assert.Equal(3, await store.LastSeqAsync(first.Id, default));
        Assert.Equal(1, await store.LastSeqAsync(second.Id, default));
        Assert.Equal(0, await store.LastSeqAsync(Guid.NewGuid().ToString("n"), default));

        var stored = await CollectAsync(store.ReadAsync(first.Id, 2, 3, default));
        Assert.Equal(new long[] { 2, 3 }, stored.Select(m => m.Seq));
    }

    [Fact]
    public async Task Summaries_round_trip_in_order()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        Assert.Empty(await store.GetSummariesAsync(session.Id, default));
        var first = new SummaryRecord(session.Id, 1, 1, 4, "stage one", DateTimeOffset.UtcNow);
        var second = new SummaryRecord(session.Id, 2, 1, 8, "session view", DateTimeOffset.UtcNow);
        await store.PutSummaryAsync(first, default);
        await store.PutSummaryAsync(second, default);

        var loaded = await store.GetSummariesAsync(session.Id, default);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("stage one", loaded[0].Text);
        Assert.Equal("session view", loaded[1].Text);
        Assert.Empty(await store.GetSummariesAsync(Guid.NewGuid().ToString("n"), default));
    }

    [Fact]
    public async Task Search_finds_compacted_and_recent_text()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);
        await store.AppendAsync(session.Id, Text("the quick brown fox"), null, default);
        await store.AppendAsync(session.Id, Text("entirely unrelated"), null, default);

        var hits = await store.SearchAsync(session.Id, "quick brown", 10, default);
        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0].Seq);
        Assert.Empty(await store.SearchAsync(session.Id, "no such phrase xyz", 10, default));
        Assert.Empty(await store.SearchAsync(session.Id, "   ", 10, default));
        Assert.Empty(await store.SearchAsync(session.Id, "quick", 0, default));
    }

    [Fact]
    public async Task Concurrent_open_of_a_fresh_db_by_five_stores_all_succeed()
    {
        using var directory = new TempDirectory();
        var stores = new List<SqliteSessionStore>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                stores.Add(new SqliteSessionStore(directory.Path));
            }

            var created = await Task.WhenAll(stores.Select((store, i) =>
                store.CreateSessionAsync("D:/ws/s" + i, "s" + i, default))).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(5, created.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());

            var listed = await stores[0].ListSessionsAsync(null, default);
            Assert.Equal(5, listed.Count);
        }
        finally
        {
            foreach (var store in stores)
            {
                await store.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Failed_appends_do_not_corrupt_sequence_ordering()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync("D:/ws", "t", default);

        // An invalid segment name must be rejected before touching the writer queue.
        await Assert.ThrowsAnyAsync<Exception>(() => store.AppendAsync("../escape", Text("x"), null, default));
        Assert.Equal(1, await store.AppendAsync(session.Id, Text("ok"), null, default));
        var stored = await CollectAsync(store.ReadAsync(session.Id, 1, null, default));
        Assert.Single(stored);
        Assert.Equal("ok", ((TextBlock)stored[0].Message.Content[0]).Text);
    }

    [Fact]
    public async Task Future_user_version_fails_closed_without_touching_the_file()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteSessionStore(directory.Path))
        {
            await store.CreateSessionAsync("D:/ws", "t", default);
        }

        var path = Path.Combine(directory.Path, "forge.db");
        await using (var connection = new SqliteConnection(directory.ConnectionString.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 9999";
            await command.ExecuteNonQueryAsync();
        }

        // Bytes are snapshotted after the version bump: the failed open below must not touch them.
        var preserved = File.ReadAllBytes(path);
        await using (var reopened = new SqliteSessionStore(directory.Path))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.CreateSessionAsync("D:/ws", "t2", default));
        }

        Assert.Equal(preserved, File.ReadAllBytes(path));
    }
}
