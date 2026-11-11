using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the cross-process first-open of <c>forge.db</c>: two OS processes opening the same
/// fresh database concurrently must both succeed, <c>PRAGMA user_version</c> must read the current version (10),
/// and the schema must be created exactly once (no <c>duplicate column</c> failures from a
/// migration applied twice). Uses a child-process seam (same pattern as
/// <c>ApprovalChildProcess</c>) so the test-process environment is never mutated and the
/// test stays parallel-safe.
/// </summary>
public sealed class StorageSchemaConcurrencyPinTests
{
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToExitAsync(
        System.Diagnostics.Process process, TimeSpan timeout)
    {
        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                Assert.Fail("A schema child process did not exit within " + timeout + ".");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
    }

    private static async Task<int> ReadUserVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadSessionColumnsAsync(string databasePath)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(sessions)";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    [Fact]
    public async Task Two_processes_opening_the_same_fresh_database_concurrently_both_succeed()
    {
        using var directory = new TempDirectory();
        var databasePath = Path.Combine(directory.Path, "forge.db");
        Assert.False(File.Exists(databasePath));

        var first = StorageSchemaConcurrencyChildProcess.Start(directory.Path);
        var second = StorageSchemaConcurrencyChildProcess.Start(directory.Path);
        var timeout = TimeSpan.FromSeconds(120);

        var firstResult = await RunToExitAsync(first, timeout);
        var secondResult = await RunToExitAsync(second, timeout);

        Assert.Equal(0, firstResult.ExitCode);
        Assert.Equal(0, secondResult.ExitCode);
        Assert.Contains(
            StorageSchemaConcurrencyChildProcess.DoneLine, firstResult.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            StorageSchemaConcurrencyChildProcess.DoneLine, secondResult.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate column", firstResult.Stdout + firstResult.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("duplicate column", secondResult.Stdout + secondResult.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fatal", firstResult.Stdout + firstResult.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fatal", secondResult.Stdout + secondResult.Stderr, StringComparison.OrdinalIgnoreCase);

        // Exactly one schema at the current version, with the v1 columns present a single time.
        Assert.Equal(10, await ReadUserVersionAsync(databasePath));
        var columns = await ReadSessionColumnsAsync(databasePath);
        foreach (var expected in new[]
                 {
                     "id", "workspace_path", "title", "created_at", "project_id", "worktree_path",
                     "branch", "parent_session_id", "agent_kind", "task", "status",
                     "last_active_at", "last_active_ticks",
                 })
        {
            Assert.Contains(expected, columns);
        }

        Assert.Equal(columns.Count, columns.Distinct(StringComparer.Ordinal).Count());

        // Both children committed through the same database file.
        await using var store = new SqliteSessionStore(directory.Path);
        Assert.Equal(2, (await store.ListSessionsAsync(null, CancellationToken.None)).Count);
    }
}
