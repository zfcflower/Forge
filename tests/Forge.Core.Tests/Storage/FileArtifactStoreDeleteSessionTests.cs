using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// <see cref="FileArtifactStore.DeleteSessionAsync"/>: one session's rows and folder go, every other session's stay; a second
/// call is a no-op; a database or folder failure still lets the other half run and is reported, and a retry finishes it.
/// </summary>
public class FileArtifactStoreDeleteSessionTests
{
    [Fact]
    public async Task Deletes_only_the_sessions_rows_and_folder()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var gone1 = await store.PutAsync("session1", "a", new byte[] { 1 }, default);
        var gone2 = await store.PutAsync("session1", "b", new byte[] { 2 }, default);
        var kept = await store.PutAsync("session2", "c", new byte[] { 3 }, default);
        // A sibling whose name starts with the deleted id must not be caught by a prefix match.
        var prefixed = await store.PutAsync("session10", "d", new byte[] { 4 }, default);

        Assert.Equal(2, await store.DeleteSessionAsync("session1", default));

        Assert.False(Directory.Exists(directory.Combine("artifacts", "session1")));
        Assert.Null(await store.GetInfoAsync(gone1.Id, default));
        Assert.Null(await store.GetInfoAsync(gone2.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync(gone1.Id, 0, null, default));
        Assert.Equal(new byte[] { 3 }, (await store.ReadAsync(kept.Id, 0, null, default)).ToArray());
        Assert.Equal(new byte[] { 4 }, (await store.ReadAsync(prefixed.Id, 0, null, default)).ToArray());
        Assert.Equal(2, await CountRowsAsync(directory, null));

        // A fresh store, without the first one's cache, sees the same database.
        await using var reopened = FileArtifactStore.ForDataDirectory(directory.Path);
        Assert.Null(await reopened.GetInfoAsync(gone1.Id, default));
        Assert.NotNull(await reopened.GetInfoAsync(kept.Id, default));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        await store.PutAsync("session1", "a", new byte[] { 1 }, default);

        Assert.Equal(1, await store.DeleteSessionAsync("session1", default));
        Assert.Equal(0, await store.DeleteSessionAsync("session1", default));
        Assert.Equal(0, await store.DeleteSessionAsync("never-stored", default));
    }

    [Fact]
    public async Task A_database_without_the_table_deletes_nothing_and_does_not_create_it()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        Assert.Equal(0, await store.DeleteSessionAsync("session1", default));

