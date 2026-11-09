using System.Diagnostics;
using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the forge.db v1 → v2 step (five step columns on <c>todos</c> plus the <c>plan_summaries</c> table) and the whole
/// ladder a v0 or v1 file climbs to the current schema: data survives, each step is atomic, concurrent first opens apply
/// it once, and a newer file is refused without a single write.
/// </summary>
public sealed class StorageSchemaV2MigrationTests
{
    /// <summary>The exact pre-versioning DDL (user_version 0): sessions/messages/summaries only.</summary>
    private const string V0SchemaSql = """
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            workspace_path TEXT NOT NULL,
            title TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX ix_sessions_workspace ON sessions (workspace_path, created_at DESC);
        CREATE TABLE messages (
            session_id TEXT NOT NULL,
            seq INTEGER NOT NULL,
            role TEXT NOT NULL,
            content_json TEXT NOT NULL,
            usage_json TEXT,
            at TEXT NOT NULL,
            PRIMARY KEY (session_id, seq)
        ) WITHOUT ROWID;
        CREATE TABLE summaries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            layer INTEGER NOT NULL,
            from_seq INTEGER NOT NULL,
            to_seq INTEGER NOT NULL,
            text TEXT NOT NULL,
            at TEXT NOT NULL
        );
        CREATE INDEX ix_summaries_session ON summaries (session_id, layer, from_seq);
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Fresh_database_has_the_v2_todo_columns_all_nullable_and_the_plan_summary_table()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            await store.AddAsync("sess-a", "T", null, null, default);
        }

        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.Equal(["session_id", "text", "updated_at"], await TodoSchemaRaw.ColumnsAsync(directory, "plan_summaries"));

        foreach (var column in new[] { "commit_subject", "summary", "verification", "commit_id", "commit_error" })
        {
            Assert.Equal(
                0L,
                await TodoSchemaRaw.ScalarAsync<long>(
                    directory,
                    "SELECT \"notnull\" FROM pragma_table_info('todos') WHERE name = $name",
                    ("$name", column)));
            Assert.Equal(
                "TEXT",
                await TodoSchemaRaw.ScalarAsync<string>(
                    directory,
                    "SELECT type FROM pragma_table_info('todos') WHERE name = $name",
                    ("$name", column)));
        }

        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT pk FROM pragma_table_info('plan_summaries') WHERE name = 'session_id'"));
        Assert.Equal(
            2L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT sum(\"notnull\") FROM pragma_table_info('plan_summaries') WHERE name IN ('text', 'updated_at')"));
    }

    [Fact]
    public async Task V1_database_with_todos_upgrades_to_v2_keeping_every_field_and_the_order()
    {
        using var directory = new TempDirectory();
        TodoItem[] sessionABefore;
        TodoItem[] sessionBBefore;
        string sessionId;
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            var alpha = await seed.AddAsync("sess-a", "Alpha", "note a", null, default);
            var beta = await seed.AddAsync("sess-a", "Beta", null, null, default);
            var gamma = await seed.AddAsync("sess-a", "Gamma", "note g", null, default);
            await seed.AddAsync("sess-a", "Delta", null, alpha.Id, default);
            await seed.AddAsync("sess-b", "Other", "other note", null, default);
            await seed.UpdateAsync(alpha.Id, null, TodoStatus.Done, null, default);
            await seed.UpdateAsync(beta.Id, "Beta renamed", TodoStatus.InProgress, "now with a note", default);
            await seed.MoveAsync(gamma.Id, 0, default);
            sessionABefore = [.. await seed.ListAsync("sess-a", default)];
            sessionBBefore = [.. await seed.ListAsync("sess-b", default)];
        }

        await using (var sessions = new SqliteSessionStore(directory.Path))
        {
            sessionId = (await sessions.CreateSessionAsync("D:/ws", "keep me", default)).Id;
        }

        Assert.Equal(["Gamma", "Alpha", "Delta", "Beta renamed"], sessionABefore.Select(item => item.Title).ToArray());
        await TodoSchemaRaw.DowngradeToV1Async(directory);
        var rawRowsBefore = await TodoSchemaRaw.V1TodoRowsAsync(directory);
        Assert.Equal(5, rawRowsBefore.Length);

        await using (var store = new SqliteTodoStore(directory.Path))
        {
            // The very first call migrates; the old rows read back exactly as they were written.
            Assert.Equal(sessionABefore, await store.ListAsync("sess-a", default));
            Assert.Equal(sessionBBefore, await store.ListAsync("sess-b", default));
        }

        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summaries"));
        Assert.Equal(rawRowsBefore, await TodoSchemaRaw.V1TodoRowsAsync(directory));
        Assert.Equal(
            0L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM todos WHERE commit_subject IS NOT NULL OR summary IS NOT NULL OR verification IS NOT NULL OR commit_id IS NOT NULL OR commit_error IS NOT NULL"));

        // Rows written before the upgrade take part in the new features, and the ladder did not touch other tables.
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            var first = sessionABefore[1];
            var updated = await store.UpdateAsync(
                first.Id, null, null, null, new TodoStepFields("feat: alpha", "alpha summary", "alpha verified"), default);
            Assert.NotNull(updated);
            Assert.Equal(first.Title, updated.Title);
            Assert.Equal(first.Status, updated.Status);
            Assert.Equal(first.Order, updated.Order);
            Assert.Equal("feat: alpha", updated.CommitSubject);
            var committed = await store.SetCommitResultAsync(first.Id, "0123456789abcdef", null, default);
            Assert.Equal("0123456789abcdef", committed?.CommitId);
            var appended = await store.AddAsync("sess-a", "Epsilon", null, null, default);
            Assert.Equal(4, appended.Order);
        }

        await using (var sessions = new SqliteSessionStore(directory.Path))
        {
            var kept = await sessions.GetSessionAsync(sessionId, default);
            Assert.NotNull(kept);
            Assert.Equal("keep me", kept.Title);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V0_database_reaches_the_current_schema_in_one_open_through_a_read_or_a_write(bool throughWrite)
    {
        using var directory = new TempDirectory();
        var createdAt = new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        await CreateV0DatabaseAsync(directory, "sess-v0", createdAt);
        Assert.Equal(0L, await TodoSchemaRaw.UserVersionAsync(directory));

        await using (var store = new SqliteTodoStore(directory.Path))
        {
            if (throughWrite)
            {
                var summary = await store.SetPlanSummaryAsync("sess-v0", "plan", default);
                Assert.Equal("plan", summary.Text);
            }
            else
            {
                Assert.Null(await store.GetPlanSummaryAsync("sess-v0", default));
            }
        }

        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.Equal(["session_id", "text", "updated_at"], await TodoSchemaRaw.ColumnsAsync(directory, "plan_summaries"));
        foreach (var table in new[] { "projects", "ui_state", "turn_checkpoints" })
        {
            Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, table), $"table {table} should exist after the ladder");
        }

        // The pre-versioning session row survived with its data and is now an Active v1 session.
        await using var sessions = new SqliteSessionStore(directory.Path);
        var loaded = await sessions.GetSessionAsync("sess-v0", default);
        Assert.NotNull(loaded);
        Assert.Equal("old title", loaded.Title);
        Assert.Equal(SessionStatus.Active, loaded.Status);

        // And the new todo features work on the migrated file straight away.
        await using var todos = new SqliteTodoStore(directory.Path);
        var item = await todos.AddAsync("sess-v0", "First", null, null, default);
        var stepped = await todos.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("s", "m", "v"), default);
        Assert.Equal("s", stepped?.CommitSubject);
        Assert.Equal("m", (await todos.SetCommitResultAsync(item.Id, null, "failed", default))?.Summary);
    }

    [Theory]
    [InlineData("ALTER TABLE todos ADD COLUMN verification TEXT NULL", "ALTER TABLE todos DROP COLUMN verification", "duplicate column", false)]
    [InlineData("ALTER TABLE todos ADD COLUMN verification TEXT NULL", "ALTER TABLE todos DROP COLUMN verification", "duplicate column", true)]
    [InlineData("CREATE INDEX plan_summaries ON ui_state (key)", "DROP INDEX plan_summaries", "plan_summaries", false)]
    [InlineData("CREATE INDEX plan_summaries ON ui_state (key)", "DROP INDEX plan_summaries", "plan_summaries", true)]
    public async Task A_v2_step_that_fails_part_way_rolls_back_every_statement_and_a_retry_succeeds(
        string poisonSql,
        string cleanupSql,
        string expectedMessagePart,
        bool throughWrite)
    {
        using var directory = new TempDirectory();
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            await seed.AddAsync("sess-a", "Keep", "note", null, default);
            await seed.AddAsync("sess-a", "Keep too", null, null, default);
        }

        await TodoSchemaRaw.DowngradeToV1Async(directory);
        await TodoSchemaRaw.ExecuteAsync(directory, poisonSql);
        var schemaBefore = await TodoSchemaRaw.SchemaSnapshotAsync(directory);
        var rowsBefore = await TodoSchemaRaw.V1TodoRowsAsync(directory);

        await using var store = new SqliteTodoStore(directory.Path);
        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => throughWrite
                ? store.AddAsync("sess-a", "never stored", null, null, default)
                : store.ListAsync("sess-a", default));
        Assert.Contains(expectedMessagePart, failure.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing of the step survived: not the columns that were added before the failure, not the version bump.
        Assert.Equal(1L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(schemaBefore, await TodoSchemaRaw.SchemaSnapshotAsync(directory));
        Assert.Equal(rowsBefore, await TodoSchemaRaw.V1TodoRowsAsync(directory));
        Assert.False(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summaries"));

        // Once the cause is gone the same store instance upgrades: a failed migration is not remembered as done.
        await TodoSchemaRaw.ExecuteAsync(directory, cleanupSql);
        var titles = (await store.ListAsync("sess-a", default)).Select(item => item.Title).ToArray();
        Assert.Equal(["Keep", "Keep too"], titles);
        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summaries"));
        Assert.Equal(
            "now",
            (await store.SetPlanSummaryAsync("sess-a", "now", default)).Text);
    }

    [Fact]
    public async Task Several_stores_first_opening_a_v1_file_concurrently_migrate_it_once_and_lose_nothing()
    {
        using var directory = new TempDirectory();
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            await seed.AddAsync("sess-a", "One", null, null, default);
            await seed.AddAsync("sess-a", "Two", "n", null, default);
            await seed.AddAsync("sess-a", "Three", null, null, default);
        }

        await TodoSchemaRaw.DowngradeToV1Async(directory);

        var listing = new SqliteTodoStore(directory.Path);
        var writing = new SqliteTodoStore(directory.Path);
        var planWriting = new SqliteTodoStore(directory.Path);
        var planReading = new SqliteTodoStore(directory.Path);
        var sessions = new SqliteSessionStore(directory.Path);
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new List<Task>();
            IReadOnlyList<TodoItem>? listed = null;
            PlanSummary? planBefore = null;
            tasks.Add(Task.Run(async () =>
            {
                await start.Task;
                listed = await listing.ListAsync("sess-a", default);
            }));
            tasks.Add(Task.Run(async () =>
            {
                await start.Task;
                await writing.AddAsync("sess-a", "Four", null, null, default);
            }));
            tasks.Add(Task.Run(async () =>
            {
                await start.Task;
                await planWriting.SetPlanSummaryAsync("sess-a", "plan", default);
            }));
            tasks.Add(Task.Run(async () =>
            {
                await start.Task;
                planBefore = await planReading.GetPlanSummaryAsync("sess-b", default);
            }));
            tasks.Add(Task.Run(async () =>
            {
                await start.Task;
                await sessions.ListSessionsAsync(null, default);
            }));
            start.SetResult();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.NotNull(listed);
            Assert.True(listed.Count >= 3);
            Assert.Null(planBefore);
            Assert.Equal(["One", "Two", "Three", "Four"], (await listing.ListAsync("sess-a", default)).Select(item => item.Title).ToArray());
            Assert.Equal("plan", (await planReading.GetPlanSummaryAsync("sess-a", default))?.Text);
        }
        finally
        {
            await listing.DisposeAsync();
            await writing.DisposeAsync();
            await planWriting.DisposeAsync();
            await planReading.DisposeAsync();
            await sessions.DisposeAsync();
        }

        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        var columns = await TodoSchemaRaw.ColumnsAsync(directory, "todos");
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, columns);
        Assert.Equal(columns.Length, columns.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Two_processes_first_opening_a_v1_file_concurrently_both_succeed_and_migrate_once()
    {
        using var directory = new TempDirectory();
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            await seed.AddAsync("sess-a", "Survivor", "n", null, default);
        }

        await TodoSchemaRaw.DowngradeToV1Async(directory);
        var rowsBefore = await TodoSchemaRaw.V1TodoRowsAsync(directory);

        var first = StorageSchemaConcurrencyChildProcess.Start(directory.Path);
        var second = StorageSchemaConcurrencyChildProcess.Start(directory.Path);
        var firstResult = await RunToExitAsync(first);
        var secondResult = await RunToExitAsync(second);

        foreach (var result in new[] { firstResult, secondResult })
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(StorageSchemaConcurrencyChildProcess.DoneLine, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("duplicate column", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fatal", result.Output, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(4L, await TodoSchemaRaw.UserVersionAsync(directory));
        var columns = await TodoSchemaRaw.ColumnsAsync(directory, "todos");
        Assert.Equal(TodoSchemaRaw.V2TodoColumns, columns);
        Assert.Equal(rowsBefore, await TodoSchemaRaw.V1TodoRowsAsync(directory));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summaries"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_schema_is_refused_by_every_new_operation_without_writing(bool v1Shaped)
    {
        using var directory = new TempDirectory();
        string id;
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            id = (await seed.AddAsync("sess-a", "Untouched", "note", null, default)).Id;
            if (!v1Shaped)
            {
                await seed.SetPlanSummaryAsync("sess-a", "plan", default);
            }
        }

        if (v1Shaped)
        {
            // A hypothetical newer file without the v2 columns: any "helpful" migration attempt would show up as a diff.
            await TodoSchemaRaw.DowngradeToV1Async(directory);
        }

        await TodoSchemaRaw.SetUserVersionAsync(directory, 5);
        var schemaBefore = await TodoSchemaRaw.SchemaSnapshotAsync(directory);
        var rowsBefore = await TodoSchemaRaw.V1TodoRowsAsync(directory);

        await using var store = new SqliteTodoStore(directory.Path);
        var attempts = new Dictionary<string, Func<Task>>
        {
            ["list"] = () => store.ListAsync("sess-a", default),
            ["add"] = () => store.AddAsync("sess-a", "Nope", null, null, default),
            ["update"] = () => store.UpdateAsync(id, "Changed", TodoStatus.Done, null, new TodoStepFields("s", "m", "v"), default),
            ["commit result"] = () => store.SetCommitResultAsync(id, "abc", null, default),
            ["plan summary get"] = () => store.GetPlanSummaryAsync("sess-a", default),
            ["plan summary set"] = () => store.SetPlanSummaryAsync("sess-a", "changed", default),
        };
        foreach (var (name, attempt) in attempts)
        {
            var refusal = await Assert.ThrowsAsync<InvalidDataException>(attempt);
            Assert.True(
                refusal.Message.Contains("schema 5", StringComparison.Ordinal) && refusal.Message.Contains("supports 4", StringComparison.Ordinal),
                $"{name}: unexpected refusal message '{refusal.Message}'");
        }

        Assert.Equal(5L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.Equal(schemaBefore, await TodoSchemaRaw.SchemaSnapshotAsync(directory));
        Assert.Equal(rowsBefore, await TodoSchemaRaw.V1TodoRowsAsync(directory));
        if (!v1Shaped)
        {
            Assert.Equal(
                "plan",
                await TodoSchemaRaw.ScalarAsync<string>(directory, "SELECT text FROM plan_summaries WHERE session_id = 'sess-a'"));
            Assert.Equal(
                0L,
                await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM todos WHERE commit_id IS NOT NULL OR summary IS NOT NULL"));
        }
    }

    private static async Task CreateV0DatabaseAsync(TempDirectory directory, string sessionId, string createdAt)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = directory.Combine("forge.db"),
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = V0SchemaSql;
            await schema.ExecuteNonQueryAsync();
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO sessions (id, workspace_path, title, created_at) VALUES ($id, 'D:/ws/old', 'old title', $at)";
        insert.Parameters.AddWithValue("$id", sessionId);
        insert.Parameters.AddWithValue("$at", createdAt);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
    }

    private static async Task<(int ExitCode, string Output)> RunToExitAsync(Process process)
    {
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(Patience);
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

                Assert.Fail("A schema child process did not exit within " + Patience + ".");
            }

            return (process.ExitCode, await stdout + await stderr);
        }
    }
}
