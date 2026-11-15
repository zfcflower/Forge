using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// The message search table follows the build, not the database: a <c>forge.db</c> whose <c>messages_fts</c> was written
/// by a build with the FTS5 module stays writable and searchable in a build without it, because that build then keeps
/// <c>messages_text</c> - created and filled from <c>messages</c> on the first degraded open, topped up on every later
/// one. Without it the write path points at a table that does not exist, so every append fails and takes its whole
/// writer batch down.
/// <para>
/// The FTS5 module cannot be unlinked from the bundled SQLite at run time, so these tests rewrite the stored DDL of
/// <c>messages_fts</c> to a module this build does not have. That is exactly what a build without FTS5 sees: the table
/// is listed in <c>sqlite_master</c> and preparing a statement against it fails.
/// </para>
/// </summary>
public sealed class SearchFallbackSchemaTests
{
    /// <summary>A module name no SQLite build carries; standing in for "this build has no FTS5".</summary>
    private const string AbsentModule = "fts5_absent";

    private const string Fts5Module = "fts5";

    [Fact]
    public async Task An_index_this_build_cannot_read_gets_a_backfilled_fallback_table_and_appends_keep_working()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            Assert.Equal(1, await ftsBuild.AppendAsync(session.Id, Text("the quick brown fox jumps"), null, default));
            Assert.True(ftsBuild.Fts5Available);
        }

        await SetFtsModuleAsync(directory, AbsentModule);

        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Single(await CollectAsync(store.ReadAsync(sessionId, 1, null, default)));
        Assert.False(store.Fts5Available);

        // The other build's index is left alone and the fallback table it never had is created already backfilled.
        Assert.Equal(1L, await CountAsync(directory, "SELECT count(*) FROM sqlite_master WHERE name = 'messages_fts'"));
        Assert.Equal(
            "the quick brown fox jumps",
            await TextAsync(directory, $"SELECT text FROM messages_text WHERE session_id = '{sessionId}' AND seq = 1"));

        // The append that used to fail on "no such table: messages_text" writes both of its rows.
        Assert.Equal(2, await store.AppendAsync(sessionId, Text("a second message"), null, default));
        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{sessionId}'"));
        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages WHERE session_id = '{sessionId}'"));

        // Search degrades to LIKE over the fallback table and finds what the other build indexed and what this one wrote.
        Assert.Equal([1L], (await store.SearchAsync(sessionId, "quick brown", 10, default)).Select(static hit => hit.Seq));
        Assert.Equal([2L], (await store.SearchAsync(sessionId, "second", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task A_fallback_table_left_behind_by_a_degraded_build_is_topped_up_when_the_build_degrades_again()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            await ftsBuild.AppendAsync(session.Id, Text("first message"), null, default);
        }

        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(1, await degraded.LastSeqAsync(sessionId, default));
            Assert.False(degraded.Fts5Available);
            // Written to the fallback table only: an FTS build reads and writes its own index.
            await degraded.AppendAsync(sessionId, Text("second message"), null, default);
        }

        await SetFtsModuleAsync(directory, Fts5Module);
        await using (var ftsAgain = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(2, await ftsAgain.LastSeqAsync(sessionId, default));
            Assert.True(ftsAgain.Fts5Available);
            await ftsAgain.AppendAsync(sessionId, Text("third message"), null, default);
        }

        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{sessionId}'"));

        await SetFtsModuleAsync(directory, AbsentModule);
        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Equal(3, await store.LastSeqAsync(sessionId, default));
        Assert.False(store.Fts5Available);

        // The message the FTS5 build stored while the fallback table sat idle is searchable again, not silently lost.
        Assert.Equal(3L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{sessionId}'"));
        Assert.Equal(
            [1L, 2L, 3L],
            (await store.SearchAsync(sessionId, "message", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task A_purge_clears_the_fallback_table_a_degraded_open_left_behind_so_no_build_finds_the_text_again()
    {
        using var directory = new TempDirectory();
        string purgedId;
        string keptId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var purged = await ftsBuild.CreateSessionAsync("D:/ws", "purged", default);
            var kept = await ftsBuild.CreateSessionAsync("D:/other", "kept", default);
            purgedId = purged.Id;
            keptId = kept.Id;
            await ftsBuild.AppendAsync(purged.Id, Text("quokka purged"), null, default);
            await ftsBuild.AppendAsync(kept.Id, Text("quokka kept"), null, default);
        }

        // A degraded open creates and fills the fallback table; its own append never reaches the FTS5 index.
        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            await degraded.AppendAsync(purgedId, Text("a second purged message"), null, default);
            Assert.False(degraded.Fts5Available);
        }

        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{purgedId}'"));

        await SetFtsModuleAsync(directory, Fts5Module);

        // Both tables hold the purged session before the purge: one row in the index, two in the fallback table.
        Assert.Equal(1L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{purgedId}'"));

        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        Assert.Equal(1, await catalog.SoftDeleteAsync([purgedId], default));
        Assert.True(store.Fts5Available);
        Assert.Equal([purgedId], await catalog.PurgeAsync(purgedId, default));

        // Neither search table may keep the purged text: this build searches the index, a later degraded one the table.
        foreach (var table in new[] { "messages", "messages_fts", "messages_text" })
        {
            Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM {table} WHERE session_id = '{purgedId}'"));
            Assert.Equal(1L, await CountAsync(directory, $"SELECT count(*) FROM {table} WHERE session_id = '{keptId}'"));
        }

        Assert.Empty(await store.SearchAsync(purgedId, "quokka", 10, default));
        Assert.Equal([1L], (await store.SearchAsync(keptId, "quokka", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task A_purge_run_by_a_degraded_build_clears_the_table_it_searches_and_still_deletes_the_session()
    {
        using var directory = new TempDirectory();
        string purgedId;
        string keptId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var purged = await ftsBuild.CreateSessionAsync("D:/ws", "purged", default);
            var kept = await ftsBuild.CreateSessionAsync("D:/other", "kept", default);
            purgedId = purged.Id;
            keptId = kept.Id;
            await ftsBuild.AppendAsync(purged.Id, Text("quokka purged"), null, default);
            await ftsBuild.AppendAsync(kept.Id, Text("quokka kept"), null, default);
        }

        await SetFtsModuleAsync(directory, AbsentModule);
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        Assert.Equal(1, await catalog.SoftDeleteAsync([purgedId], default));
        Assert.False(store.Fts5Available);

        // The index of a module this build does not have cannot be written - not even to be emptied - so the purge
        // clears the table it does search instead of failing on the one it cannot read.
        Assert.Equal([purgedId], await catalog.PurgeAsync(purgedId, default));
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{purgedId}'"));
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages WHERE session_id = '{purgedId}'"));
        Assert.Equal(1L, await CountAsync(directory, $"SELECT count(*) FROM messages_text WHERE session_id = '{keptId}'"));
        Assert.Empty(await store.SearchAsync(purgedId, "quokka", 10, default));
        Assert.Equal([1L], (await store.SearchAsync(keptId, "quokka", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task An_index_row_a_purge_run_without_fts5_could_not_delete_is_trimmed_when_the_module_is_back()
    {
        using var directory = new TempDirectory();
        string purgedId;
        string keptId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var purged = await ftsBuild.CreateSessionAsync("D:/ws", "purged", default);
            var kept = await ftsBuild.CreateSessionAsync("D:/other", "kept", default);
            purgedId = purged.Id;
            keptId = kept.Id;
            await ftsBuild.AppendAsync(purged.Id, Text("quokka purged"), null, default);
            await ftsBuild.AppendAsync(kept.Id, Text("quokka kept"), null, default);
        }

        // The purge of a build without the module clears the two tables it can write and leaves the one it cannot read.
        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            ISessionCatalog catalog = degraded;
            Assert.Equal(1, await catalog.SoftDeleteAsync([purgedId], default));
            Assert.Equal([purgedId], await catalog.PurgeAsync(purgedId, default));
            Assert.False(degraded.Fts5Available);
        }

        await SetFtsModuleAsync(directory, Fts5Module);

        // What the next build with the module used to find: the purged session's text still indexed, its messages gone.
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages WHERE session_id = '{purgedId}'"));
        Assert.Equal(1L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{purgedId}'"));

        await using var store = new SqliteSessionStore(directory.Path);
        // The first read is what opens the database, and opening is what trims the index.
        Assert.NotNull(await store.GetSessionAsync(keptId, default));
        Assert.True(store.Fts5Available);

        // Full-text search must not find the purged session's text any more, and the index is trimmed back to the
        // messages that exist: the purged rows are gone, the kept session's stay.
        Assert.Empty(await store.SearchAsync(purgedId, "quokka", 10, default));
        Assert.Equal([1L], (await store.SearchAsync(keptId, "quokka", 10, default)).Select(static hit => hit.Seq));
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{purgedId}'"));
        Assert.Equal(1L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{keptId}'"));
    }

    [Fact]
    public async Task An_fts5_build_reopening_a_database_a_degraded_build_only_opened_keeps_every_index_row()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            await ftsBuild.AppendAsync(session.Id, Text("first message"), null, default);
            await ftsBuild.AppendAsync(session.Id, Text("second message"), null, default);
        }

        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(2, await degraded.LastSeqAsync(sessionId, default));
            Assert.False(degraded.Fts5Available);
        }

        await SetFtsModuleAsync(directory, Fts5Module);
        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Equal(2, await store.LastSeqAsync(sessionId, default));
        Assert.True(store.Fts5Available);

        // A fallback table on its own is not residue: a session whose messages are all still stored keeps its index rows.
        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));
        Assert.Equal([1L, 2L], (await store.SearchAsync(sessionId, "message", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task A_message_a_degraded_build_appended_is_searchable_again_when_an_fts5_build_reopens_the_database()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            Assert.Equal(1, await ftsBuild.AppendAsync(session.Id, Text("first message"), null, default));
        }

        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(2, await degraded.AppendAsync(sessionId, Text("degraded second message"), null, default));
            Assert.False(degraded.Fts5Available);
            Assert.Equal([2L], (await degraded.SearchAsync(sessionId, "degraded", 10, default)).Select(static hit => hit.Seq));
        }

        await SetFtsModuleAsync(directory, Fts5Module);
        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Equal(2, await store.LastSeqAsync(sessionId, default));
        Assert.True(store.Fts5Available);

        // Opening fills the index from messages, so full-text search finds what only the fallback table had.
        Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));
        Assert.Equal([2L], (await store.SearchAsync(sessionId, "degraded", 10, default)).Select(static hit => hit.Seq));
        Assert.Equal([1L, 2L], (await store.SearchAsync(sessionId, "message", 10, default)).Select(static hit => hit.Seq));
    }

    [Fact]
    public async Task The_index_top_up_inserts_each_missing_message_once_and_leaves_the_index_whole_on_a_later_open()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            await ftsBuild.AppendAsync(session.Id, Text("first message"), null, default);
        }

        await SetFtsModuleAsync(directory, AbsentModule);
        await using (var degraded = new SqliteSessionStore(directory.Path))
        {
            await degraded.AppendAsync(sessionId, Text("degraded second message"), null, default);
            Assert.False(degraded.Fts5Available);
        }

        await SetFtsModuleAsync(directory, Fts5Module);
        await using (var firstOpen = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(2, await firstOpen.LastSeqAsync(sessionId, default));
            Assert.True(firstOpen.Fts5Available);
            // One index row per message: the already indexed first message is not inserted a second time.
            Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));
            Assert.Equal([1L, 2L], (await firstOpen.SearchAsync(sessionId, "message", 10, default)).Select(static hit => hit.Seq));
        }

        // Nothing is missing on the next open, so the complete index is left exactly as it was.
        await using (var secondOpen = new SqliteSessionStore(directory.Path))
        {
            Assert.Equal(2, await secondOpen.LastSeqAsync(sessionId, default));
            Assert.Equal(2L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));
            Assert.Equal([1L, 2L], (await secondOpen.SearchAsync(sessionId, "message", 10, default)).Select(static hit => hit.Seq));
        }
    }

    [Fact]
    public async Task An_fts5_build_reopening_a_database_that_never_degraded_does_not_create_the_fallback_table_or_fill_a_gap()
    {
        using var directory = new TempDirectory();
        string sessionId;
        await using (var ftsBuild = new SqliteSessionStore(directory.Path))
        {
            var session = await ftsBuild.CreateSessionAsync("D:/ws", "t", default);
            sessionId = session.Id;
            await ftsBuild.AppendAsync(session.Id, Text("first message"), null, default);
        }

        // No degraded open ever happened, so there is no fallback table - the mark the index top-up goes by.
        Assert.Equal(0L, await CountAsync(directory, "SELECT count(*) FROM sqlite_master WHERE name = 'messages_text'"));
        // Remove the index row by hand: a top-up that scanned every FTS5 database would put it back.
        await ExecuteAsync(
            directory.ConnectionString.ToString(),
            $"DELETE FROM messages_fts WHERE session_id = '{sessionId}' AND seq = 1");
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));

        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Equal(1, await store.LastSeqAsync(sessionId, default));
        Assert.True(store.Fts5Available);

        Assert.Equal(0L, await CountAsync(directory, "SELECT count(*) FROM sqlite_master WHERE name = 'messages_text'"));
        Assert.Equal(0L, await CountAsync(directory, $"SELECT count(*) FROM messages_fts WHERE session_id = '{sessionId}'"));
        Assert.Empty(await store.SearchAsync(sessionId, "message", 10, default));
    }

    /// <summary>
    /// Points the stored DDL of <c>messages_fts</c> at <paramref name="module"/>, changing nothing else about the table,
    /// so restoring <c>fts5</c> gives back byte-for-byte the DDL the store itself wrote.
    /// </summary>
    private static async Task SetFtsModuleAsync(TempDirectory directory, string module)
    {
        var connection = directory.ConnectionString.ToString();
        var ddl = await TextAsync(directory, "SELECT sql FROM sqlite_master WHERE name = 'messages_fts'");
        // The stored DDL carries the tokenizer's own quotes, so they are doubled for the literal that rewrites it.
        var rewritten = WithModule(ddl, module).Replace("'", "''", StringComparison.Ordinal);
        await ExecuteAsync(
            connection,
            "PRAGMA writable_schema=ON;\n" +
            $"UPDATE sqlite_master SET sql = '{rewritten}' WHERE name = 'messages_fts';\n" +
            "PRAGMA writable_schema=OFF;");
        SqliteConnection.ClearAllPools();
    }

    private static string WithModule(string ddl, string module)
    {
        var start = ddl.IndexOf("USING ", StringComparison.OrdinalIgnoreCase) + "USING ".Length;
        var end = ddl.IndexOf('(', start);
        Assert.True(start > 0 && end > start, $"'{ddl}' is not a virtual table statement.");
        return string.Concat(ddl.AsSpan(0, start), module, ddl.AsSpan(end));
    }

    private static async Task<long> CountAsync(TempDirectory directory, string sql)
    {
        var value = await ScalarAsync(directory, sql);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(TempDirectory directory, string sql) =>
        Assert.IsType<string>(await ScalarAsync(directory, sql));

    private static async Task<object?> ScalarAsync(TempDirectory directory, string sql)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
