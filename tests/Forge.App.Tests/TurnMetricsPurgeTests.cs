using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The purge of a database written before <c>turn_metrics</c> existed: the table is missing and the delete must skip it
/// instead of failing the session's removal — the exact regression the existence guard in the purge was added for.
/// </summary>
public sealed class TurnMetricsPurgeTests
{
    [Fact]
    public async Task Purging_a_database_without_the_metrics_table_deletes_the_session()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-tests-turnmetrics-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new SqliteSessionStore(directory);
            var session = await store.CreateSessionAsync("/work", "title", CancellationToken.None);
            await store.SoftDeleteAsync([session.Id], CancellationToken.None);

            // The exact shape a database from before the feature has: the schema ran, the table never existed.
            var databasePath = Path.Combine(directory, "forge.db");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None);
                await using var drop = new SqliteCommand("DROP TABLE turn_metrics", connection);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // The delete must not fail on the missing table: the rows cannot exist and the guard skips it.
            var purged = await store.PurgeAsync(session.Id, CancellationToken.None);

            Assert.Contains(session.Id, purged);
            Assert.Null(await store.GetSessionAsync(session.Id, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // A pooled handle can outlive the test; the temp directory is the OS's problem then.
            }
        }
    }
}