        await using var connection = Open(directory);
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'artifacts'";
        Assert.Equal(0L, (long)(await probe.ExecuteScalarAsync())!);
        Assert.False(Directory.Exists(directory.Combine("artifacts")));
    }

    [Fact]
    public async Task A_folder_left_without_rows_is_still_removed()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var orphan = directory.Combine("artifacts", "session1");
        Directory.CreateDirectory(Path.Combine(orphan, "nested"));
        await File.WriteAllTextAsync(Path.Combine(orphan, "nested", "x.tmp"), "left over");

        Assert.Equal(0, await store.DeleteSessionAsync("session1", default));

        Assert.False(Directory.Exists(orphan));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../session2")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Rejects_an_id_that_is_not_one_path_segment_and_touches_nothing(string sessionId)
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var kept = await store.PutAsync("session2", "c", new byte[] { 3 }, default);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteSessionAsync(sessionId, default));

        Assert.NotNull(await store.GetInfoAsync(kept.Id, default));
        Assert.True(Directory.Exists(directory.Combine("artifacts", "session2")));
    }

    [Fact]
    public async Task A_cancelled_call_deletes_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await store.PutAsync("session1", "a", new byte[] { 1 }, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.DeleteSessionAsync("session1", new CancellationToken(canceled: true)));

        Assert.NotNull(await store.GetInfoAsync(info.Id, default));
        Assert.True(Directory.Exists(directory.Combine("artifacts", "session1")));
    }

    [Fact]
    public async Task A_database_failure_still_removes_the_folder_reports_it_and_a_retry_finishes()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await store.PutAsync("session1", "a", new byte[] { 1 }, default);
        await ExecuteAsync(directory, "CREATE TRIGGER block_delete BEFORE DELETE ON artifacts BEGIN SELECT RAISE(ABORT, 'delete blocked'); END;");

        var failure = await Assert.ThrowsAsync<IOException>(() => store.DeleteSessionAsync("session1", default));

        Assert.Contains("delete blocked", failure.Message, StringComparison.Ordinal);
        Assert.IsType<SqliteException>(failure.InnerException);
        Assert.False(Directory.Exists(directory.Combine("artifacts", "session1")));
        Assert.Equal(1, await CountRowsAsync(directory, "session1"));

        await ExecuteAsync(directory, "DROP TRIGGER block_delete;");
        Assert.Equal(1, await store.DeleteSessionAsync("session1", default));
        Assert.Null(await store.GetInfoAsync(info.Id, default));
    }

    [Fact]
    public async Task A_folder_failure_still_removes_the_rows_reports_it_and_a_retry_finishes()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var info = await store.PutAsync("session1", "a", new byte[] { 1 }, default);
        var folder = directory.Combine("artifacts", "session1");
        Directory.Delete(folder, recursive: true);
        await File.WriteAllTextAsync(folder, "a file where the folder should be");

        var failure = await Assert.ThrowsAsync<IOException>(() => store.DeleteSessionAsync("session1", default));

        Assert.Contains(folder, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountRowsAsync(directory, "session1"));
        Assert.Null(await store.GetInfoAsync(info.Id, default));
        Assert.True(File.Exists(folder), "a file the store does not own is reported, never deleted");

        File.Delete(folder);
        Assert.Equal(0, await store.DeleteSessionAsync("session1", default));
    }

    [Fact]
    public async Task Both_failures_are_reported_together()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        await store.PutAsync("session1", "a", new byte[] { 1 }, default);
        var folder = directory.Combine("artifacts", "session1");
        Directory.Delete(folder, recursive: true);
        await File.WriteAllTextAsync(folder, "x");
        await ExecuteAsync(directory, "CREATE TRIGGER block_delete BEFORE DELETE ON artifacts BEGIN SELECT RAISE(ABORT, 'delete blocked'); END;");

        var failure = await Assert.ThrowsAsync<IOException>(() => store.DeleteSessionAsync("session1", default));

        Assert.Contains("delete blocked", failure.Message, StringComparison.Ordinal);
        Assert.Contains(folder, failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions.Count);
    }

    [Fact]
    public async Task A_link_in_place_of_the_folder_or_inside_it_is_unlinked_and_its_target_left_alone()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);
        var outside = directory.Combine("outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.txt");
        await File.WriteAllTextAsync(precious, "not the session's");
        var other = await store.PutAsync("session2", "c", new byte[] { 3 }, default);

        // The session folder itself is a link to content it does not own.
        var linkedFolder = directory.Combine("artifacts", "session1");
        LinkDirectory(linkedFolder, outside);
        Assert.Equal(0, await store.DeleteSessionAsync("session1", default));
        Assert.False(Directory.Exists(linkedFolder));
        Assert.True(File.Exists(precious));

        // A real session folder holding a link: the link goes, the content behind it stays.
        await store.PutAsync("session3", "a", new byte[] { 1 }, default);
        LinkDirectory(directory.Combine("artifacts", "session3", "nested-link"), outside);
        Assert.Equal(1, await store.DeleteSessionAsync("session3", default));
        Assert.False(Directory.Exists(directory.Combine("artifacts", "session3")));
        Assert.True(File.Exists(precious));
        Assert.Equal(new byte[] { 3 }, (await store.ReadAsync(other.Id, 0, null, default)).ToArray());
    }

    /// <summary>A directory symbolic link, or a junction on Windows without the symlink privilege.</summary>
    private static void LinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.True(OperatingSystem.IsWindows(), "symbolic link failed: " + exception.Message);
            using var junction = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            junction.WaitForExit();
            Assert.True(junction.ExitCode == 0, "mklink /J failed: " + junction.StandardError.ReadToEnd());
        }

        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    private static SqliteConnection Open(TempDirectory directory)
    {
        var connection = new SqliteConnection(directory.ConnectionString.ToString());
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(TempDirectory directory, string sql)
    {
        await using var connection = Open(directory);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(TempDirectory directory, string? sessionId)
    {
        await using var connection = Open(directory);
        await using var command = connection.CreateCommand();
        command.CommandText = sessionId is null
            ? "SELECT COUNT(*) FROM artifacts"
            : "SELECT COUNT(*) FROM artifacts WHERE session_id = $s";
        if (sessionId is not null)
        {
            command.Parameters.AddWithValue("$s", sessionId);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
