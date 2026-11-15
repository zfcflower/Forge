using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class FileArtifactStoreTests
{
    /// <summary>The exact shape a foreign writer leaves behind: <c>datetime.isoformat()</c> spells six fractional digits.</summary>
    private const string ForeignTimestamp = "2026-11-04T07:44:28.766342+00:00";

    private const string CanonicalTimestamp = "2026-11-04T07:44:28.7663420+00:00";

    private const string OffsetlessTimestamp = "2026-11-04 07:44:28";

    [Fact]
    public async Task Put_and_read_round_trip_content_and_metadata()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var content = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();

        var info = await store.PutAsync("session1", "build.log", content, default);

        Assert.Equal("session1", info.SessionId);
        Assert.Equal("build.log", info.Name);
        Assert.Equal(content.Length, info.SizeBytes);
        Assert.InRange(info.Id.Length, 8, 64);
        Assert.Equal(info, await store.GetInfoAsync(info.Id, default));

        var full = await store.ReadAsync(info.Id, 0, null, default);
        Assert.Equal(content, full.ToArray());

        Assert.Null(await store.GetInfoAsync("0123456789ab", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync("0123456789ab", 0, null, default));
    }

    [Fact]
    public async Task Read_returns_requested_ranges_without_touching_the_rest()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var content = Enumerable.Range(0, 1000).Select(value => (byte)(value % 256)).ToArray();
        var info = await store.PutAsync("session1", "blob", content, default);

        Assert.Equal(content.AsSpan(10, 20).ToArray(), (await store.ReadAsync(info.Id, 10, 20, default)).ToArray());
        Assert.Equal(content.AsSpan(900).ToArray(), (await store.ReadAsync(info.Id, 900, null, default)).ToArray());
        Assert.Equal(content.AsSpan(0, 1).ToArray(), (await store.ReadAsync(info.Id, 0, 1, default)).ToArray());
        Assert.Equal(content.AsSpan(995, 5).ToArray(), (await store.ReadAsync(info.Id, 995, 500, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 1000, 10, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 5000, null, default)).ToArray());
        Assert.Empty((await store.ReadAsync(info.Id, 0, 0, default)).ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(info.Id, -1, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(info.Id, 0, -1, default));
    }

    [Fact]
    public async Task Artifacts_are_persisted_per_session_and_reopen_cleanly()
    {
        using var directory = new TempDirectory();
        string id;
        await using (var store = FileArtifactStore.ForDataDirectory(directory.Path))
        {
            id = (await store.PutAsync("session1", "one", new byte[] { 1, 2, 3 }, default)).Id;
            await store.PutAsync("session2", "two", new byte[] { 4, 5, 6 }, default);
        }

        Assert.True(File.Exists(Path.Combine(directory.Path, "artifacts", "session1", id)));

        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await reopened.GetInfoAsync(id, default);
        Assert.NotNull(info);
        Assert.Equal("session1", info.SessionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await reopened.ReadAsync(id, 0, null, default)).ToArray());
    }

    [Fact]
    public async Task Put_assigns_distinct_ids_and_handles_empty_content()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        var first = await store.PutAsync("session1", "a", ReadOnlyMemory<byte>.Empty, default);
        var second = await store.PutAsync("session1", "b", new byte[] { 9 }, default);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, first.SizeBytes);
        Assert.Empty((await store.ReadAsync(first.Id, 0, null, default)).ToArray());
        Assert.Equal(new byte[] { 9 }, (await store.ReadAsync(second.Id, 0, null, default)).ToArray());
    }

    [Fact]
    public async Task Artifact_metadata_shares_the_session_database()
    {
        using var directory = new TempDirectory();
        await using var sessions = new SqliteSessionStore(directory.Path);
        await using var artifacts = new FileArtifactStore(directory.Combine("artifacts"), sessions.ConnectionString);
        var session = await sessions.CreateSessionAsync("D:/ws", "t", default);

        var info = await artifacts.PutAsync(session.Id, "out.txt", "hello"u8.ToArray(), default);

        Assert.Equal("hello"u8.ToArray(), (await artifacts.ReadAsync(info.Id, 0, null, default)).ToArray());
        Assert.Equal(0, await sessions.LastSeqAsync(session.Id, default));
        Assert.NotNull(await sessions.GetSessionAsync(session.Id, default));
    }

    [Fact]
    public async Task The_database_refuses_a_foreign_artifact_timestamp_the_moment_the_store_created_its_table()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await store.PutAsync("session1", "build.log", new byte[] { 1, 2, 3 }, default);

        // No migration step creates this table, so its owner installs the guard the ladder would have: a tool that
        // opens forge.db on its own cannot store an `at` every later read would refuse.
        var insert = await Assert.ThrowsAsync<SqliteException>(() => TodoSchemaRaw.ExecuteAsync(
            directory,
            "INSERT INTO artifacts (id, session_id, name, size_bytes, at) " +
            $"VALUES ('ffffffffffff', 'session1', 'x', 1, '{ForeignTimestamp}')"));
        Assert.Contains("artifacts.at", insert.Message, StringComparison.Ordinal);
        Assert.Contains("canonical", insert.Message, StringComparison.OrdinalIgnoreCase);

        var update = await Assert.ThrowsAsync<SqliteException>(() => TodoSchemaRaw.ExecuteAsync(
            directory,
            $"UPDATE artifacts SET at = '{ForeignTimestamp}' WHERE id = '{info.Id}'"));
        Assert.Contains("artifacts.at", update.Message, StringComparison.Ordinal);

        // A canonical value carrying another offset is not foreign and stays writable.
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            $"UPDATE artifacts SET at = '2026-01-01T00:00:00.0000000+08:00' WHERE id = '{info.Id}'");
        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        Assert.Equal(TimeSpan.FromHours(8), (await reopened.GetInfoAsync(info.Id, default))!.At.Offset);
    }

    [Fact]
    public async Task A_foreign_artifact_timestamp_is_repaired_when_the_store_next_opens()
    {
        using var directory = new TempDirectory();
        string id;
        await using (var store = FileArtifactStore.ForDataDirectory(directory.Path))
        {
            id = (await store.PutAsync("session1", "build.log", new byte[] { 1 }, default)).Id;
        }

        await WithoutArtifactGuardsAsync(directory, $"UPDATE artifacts SET at = '{ForeignTimestamp}' WHERE id = '{id}'");

        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await reopened.GetInfoAsync(id, default);

        // The same instant in the shape this codebase reads: the repair is what lets the read stay strict.
        Assert.NotNull(info);
        Assert.Equal(DateTimeOffset.Parse(ForeignTimestamp, CultureInfo.InvariantCulture), info.At);
        Assert.Equal(
            CanonicalTimestamp,
            await TodoSchemaRaw.ScalarAsync<string>(directory, $"SELECT at FROM artifacts WHERE id = '{id}'"));
    }

    [Fact]
    public async Task An_artifact_timestamp_without_an_offset_is_left_untouched_and_the_read_names_the_row()
    {
        using var directory = new TempDirectory();
        string id;
        await using (var store = FileArtifactStore.ForDataDirectory(directory.Path))
        {
            id = (await store.PutAsync("session1", "build.log", new byte[] { 1 }, default)).Id;
        }

        await WithoutArtifactGuardsAsync(directory, $"UPDATE artifacts SET at = '{OffsetlessTimestamp}' WHERE id = '{id}'");

        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetInfoAsync(id, default));
        Assert.Contains("malformed at value", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"artifact {id}", failure.Message, StringComparison.Ordinal);

        // Repairing it would mean inventing an instant - the value would be read in the local zone - so it stays
        // exactly as the other writer left it and every read reports the row instead.
        Assert.Equal(
            OffsetlessTimestamp,
            await TodoSchemaRaw.ScalarAsync<string>(directory, $"SELECT at FROM artifacts WHERE id = '{id}'"));
    }

    /// <summary>
    /// Stores a value the way a writer outside Forge would: the guards the store installed have to be out of the way
    /// first, or they refuse exactly the spelling these tests are about.
    /// </summary>
    private static async Task WithoutArtifactGuardsAsync(TempDirectory directory, string sql)
    {
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            "DROP TRIGGER IF EXISTS trg_artifacts_at_insert;\n" +
            "DROP TRIGGER IF EXISTS trg_artifacts_at_update;\n" +
            sql);
        SqliteConnection.ClearAllPools();
    }
}
