using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the v4 groups contract of <see cref="SqliteTodoStore"/>: layer-local dense ordering, group lookup and
/// promotion, origin/paused round-trips, the single in-progress invariant and the v3→v4 ladder. The pre-group
/// dense/loose behaviour itself lives in <see cref="SqliteTodoStoreTests"/>.
/// </summary>
public sealed class SqliteTodoStoreGroupTests
{
    private static async Task<IReadOnlyList<TodoItem>> LayerAsync(SqliteTodoStore store, string session, string? group)
    {
        var all = await store.ListAsync(session, default);
        return [.. all.Where(item => string.Equals(item.GroupId, group, StringComparison.Ordinal)).OrderBy(item => item.Order)];
    }

    [Fact]
    public async Task Add_to_group_records_group_id_and_appends_at_the_end_of_that_layer()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var group = await store.FindOrCreateGroupAsync("sess-a", "Plan", default);

        var first = await store.AddAsync(new TodoAddRequest("sess-a", "Step one", GroupId: group.Id), default);
        var second = await store.AddAsync(new TodoAddRequest("sess-a", "Step two", Note: "n", GroupId: group.Id), default);

        Assert.Equal(group.Id, first.GroupId);
        Assert.Equal(group.Id, second.GroupId);
        Assert.Equal(TodoOrigin.Agent, first.Origin);
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Order).ToArray());
        Assert.Equal(new[] {"Step one", "Step two"}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Title).ToArray());
        Assert.Empty(await LayerAsync(store, "sess-a", null));
    }

    [Fact]
    public async Task Add_without_group_lands_in_the_loose_queue_with_agent_origin_by_default()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var item = await store.AddAsync(new TodoAddRequest("sess-a", "Queued"), default);

        Assert.Null(item.GroupId);
        Assert.Equal(TodoOrigin.Agent, item.Origin);
        Assert.Equal(item, Assert.Single(await LayerAsync(store, "sess-a", null)));
    }

    [Fact]
    public async Task User_origin_round_trips_through_the_loose_queue_and_a_group_step()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var group = await store.FindOrCreateGroupAsync("sess-a", "Plan", default);

        var loose = await store.AddAsync(new TodoAddRequest("sess-a", "Manual", Origin: TodoOrigin.User), default);
        var stepped = await store.AddAsync(new TodoAddRequest("sess-a", "Manual step", GroupId: group.Id, Origin: TodoOrigin.User), default);

        Assert.Equal(TodoOrigin.User, Assert.Single(await LayerAsync(store, "sess-a", null)).Origin);
        Assert.Equal(TodoOrigin.User, Assert.Single(await LayerAsync(store, "sess-a", group.Id), item => item.Id == stepped.Id).Origin);
        Assert.Equal(TodoOrigin.User, (await store.ListAsync("sess-a", default)).First(item => item.Id == loose.Id).Origin);

        await using var reopened = new SqliteTodoStore(directory.Path);
        Assert.Equal(TodoOrigin.User, (await reopened.ListAsync("sess-a", default)).First(item => item.Id == loose.Id).Origin);
        Assert.Equal(TodoOrigin.User, (await reopened.ListAsync("sess-a", default)).First(item => item.Id == stepped.Id).Origin);
    }

    [Fact]
    public async Task Add_with_a_group_of_another_session_is_rejected_and_writes_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var foreign = await store.FindOrCreateGroupAsync("sess-b", "Other", default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => store.AddAsync(new TodoAddRequest("sess-a", "Nope", GroupId: foreign.Id), default));

        Assert.Contains(foreign.Id, failure.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync("sess-a", default));
        Assert.Empty(await store.ListGroupsAsync("sess-a", default));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Add_with_a_cross_layer_after_id_is_rejected_but_same_layer_inserts_behind()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var group = await store.FindOrCreateGroupAsync("sess-a", "Plan", default);
        var looseFirst = await store.AddAsync(new TodoAddRequest("sess-a", "Loose one"), default);
        var stepFirst = await store.AddAsync(new TodoAddRequest("sess-a", "Step one", GroupId: group.Id), default);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AddAsync(new TodoAddRequest("sess-a", "Cross", AfterId: stepFirst.Id), default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AddAsync(new TodoAddRequest("sess-a", "Cross", GroupId: group.Id, AfterId: looseFirst.Id), default));
        Assert.Equal(new[] {"Loose one"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Step one"}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Title).ToArray());

        var behind = await store.AddAsync(new TodoAddRequest("sess-a", "Step two", GroupId: group.Id, AfterId: stepFirst.Id), default);

        Assert.Equal(1, behind.Order);
        Assert.Equal(
            ["Step one", "Step two"],
            (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Loose one"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task Inserts_removes_and_moves_renumber_only_their_own_layer()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var red = await store.FindOrCreateGroupAsync("sess-a", "Red", default);
        var blue = await store.FindOrCreateGroupAsync("sess-a", "Blue", default);
        var looseA = await store.AddAsync(new TodoAddRequest("sess-a", "Loose A"), default);
        var looseB = await store.AddAsync(new TodoAddRequest("sess-a", "Loose B"), default);
        var redA = await store.AddAsync(new TodoAddRequest("sess-a", "Red A", GroupId: red.Id), default);
        var redB = await store.AddAsync(new TodoAddRequest("sess-a", "Red B", GroupId: red.Id), default);
        var blueA = await store.AddAsync(new TodoAddRequest("sess-a", "Blue A", GroupId: blue.Id), default);

        Assert.True(await store.MoveAsync(redB.Id, 0, default));
        Assert.Equal(new[] {"Red B", "Red A"}, (await LayerAsync(store, "sess-a", red.Id)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Loose A", "Loose B"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Blue A"}, (await LayerAsync(store, "sess-a", blue.Id)).Select(item => item.Title).ToArray());

        Assert.True(await store.RemoveAsync(looseA.Id, default));
        Assert.Equal(new[] {0}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Order).ToArray());
        Assert.Equal(new[] {"Loose B"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", red.Id)).Select(item => item.Order).ToArray());

        var appended = await store.AddAsync(new TodoAddRequest("sess-a", "Loose C"), default);
        Assert.Equal(1, appended.Order);
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Order).ToArray());
        Assert.Equal(new[] {"Loose B", "Loose C"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Blue A"}, (await LayerAsync(store, "sess-a", blue.Id)).Select(item => item.Title).ToArray());
    }

    [Fact]
    public async Task Move_clamps_to_the_layer_and_never_touches_other_layers()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var group = await store.FindOrCreateGroupAsync("sess-a", "Plan", default);
        var looseA = await store.AddAsync(new TodoAddRequest("sess-a", "Loose A"), default);
        var looseB = await store.AddAsync(new TodoAddRequest("sess-a", "Loose B"), default);
        var stepA = await store.AddAsync(new TodoAddRequest("sess-a", "Step A", GroupId: group.Id), default);
        var stepB = await store.AddAsync(new TodoAddRequest("sess-a", "Step B", GroupId: group.Id), default);

        Assert.True(await store.MoveAsync(looseA.Id, 100, default));
        Assert.Equal(new[] {"Loose B", "Loose A"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {"Step A", "Step B"}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Title).ToArray());

        Assert.True(await store.MoveAsync(stepB.Id, 0, default));
        Assert.Equal(new[] {"Step B", "Step A"}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Order).ToArray());
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", group.Id)).Select(item => item.Order).ToArray());
        Assert.False(await store.MoveAsync(Guid.NewGuid().ToString("n"), 0, default));
    }

    [Fact]
    public async Task ListGroups_returns_groups_in_order_and_FindOrCreate_reuses_trimmed_titles_case_sensitively()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var first = await store.FindOrCreateGroupAsync("sess-a", "Alpha", default);
        var second = await store.FindOrCreateGroupAsync("sess-a", "Beta", default);
        Assert.Equal(new[] {0, 1}, new[] { first.Order, second.Order });

        var reused = await store.FindOrCreateGroupAsync("sess-a", "  Alpha  ", default);
        Assert.Equal(first.Id, reused.Id);
        Assert.Equal("Alpha", reused.Title);
        Assert.Equal(2, (await store.ListGroupsAsync("sess-a", default)).Count);

        var cased = await store.FindOrCreateGroupAsync("sess-a", "alpha", default);
        Assert.NotEqual(first.Id, cased.Id);
        Assert.Equal(2, cased.Order);

        var groups = await store.ListGroupsAsync("sess-a", default);
        Assert.Equal(new[] {"Alpha", "Beta", "alpha"}, groups.Select(group => group.Title).ToArray());
        Assert.Equal(new[] {0, 1, 2}, groups.Select(group => group.Order).ToArray());
        Assert.Empty(await store.ListGroupsAsync("sess-b", default));
    }

    [Fact]
    public async Task Promote_inherits_the_item_id_and_title_removes_the_loose_row_and_compacts_the_queue()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var keepFirst = await store.AddAsync(new TodoAddRequest("sess-a", "Keep first"), default);
        var promoted = await store.AddAsync(new TodoAddRequest("sess-a", "Become plan", Note: "queue note"), default);
        var keepLast = await store.AddAsync(new TodoAddRequest("sess-a", "Keep last"), default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        var group = await store.PromoteAsync(promoted.Id, null, default);

        Assert.NotNull(group);
        Assert.Equal(promoted.Id, group.Id);
        Assert.Equal("Become plan", group.Title);
        Assert.Equal("sess-a", group.SessionId);
        Assert.Equal(0, group.Order);
        Assert.Equal(new[] {"sess-a"}, changed);
        Assert.DoesNotContain(await store.ListAsync("sess-a", default), item => item.Id == promoted.Id);
        Assert.Equal(new[] {"Keep first", "Keep last"}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Title).ToArray());
        Assert.Equal(new[] {0, 1}, (await LayerAsync(store, "sess-a", null)).Select(item => item.Order).ToArray());
        Assert.Equal(promoted.Id, Assert.Single(await store.ListGroupsAsync("sess-a", default)).Id);
        Assert.Equal(keepFirst.Id, (await LayerAsync(store, "sess-a", null))[0].Id);
        Assert.Equal(keepLast.Id, (await LayerAsync(store, "sess-a", null))[1].Id);
    }

    [Fact]
    public async Task Promote_with_a_title_override_trims_and_a_second_group_appends_after_the_first()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var first = await store.AddAsync(new TodoAddRequest("sess-a", "Raw title"), default);

        var group = await store.PromoteAsync(first.Id, "  Better plan  ", default);

        Assert.NotNull(group);
        Assert.Equal("Better plan", group.Title);
        var second = await store.AddAsync(new TodoAddRequest("sess-a", "Other"), default);
        var secondGroup = await store.PromoteAsync(second.Id, null, default);
        Assert.NotNull(secondGroup);
        Assert.Equal(1, secondGroup.Order);
        Assert.Equal(new[] {"Better plan", "Other"}, (await store.ListGroupsAsync("sess-a", default)).Select(g => g.Title).ToArray());
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    public async Task Promote_rejects_blank_titles_without_writing(string title)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(new TodoAddRequest("sess-a", "Queued"), default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        await Assert.ThrowsAsync<ArgumentException>(() => store.PromoteAsync(item.Id, title, default));

        Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Empty(await store.ListGroupsAsync("sess-a", default));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Promote_of_a_group_step_or_a_missing_id_returns_null_and_writes_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var group = await store.FindOrCreateGroupAsync("sess-a", "Plan", default);
        var step = await store.AddAsync(new TodoAddRequest("sess-a", "Step", GroupId: group.Id), default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        Assert.Null(await store.PromoteAsync(step.Id, null, default));
        Assert.Null(await store.PromoteAsync(Guid.NewGuid().ToString("n"), null, default));
        Assert.Single(await store.ListGroupsAsync("sess-a", default));
        Assert.Equal(step, Assert.Single(await store.ListAsync("sess-a", default)));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Paused_status_round_trips_and_survives_a_reopen()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(new TodoAddRequest("sess-a", "Waiting"), default);

        var paused = await store.UpdateAsync(item.Id, null, TodoStatus.Paused, null, default);

        Assert.NotNull(paused);
        Assert.Equal(TodoStatus.Paused, paused.Status);
        Assert.Equal(TodoStatus.Paused, Assert.Single(await store.ListAsync("sess-a", default)).Status);

        await using var reopened = new SqliteTodoStore(directory.Path);
        Assert.Equal(TodoStatus.Paused, Assert.Single(await reopened.ListAsync("sess-a", default)).Status);
    }

    [Fact]
    public async Task A_row_with_an_unknown_origin_is_reported_as_malformed_with_its_row_id()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(new TodoAddRequest("sess-a", "Step"), default);
        await StorageTestHelpers.ExecuteAsync(
            store.ConnectionString,
            $"UPDATE todos SET origin = 'robot' WHERE id = '{item.Id}'");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync("sess-a", default));

        Assert.Contains("origin", failure.Message, StringComparison.Ordinal);
        Assert.Contains(item.Id, failure.Message, StringComparison.Ordinal);
        Assert.Contains("robot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_with_an_unknown_status_is_reported_as_malformed_with_its_row_id()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(new TodoAddRequest("sess-a", "Step"), default);
        await StorageTestHelpers.ExecuteAsync(
            store.ConnectionString,
            $"UPDATE todos SET status = 'Bogus' WHERE id = '{item.Id}'");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync("sess-a", default));

        Assert.Contains("status", failure.Message, StringComparison.Ordinal);
        Assert.Contains(item.Id, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_step_is_in_progress_across_groups_and_the_queue_with_a_single_notification()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var red = await store.FindOrCreateGroupAsync("sess-a", "Red", default);
        var blue = await store.FindOrCreateGroupAsync("sess-a", "Blue", default);
        var loose = await store.AddAsync(new TodoAddRequest("sess-a", "Loose"), default);
        var redStep = await store.AddAsync(new TodoAddRequest("sess-a", "Red step", GroupId: red.Id), default);
        var blueStep = await store.AddAsync(new TodoAddRequest("sess-a", "Blue step", GroupId: blue.Id), default);
        await StorageTestHelpers.ExecuteAsync(
            store.ConnectionString,
            $"UPDATE todos SET updated_at = '2000-01-01T00:00:00.0000000+00:00' WHERE id = '{loose.Id}'");
        var before = DateTimeOffset.UtcNow;
        var changed = new List<string>();
        store.Changed += changed.Add;

        var running = await store.UpdateAsync(redStep.Id, null, TodoStatus.InProgress, null, default);
        Assert.NotNull(running);
        Assert.Equal(TodoStatus.InProgress, running.Status);
        Assert.Equal(new[] {"sess-a"}, changed);

        changed.Clear();
        var moved = await store.UpdateAsync(blueStep.Id, null, TodoStatus.InProgress, null, default);

        Assert.NotNull(moved);
        Assert.Equal(TodoStatus.InProgress, moved.Status);
        Assert.Equal(new[] {"sess-a"}, changed);
        var byId = (await store.ListAsync("sess-a", default)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        Assert.Equal(TodoStatus.Pending, byId[redStep.Id].Status);
        Assert.Equal(TodoStatus.Pending, byId[loose.Id].Status);
        Assert.True(byId[redStep.Id].UpdatedAt >= moved.UpdatedAt);
        Assert.True(moved.UpdatedAt >= before);

        changed.Clear();
        var fromQueue = await store.UpdateAsync(loose.Id, null, TodoStatus.InProgress, null, default);
        Assert.NotNull(fromQueue);
        Assert.Equal(new[] {"sess-a"}, changed);
        byId = (await store.ListAsync("sess-a", default)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        Assert.Equal(TodoStatus.InProgress, byId[loose.Id].Status);
        Assert.Equal(TodoStatus.Pending, byId[blueStep.Id].Status);
        Assert.Equal(TodoStatus.Pending, byId[redStep.Id].Status);
        Assert.Single(byId.Values, item => item.Status == TodoStatus.InProgress);
    }

    [Fact]
    public async Task A_v3_file_without_groups_migrates_to_v4_with_loose_rows_defaulted_to_agent()
    {
        using var directory = new TempDirectory();
        string keptId;
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            keptId = (await seed.AddAsync(new TodoAddRequest("sess-a", "Kept"), default)).Id;
        }

        SqliteConnection.ClearAllPools();
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            """
            DROP INDEX IF EXISTS ix_todos_group;
            ALTER TABLE todos DROP COLUMN group_id;
            ALTER TABLE todos DROP COLUMN origin;
            DROP TABLE IF EXISTS todo_groups;
            DROP TRIGGER IF EXISTS trg_todo_groups_updated_at_insert;
            DROP TRIGGER IF EXISTS trg_todo_groups_updated_at_update;
            PRAGMA user_version = 3;
            """);
        SqliteConnection.ClearAllPools();
        Assert.Equal(3L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.False(await TodoSchemaRaw.TableExistsAsync(directory, "todo_groups"));

        await using (var store = new SqliteTodoStore(directory.Path))
        {
            var kept = Assert.Single(await store.ListAsync("sess-a", default));
            Assert.Equal(keptId, kept.Id);
            Assert.Null(kept.GroupId);
            Assert.Equal(TodoOrigin.Agent, kept.Origin);
        }

        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "todo_groups"));
        Assert.Equal(
            ["id", "session_id", "title", "order_index", "updated_at"],
            await TodoSchemaRaw.ColumnsAsync(directory, "todo_groups"));
        Assert.Equal(
            ["id", "session_id", "title", "status", "note", "order_index", "updated_at", "commit_subject", "summary", "verification", "commit_id", "commit_error", "group_id", "origin"],
            await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.Equal(
            "agent",
            await TodoSchemaRaw.ScalarAsync<string>(directory, $"SELECT origin FROM todos WHERE id = '{keptId}'"));
        Assert.Equal(
            0L,
            await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM todos WHERE group_id IS NOT NULL"));

        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var triggers = connection.CreateCommand();
        triggers.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        var names = new List<string>();
        await using var reader = await triggers.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Contains("trg_todo_groups_updated_at_insert", names);
        Assert.Contains("trg_todo_groups_updated_at_update", names);
    }

    [Fact]
    public async Task A_fresh_file_is_already_at_version_4_with_groups_and_guards()
    {
        using var directory = new TempDirectory();
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            await store.AddAsync(new TodoAddRequest("sess-a", "T"), default);
        }

        Assert.Equal(9L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "todo_groups"));
        Assert.Contains("group_id", await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
        Assert.Contains("origin", await TodoSchemaRaw.ColumnsAsync(directory, "todos"));
    }
}
