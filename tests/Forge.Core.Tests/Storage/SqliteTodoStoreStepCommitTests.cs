using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Smoke checks for the v2 schema: step fields, commit results and the plan summary of SqliteTodoStore.</summary>
public sealed class SqliteTodoStoreStepCommitTests
{
    [Fact]
    public async Task Step_fields_round_trip_null_keeps_and_empty_clears()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", "note", null, default);

        var updated = await store.UpdateAsync(
            item.Id, null, TodoStatus.InProgress, null, new TodoStepFields("feat: x", "did x", "ran tests"), default);
        Assert.NotNull(updated);
        Assert.Equal("feat: x", updated.CommitSubject);
        Assert.Equal("did x", updated.Summary);
        Assert.Equal("ran tests", updated.Verification);
        Assert.Equal("note", updated.Note);

        var kept = await store.UpdateAsync(item.Id, "Renamed", null, null, new TodoStepFields(Summary: "did x again"), default);
        Assert.NotNull(kept);
        Assert.Equal("Renamed", kept.Title);
        Assert.Equal("feat: x", kept.CommitSubject);
        Assert.Equal("did x again", kept.Summary);
        Assert.Equal("ran tests", kept.Verification);

        var cleared = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(CommitSubject: "", Verification: ""), default);
        Assert.NotNull(cleared);
        Assert.Null(cleared.CommitSubject);
        Assert.Equal("did x again", cleared.Summary);
        Assert.Null(cleared.Verification);

        var listed = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(cleared with { UpdatedAt = listed.UpdatedAt }, listed);
        Assert.Equal(cleared.UpdatedAt, listed.UpdatedAt);
    }

    [Fact]
    public async Task Four_argument_update_leaves_step_fields_alone()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields("s", "m", "v"), default);

        var updated = await store.UpdateAsync(item.Id, "T2", TodoStatus.Done, "n", default);

        Assert.NotNull(updated);
        Assert.Equal("s", updated.CommitSubject);
        Assert.Equal("m", updated.Summary);
        Assert.Equal("v", updated.Verification);
        Assert.Equal(TodoStatus.Done, updated.Status);
    }

    [Fact]
    public async Task Commit_result_sets_one_clears_the_other_and_touches_nothing_else()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", "note", null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("s", "m", "v"), default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        var failed = await store.SetCommitResultAsync(item.Id, null, "hook failed", default);
        Assert.NotNull(failed);
        Assert.Null(failed.CommitId);
        Assert.Equal("hook failed", failed.CommitError);
        Assert.Equal(TodoStatus.Done, failed.Status);
        Assert.Equal("m", failed.Summary);
        Assert.Equal("note", failed.Note);

        var committed = await store.SetCommitResultAsync(item.Id, "abc1234def", null, default);
        Assert.NotNull(committed);
        Assert.Equal("abc1234def", committed.CommitId);
        Assert.Null(committed.CommitError);
        Assert.True(committed.UpdatedAt >= failed.UpdatedAt);

        var cleared = await store.SetCommitResultAsync(item.Id, null, null, default);
        Assert.NotNull(cleared);
        Assert.Null(cleared.CommitId);
        Assert.Null(cleared.CommitError);

        Assert.Equal(["sess-a", "sess-a", "sess-a"], changed);
        Assert.Null(await store.SetCommitResultAsync(Guid.NewGuid().ToString("n"), "abc", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetCommitResultAsync(item.Id, "abc", "also an error", default));
    }

    [Fact]
    public async Task Plan_summary_is_upserted_per_session_and_raises_changed()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.Null(await store.GetPlanSummaryAsync("sess-a", default));

        var first = await store.SetPlanSummaryAsync("sess-a", "## Summary\nv1", default);
        var other = await store.SetPlanSummaryAsync("sess-b", "other", default);
        var second = await store.SetPlanSummaryAsync("sess-a", "## Summary\nv2", default);

        var read = await store.GetPlanSummaryAsync("sess-a", default);
        Assert.NotNull(read);
        Assert.Equal("sess-a", read.SessionId);
        Assert.Equal("## Summary\nv2", read.Text);
        Assert.Equal(second.UpdatedAt, read.UpdatedAt);
        Assert.True(second.UpdatedAt >= first.UpdatedAt);
        Assert.Equal("other", (await store.GetPlanSummaryAsync("sess-b", default))?.Text);
        Assert.Equal(other.UpdatedAt, (await store.GetPlanSummaryAsync("sess-b", default))?.UpdatedAt);
        Assert.Equal(["sess-a", "sess-b", "sess-a"], changed);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPlanSummaryAsync("sess-a", " ", default));
    }

    [Fact]
    public async Task V1_database_is_upgraded_in_place_and_keeps_its_todos()
    {
        using var directory = new TempDirectory();
        string id;
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            id = (await store.AddAsync("sess-a", "Old", "n", null, default)).Id;
        }

        await using (var connection = new SqliteConnection(directory.ConnectionString.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE todos DROP COLUMN commit_subject;
                ALTER TABLE todos DROP COLUMN summary;
                ALTER TABLE todos DROP COLUMN verification;
                ALTER TABLE todos DROP COLUMN commit_id;
                ALTER TABLE todos DROP COLUMN commit_error;
                DROP TABLE plan_summaries;
                PRAGMA user_version = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            var old = Assert.Single(await store.ListAsync("sess-a", default));
            Assert.Equal(id, old.Id);
            Assert.Equal("Old", old.Title);
            Assert.Null(old.CommitSubject);
            await store.SetPlanSummaryAsync("sess-a", "text", default);
            Assert.Equal("text", (await store.GetPlanSummaryAsync("sess-a", default))?.Text);
        }

        await using var check = new SqliteConnection(directory.ConnectionString.ToString());
        await check.OpenAsync();
        await using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, (long)(await version.ExecuteScalarAsync() ?? 0L));
    }

    [Fact]
    public async Task Newer_schema_version_is_refused()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            await store.AddAsync("sess-a", "T", null, null, default);
        }

        await using (var connection = new SqliteConnection(directory.ConnectionString.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 4";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        await using var reopened = new SqliteTodoStore(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ListAsync("sess-a", default));
    }
}
