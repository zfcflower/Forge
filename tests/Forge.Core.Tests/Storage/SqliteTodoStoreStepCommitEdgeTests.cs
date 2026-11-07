using System.Collections.Concurrent;
using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using StepTriple = (string? Subject, string? Summary, string? Verification);

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Edge semantics of the v2 additions of <see cref="SqliteTodoStore"/>: the three step fields, the commit result, the
/// plan summary, change notification, cancellation and concurrency. The happy paths live in
/// <see cref="SqliteTodoStoreStepCommitTests"/>.
/// </summary>
public sealed class SqliteTodoStoreStepCommitEdgeTests
{
    private static readonly string OldTimestamp =
        new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset OldInstant = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TodoStepFields Only(string field, string? value) => field switch
    {
        "commit_subject" => new TodoStepFields(CommitSubject: value),
        "summary" => new TodoStepFields(Summary: value),
        "verification" => new TodoStepFields(Verification: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static StepTriple Triple(string? subject, string? summary, string? verification) => (subject, summary, verification);

    private static StepTriple Fields(TodoItem item) => (item.CommitSubject, item.Summary, item.Verification);

    private static StepTriple With(StepTriple fields, string field, string? value) => field switch
        {
            "commit_subject" => (value, fields.Summary, fields.Verification),
            "summary" => (fields.Subject, value, fields.Verification),
            "verification" => (fields.Subject, fields.Summary, value),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };

    private static async Task BackdateAsync(TempDirectory directory, string table, string keyColumn, string key)
    {
        await using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET updated_at = $at WHERE {keyColumn} = $key";
        command.Parameters.AddWithValue("$at", OldTimestamp);
        command.Parameters.AddWithValue("$key", key);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static string? ReadCommitIdSynchronously(TempDirectory directory, string id)
    {
        using var connection = new SqliteConnection(directory.ConnectionString.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT commit_id FROM todos WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string text ? text : null;
    }

    [Theory]
    [InlineData("commit_subject")]
    [InlineData("summary")]
    [InlineData("verification")]
    public async Task Each_step_field_is_set_kept_and_cleared_independently_of_the_other_two(string field)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", "note", null, default);

        // An empty string on a field that was never set leaves it null.
        var untouched = await store.UpdateAsync(item.Id, null, null, null, Only(field, ""), default);
        Assert.NotNull(untouched);
        Assert.Equal(Triple(null, null, null), Fields(untouched));

        var seeded = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields("subject-0", "summary-0", "verification-0"), default);
        Assert.NotNull(seeded);
        Assert.Equal(Triple("subject-0", "summary-0", "verification-0"), Fields(seeded));

        var replaced = await store.UpdateAsync(item.Id, null, null, null, Only(field, "new value"), default);
        Assert.NotNull(replaced);
        Assert.Equal(With(Fields(seeded), field, "new value"), Fields(replaced));

        // null means "leave alone": a step object whose property is null, and no step object at all.
        var keptByNullProperty = await store.UpdateAsync(item.Id, null, null, null, Only(field, null), default);
        var keptByNullStep = await store.UpdateAsync(item.Id, "Renamed", null, null, null, default);
        Assert.Equal(Fields(replaced), Fields(keptByNullProperty!));
        Assert.Equal(Fields(replaced), Fields(keptByNullStep!));

        var cleared = await store.UpdateAsync(item.Id, null, null, null, Only(field, ""), default);
        Assert.NotNull(cleared);
        Assert.Equal(With(Fields(replaced), field, null), Fields(cleared));

        // Clearing twice is idempotent, and the stored row agrees with everything returned above.
        var clearedAgain = await store.UpdateAsync(item.Id, null, null, null, Only(field, ""), default);
        Assert.Equal(Fields(cleared), Fields(clearedAgain!));
        var stored = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(Fields(cleared), Fields(stored));
        Assert.Equal("Renamed", stored.Title);
        Assert.Equal("note", stored.Note);
    }

    [Fact]
    public async Task All_three_fields_cleared_together_in_one_update()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields("s", "m", "v"), default);

        var cleared = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields("", "", ""), default);

        Assert.NotNull(cleared);
        Assert.Equal(Triple(null, null, null), Fields(cleared));
        Assert.Equal(Triple(null, null, null), Fields(Assert.Single(await store.ListAsync("sess-a", default))));
    }

    [Fact]
    public async Task Step_fields_round_trip_multiline_unicode_and_sql_metacharacters_exactly()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        var subject = "feat(core): it's \"quoted\"; DROP TABLE todos;--";
        var summary = "  line one\r\n\tline two with 汉字 and 😀 and %_ and $id and '\n\ntrailing  ";
        var verification = new string('v', 10_000);

        await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(subject, summary, verification), default);

        await using var reopened = new SqliteTodoStore(directory.Path);
        var stored = Assert.Single(await reopened.ListAsync("sess-a", default));
        Assert.Equal(subject, stored.CommitSubject);
        Assert.Equal(summary, stored.Summary);
        Assert.Equal(verification, stored.Verification);
        Assert.Equal("Title", stored.Title);
    }

    [Fact]
    public async Task All_five_new_columns_map_to_their_own_properties_after_a_reopen()
    {
        using var directory = new TempDirectory();
        string first;
        string second;
        await using (var store = new SqliteTodoStore(directory.Path))
        {
            first = (await store.AddAsync("sess-a", "First", null, null, default)).Id;
            second = (await store.AddAsync("sess-a", "Second", null, null, default)).Id;
            await store.UpdateAsync(first, null, TodoStatus.Done, null, new TodoStepFields("subject-1", "summary-1", "verification-1"), default);
            await store.SetCommitResultAsync(first, "sha-1", null, default);
            await store.UpdateAsync(second, null, TodoStatus.Done, null, new TodoStepFields("subject-2", "summary-2", "verification-2"), default);
            await store.SetCommitResultAsync(second, null, "error-2", default);
        }

        await using var reopened = new SqliteTodoStore(directory.Path);
        var items = await reopened.ListAsync("sess-a", default);
        Assert.Equal(2, items.Count);
        Assert.Equal(Triple("subject-1", "summary-1", "verification-1"), Fields(items[0]));
        Assert.Equal("sha-1", items[0].CommitId);
        Assert.Null(items[0].CommitError);
        Assert.Equal(Triple("subject-2", "summary-2", "verification-2"), Fields(items[1]));
        Assert.Null(items[1].CommitId);
        Assert.Equal("error-2", items[1].CommitError);
    }

    [Fact]
    public async Task Four_argument_update_leaves_step_fields_and_commit_result_intact_while_the_step_stays_done()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var committed = await store.AddAsync("sess-a", "Committed", null, null, default);
        var failed = await store.AddAsync("sess-a", "Failed", null, null, default);
        foreach (var id in new[] { committed.Id, failed.Id })
        {
            await store.UpdateAsync(id, null, TodoStatus.Done, null, new TodoStepFields("subject", "summary", "verification"), default);
        }

        await store.SetCommitResultAsync(committed.Id, "abc1234", null, default);
        await store.SetCommitResultAsync(failed.Id, null, "hook failed", default);

        var afterCommitted = await store.UpdateAsync(committed.Id, "Renamed", TodoStatus.Done, "new note", default);
        var afterFailed = await store.UpdateAsync(failed.Id, null, null, "only a note", default);

        Assert.NotNull(afterCommitted);
        Assert.Equal(Triple("subject", "summary", "verification"), Fields(afterCommitted));
        Assert.Equal("abc1234", afterCommitted.CommitId);
        Assert.Null(afterCommitted.CommitError);
        Assert.Equal(TodoStatus.Done, afterCommitted.Status);
        Assert.Equal("Renamed", afterCommitted.Title);
        Assert.NotNull(afterFailed);
        Assert.Equal(Triple("subject", "summary", "verification"), Fields(afterFailed));
        Assert.Null(afterFailed.CommitId);
        Assert.Equal("hook failed", afterFailed.CommitError);
        Assert.Equal(TodoStatus.Done, afterFailed.Status);

        var listed = await store.ListAsync("sess-a", default);
        Assert.Equal([afterCommitted, afterFailed], listed);
    }

    // A step moved off Done has no commit outcome any more: the sha (or the failure) is cleared in the same write, through
    // both overloads, whatever else the call changes, while the agent-written step fields stay.
    [Theory]
    [InlineData(false, false, TodoStatus.Pending)]
    [InlineData(false, false, TodoStatus.InProgress)]
    [InlineData(false, true, TodoStatus.Pending)]
    [InlineData(false, true, TodoStatus.InProgress)]
    [InlineData(true, false, TodoStatus.Pending)]
    [InlineData(true, false, TodoStatus.InProgress)]
    [InlineData(true, true, TodoStatus.Pending)]
    [InlineData(true, true, TodoStatus.InProgress)]
    public async Task Reopening_a_done_step_clears_its_commit_result_in_the_same_write(bool withStep, bool commitFailed, TodoStatus target)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", "note", null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("subject", "summary", "verification"), default);
        await store.SetCommitResultAsync(item.Id, commitFailed ? null : "abc1234", commitFailed ? "hook failed" : null, default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        var reopened = withStep
            ? await store.UpdateAsync(item.Id, "Renamed", target, "new note", new TodoStepFields(Summary: "revised"), default)
            : await store.UpdateAsync(item.Id, "Renamed", target, "new note", default);

        Assert.NotNull(reopened);
        Assert.Equal(target, reopened.Status);
        Assert.Null(reopened.CommitId);
        Assert.Null(reopened.CommitError);
        Assert.Equal(Triple("subject", withStep ? "revised" : "summary", "verification"), Fields(reopened));
        Assert.Equal("Renamed", reopened.Title);
        Assert.Equal("new note", reopened.Note);
        Assert.Equal(["sess-a"], changed);
        Assert.Equal(reopened, Assert.Single(await store.ListAsync("sess-a", default)));
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM todos WHERE id = $id AND commit_id IS NULL AND commit_error IS NULL",
                ("$id", item.Id)));
        await using var reopenedStore = new SqliteTodoStore(directory.Path);
        Assert.Equal(reopened, Assert.Single(await reopenedStore.ListAsync("sess-a", default)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_status_that_stays_done_or_is_not_given_keeps_the_commit_result(bool commitFailed)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, default);
        await store.SetCommitResultAsync(item.Id, commitFailed ? null : "abc1234", commitFailed ? "hook failed" : null, default);

        var sameStatus = await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, default);
        var noStatus = await store.UpdateAsync(item.Id, "Renamed", null, "a note", default);
        var stepOnly = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(Summary: "edited"), default);
        var sameStatusWithStep = await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields(Verification: "checked"), default);

        foreach (var result in new[] { sameStatus, noStatus, stepOnly, sameStatusWithStep })
        {
            Assert.NotNull(result);
            Assert.Equal(TodoStatus.Done, result.Status);
            Assert.Equal(commitFailed ? null : "abc1234", result.CommitId);
            Assert.Equal(commitFailed ? "hook failed" : null, result.CommitError);
        }

        Assert.Equal(sameStatusWithStep, Assert.Single(await store.ListAsync("sess-a", default)));
    }

    [Fact]
    public async Task A_reopened_step_that_is_finished_again_stays_without_a_commit_result_until_one_is_recorded()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, default);
        await store.SetCommitResultAsync(item.Id, "first-sha", null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Pending, null, default);

        var finishedAgain = await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, default);

        Assert.NotNull(finishedAgain);
        Assert.Equal(TodoStatus.Done, finishedAgain.Status);
        Assert.Null(finishedAgain.CommitId);
        Assert.Null(finishedAgain.CommitError);

        var recorded = await store.SetCommitResultAsync(item.Id, "second-sha", null, default);
        Assert.Equal("second-sha", recorded?.CommitId);
        Assert.Equal("second-sha", Assert.Single(await store.ListAsync("sess-a", default)).CommitId);
    }

    [Fact]
    public async Task Reopening_one_step_leaves_the_commit_results_of_the_other_steps_alone()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var first = await store.AddAsync("sess-a", "First", null, null, default);
        var second = await store.AddAsync("sess-a", "Second", null, null, default);
        var elsewhere = await store.AddAsync("sess-b", "Elsewhere", null, null, default);
        foreach (var id in new[] { first.Id, second.Id, elsewhere.Id })
        {
            await store.UpdateAsync(id, null, TodoStatus.Done, null, default);
            await store.SetCommitResultAsync(id, "sha-" + id, null, default);
        }

        await store.UpdateAsync(first.Id, null, TodoStatus.Pending, null, default);

        var items = await store.ListAsync("sess-a", default);
        Assert.Null(items[0].CommitId);
        Assert.Equal("sha-" + second.Id, items[1].CommitId);
        Assert.Equal("sha-" + elsewhere.Id, Assert.Single(await store.ListAsync("sess-b", default)).CommitId);
    }

    [Fact]
    public async Task Step_update_with_an_invalid_title_is_all_or_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", "note", null, default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        foreach (var badTitle in new[] { "", "   ", "\t\n" })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.UpdateAsync(item.Id, badTitle, TodoStatus.Done, "other note", new TodoStepFields("s", "m", "v"), default));
        }

        Assert.Equal(item, Assert.Single(await store.ListAsync("sess-a", default)));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task Step_update_bumps_updated_at_and_raises_changed_once_after_the_commit()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await BackdateAsync(directory, "todos", "id", item.Id);
        var calls = new List<string>();
        var summaryVisibleInHandler = new List<string?>();
        store.Changed += session =>
        {
            calls.Add(session);
            summaryVisibleInHandler.Add(store.ListAsync(session, default).GetAwaiter().GetResult().Single().Summary);
        };
        var before = DateTimeOffset.UtcNow;

        var updated = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(Summary: "durable"), default);

        Assert.NotNull(updated);
        Assert.True(updated.UpdatedAt >= before, "UpdatedAt should be the time of this write");
        Assert.True(updated.UpdatedAt > OldInstant);
        Assert.Equal(["sess-a"], calls);
        Assert.Equal(["durable"], summaryVisibleInHandler);
        Assert.Equal(updated.UpdatedAt, Assert.Single(await store.ListAsync("sess-a", default)).UpdatedAt);
    }

    [Fact]
    public async Task Unknown_item_returns_null_and_raises_nothing_for_step_update_and_commit_result()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var existing = await store.AddAsync("sess-a", "Title", null, null, default);
        var changed = new List<string>();
        store.Changed += changed.Add;
        var unknown = Guid.NewGuid().ToString("n");

        Assert.Null(await store.UpdateAsync(unknown, "T", TodoStatus.Done, "n", new TodoStepFields("s", "m", "v"), default));
        Assert.Null(await store.SetCommitResultAsync(unknown, "abc", null, default));
        Assert.Null(await store.SetCommitResultAsync(unknown, null, "error", default));
        Assert.Null(await store.SetCommitResultAsync(unknown, null, null, default));

        Assert.Empty(changed);
        Assert.Equal(existing, Assert.Single(await store.ListAsync("sess-a", default)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("trailing.")]
    public async Task Item_ids_that_are_not_storage_identifiers_are_rejected_by_the_new_operations(string id)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetCommitResultAsync(id, "abc", null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateAsync(id, null, null, null, new TodoStepFields(Summary: "s"), default));
    }

    [Theory]
    [InlineData(null, "old error", "sha-new", null, "sha-new", null)]
    [InlineData("sha-old", null, null, "error-new", null, "error-new")]
    [InlineData("sha-old", null, null, null, null, null)]
    [InlineData(null, "old error", null, null, null, null)]
    [InlineData("sha-old", null, "", "error-new", null, "error-new")]
    [InlineData(null, "old error", "sha-new", "", "sha-new", null)]
    [InlineData("sha-old", null, "", "", null, null)]
    [InlineData(null, "old error", "", "", null, null)]
    [InlineData(null, null, "sha-new", null, "sha-new", null)]
    [InlineData(null, null, null, "error-new", null, "error-new")]
    [InlineData(null, null, null, null, null, null)]
    [InlineData("sha-old", null, "sha-new", null, "sha-new", null)]
    [InlineData(null, "old error", null, "error-new", null, "error-new")]
    public async Task Commit_result_keeps_the_id_and_the_error_mutually_exclusive(
        string? initialId,
        string? initialError,
        string? newId,
        string? newError,
        string? expectedId,
        string? expectedError)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.SetCommitResultAsync(item.Id, initialId, initialError, default);
        var seeded = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal((initialId, initialError), (seeded.CommitId, seeded.CommitError));

        var result = await store.SetCommitResultAsync(item.Id, newId, newError, default);

        Assert.NotNull(result);
        Assert.Equal((expectedId, expectedError), (result.CommitId, result.CommitError));
        var stored = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal((expectedId, expectedError), (stored.CommitId, stored.CommitError));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("sha-old", null)]
    [InlineData(null, "old error")]
    public async Task Commit_result_with_both_an_id_and_an_error_is_rejected_and_changes_nothing(string? initialId, string? initialError)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.SetCommitResultAsync(item.Id, initialId, initialError, default);
        var before = Assert.Single(await store.ListAsync("sess-a", default));
        var changed = new List<string>();
        store.Changed += changed.Add;

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetCommitResultAsync(item.Id, "sha-new", "error-new", default));

        Assert.Equal(before, Assert.Single(await store.ListAsync("sess-a", default)));
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData(TodoStatus.Pending)]
    [InlineData(TodoStatus.InProgress)]
    [InlineData(TodoStatus.Done)]
    public async Task Commit_result_changes_only_the_result_and_updated_at(TodoStatus status)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var first = await store.AddAsync("sess-a", "First", "first note", null, default);
        var target = await store.AddAsync("sess-a", "Target", "target note", null, default);
        var last = await store.AddAsync("sess-a", "Last", null, null, default);
        var otherSession = await store.AddAsync("sess-b", "Elsewhere", null, null, default);
        await store.UpdateAsync(target.Id, null, status, null, new TodoStepFields("subject", "summary", "verification"), default);
        await BackdateAsync(directory, "todos", "id", target.Id);
        var before = await store.ListAsync("sess-a", default);
        var untouchedBefore = before.Where(item => item.Id != target.Id).ToArray();
        var targetBefore = Assert.Single(before, item => item.Id == target.Id);
        var started = DateTimeOffset.UtcNow;

        var result = await store.SetCommitResultAsync(target.Id, "0123456789abcdef", null, default);

        Assert.NotNull(result);
        Assert.Equal(targetBefore with { CommitId = "0123456789abcdef", UpdatedAt = result.UpdatedAt }, result);
        Assert.True(result.UpdatedAt >= started);
        Assert.True(result.UpdatedAt > targetBefore.UpdatedAt);
        var after = await store.ListAsync("sess-a", default);
        Assert.Equal([0, 1, 2], after.Select(item => item.Order).ToArray());
        Assert.Equal([first.Id, target.Id, last.Id], after.Select(item => item.Id).ToArray());
        Assert.Equal(untouchedBefore, after.Where(item => item.Id != target.Id).ToArray());
        Assert.Equal(otherSession, Assert.Single(await store.ListAsync("sess-b", default)));
        Assert.Equal(result, Assert.Single(after, item => item.Id == target.Id));
    }

    [Fact]
    public async Task Commit_result_raises_changed_exactly_once_per_call_after_the_row_is_durable()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        var other = await store.AddAsync("sess-b", "Other", null, null, default);
        var calls = new List<string>();
        var durableCommitIds = new List<string?>();
        store.Changed += session =>
        {
            calls.Add(session);
            durableCommitIds.Add(ReadCommitIdSynchronously(directory, item.Id));
        };

        await store.SetCommitResultAsync(item.Id, "sha-1", null, default);
        Assert.Equal(["sess-a"], calls);
        Assert.Equal(["sha-1"], durableCommitIds);

        await store.SetCommitResultAsync(item.Id, null, "error", default);
        await store.SetCommitResultAsync(item.Id, null, null, default);
        Assert.Equal(["sess-a", "sess-a", "sess-a"], calls);
        Assert.Equal(["sha-1", null, null], durableCommitIds);

        await store.SetCommitResultAsync(other.Id, "sha-2", null, default);
        Assert.Equal(["sess-a", "sess-a", "sess-a", "sess-b"], calls);
    }

    [Fact]
    public async Task A_throwing_subscriber_neither_fails_nor_rolls_back_the_new_writes()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        var healthy = new List<string>();
        store.Changed += _ => throw new InvalidOperationException("subscriber bug");
        store.Changed += healthy.Add;

        var stepped = await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(Summary: "s"), default);
        var committed = await store.SetCommitResultAsync(item.Id, "sha", null, default);
        var plan = await store.SetPlanSummaryAsync("sess-a", "plan", default);

        Assert.Equal("s", stepped?.Summary);
        Assert.Equal("sha", committed?.CommitId);
        Assert.Equal("plan", plan.Text);
        Assert.Equal(["sess-a", "sess-a", "sess-a"], healthy);
        Assert.Equal("sha", Assert.Single(await store.ListAsync("sess-a", default)).CommitId);
        Assert.Equal("plan", (await store.GetPlanSummaryAsync("sess-a", default))?.Text);
    }

    [Fact]
    public async Task Removing_an_item_discards_its_step_data_and_leaves_the_plan_summary()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("s", "m", "v"), default);
        await store.SetCommitResultAsync(item.Id, "sha", null, default);
        await store.SetPlanSummaryAsync("sess-a", "plan", default);

        Assert.True(await store.RemoveAsync(item.Id, default));

        var changed = new List<string>();
        store.Changed += changed.Add;
        Assert.Null(await store.SetCommitResultAsync(item.Id, "sha-2", null, default));
        Assert.Null(await store.UpdateAsync(item.Id, null, null, null, new TodoStepFields(Summary: "again"), default));
        Assert.Empty(changed);
        Assert.Empty(await store.ListAsync("sess-a", default));
        Assert.Equal("plan", (await store.GetPlanSummaryAsync("sess-a", default))?.Text);
    }

    [Fact]
    public async Task Plan_summary_is_absent_until_written_and_isolated_per_session()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        Assert.Null(await store.GetPlanSummaryAsync("sess-a", default));

        await store.SetPlanSummaryAsync("sess-a", "plan of a", default);

        Assert.Null(await store.GetPlanSummaryAsync("sess-b", default));
        await store.SetPlanSummaryAsync("sess-b", "plan of b", default);
        Assert.Equal("plan of a", (await store.GetPlanSummaryAsync("sess-a", default))?.Text);
        Assert.Equal("plan of b", (await store.GetPlanSummaryAsync("sess-b", default))?.Text);
        Assert.Equal("sess-a", (await store.GetPlanSummaryAsync("sess-a", default))?.SessionId);
        Assert.Equal("sess-b", (await store.GetPlanSummaryAsync("sess-b", default))?.SessionId);
        Assert.Null(await store.GetPlanSummaryAsync("sess-c", default));
    }

    [Fact]
    public async Task Plan_summary_upsert_replaces_in_place_keeps_one_row_and_refreshes_the_timestamp()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        await store.SetPlanSummaryAsync("sess-a", "first", default);
        await BackdateAsync(directory, "plan_summaries", "session_id", "sess-a");
        Assert.Equal(OldInstant, (await store.GetPlanSummaryAsync("sess-a", default))?.UpdatedAt);
        var started = DateTimeOffset.UtcNow;

        var second = await store.SetPlanSummaryAsync("sess-a", "second", default);

        Assert.Equal("second", second.Text);
        Assert.True(second.UpdatedAt >= started);
        var read = await store.GetPlanSummaryAsync("sess-a", default);
        Assert.Equal(second, read);
        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM plan_summaries"));
    }

    [Fact]
    public async Task Plan_summary_text_round_trips_exactly_without_trimming_or_normalising()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var text = "\n  ## Summary\r\nIt's \"done\"; 汉字 😀 %_ $session\n\n## Changes\n- a\n\n## Testing\n- b  \n\t";
        var big = string.Concat(Enumerable.Repeat("0123456789", 20_000));

        await store.SetPlanSummaryAsync("sess-a", text, default);
        await store.SetPlanSummaryAsync("sess-b", big, default);

        await using var reopened = new SqliteTodoStore(directory.Path);
        Assert.Equal(text, (await reopened.GetPlanSummaryAsync("sess-a", default))?.Text);
        Assert.Equal(big, (await reopened.GetPlanSummaryAsync("sess-b", default))?.Text);
    }

    [Fact]
    public async Task Plan_summary_survives_removing_todos_and_is_visible_to_a_second_store_on_the_same_file()
    {
        using var directory = new TempDirectory();
        await using var writer = new SqliteTodoStore(directory.Path);
        await using var reader = new SqliteTodoStore(directory.Path);
        var item = await writer.AddAsync("sess-a", "Title", null, null, default);
        await writer.SetPlanSummaryAsync("sess-a", "plan", default);

        await writer.RemoveAsync(item.Id, default);

        Assert.Equal("plan", (await reader.GetPlanSummaryAsync("sess-a", default))?.Text);
    }

    [Fact]
    public async Task Plan_summary_set_raises_changed_once_per_call_after_the_row_is_durable()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var calls = new List<string>();
        var visible = new List<string?>();
        store.Changed += session =>
        {
            calls.Add(session);
            visible.Add(store.GetPlanSummaryAsync(session, default).GetAwaiter().GetResult()?.Text);
        };

        await store.SetPlanSummaryAsync("sess-a", "v1", default);
        await store.SetPlanSummaryAsync("sess-a", "v2", default);
        await store.SetPlanSummaryAsync("sess-b", "other", default);

        Assert.Equal(["sess-a", "sess-a", "sess-b"], calls);
        Assert.Equal(["v1", "v2", "other"], visible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public async Task Plan_summary_rejects_blank_text_without_replacing_the_stored_one_or_raising(string? text)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var kept = await store.SetPlanSummaryAsync("sess-a", "kept", default);
        var changed = new List<string>();
        store.Changed += changed.Add;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetPlanSummaryAsync("sess-a", text!, default));

        Assert.Equal(kept, await store.GetPlanSummaryAsync("sess-a", default));
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("has space")]
    public async Task Plan_summary_rejects_session_ids_that_are_not_storage_identifiers(string sessionId)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetPlanSummaryAsync(sessionId, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPlanSummaryAsync(sessionId, "text", default));
    }

    [Fact]
    public async Task A_plan_summary_row_with_a_non_canonical_timestamp_is_reported_as_invalid_data()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        await store.GetPlanSummaryAsync("sess-warmup", default);

        // The v3 write guards refuse that spelling, so the row is planted with the guard out of the way: a database
        // written before v3 (or by a tool that dropped the trigger) must still be reported, never read in the local zone.
        await TodoSchemaRaw.ExecuteAsync(directory, "DROP TRIGGER trg_plan_summaries_updated_at_insert");
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            "INSERT INTO plan_summaries (session_id, text, updated_at) VALUES ('sess-bad', 'text', '2024-05-01 12:00:00')");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetPlanSummaryAsync("sess-bad", default));

        Assert.Contains("updated_at", failure.Message, StringComparison.Ordinal);
        Assert.Contains("sess-bad", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_plan_summary_writes_leave_exactly_one_complete_value_and_one_notification_each()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var notifications = new ConcurrentQueue<string>();
        store.Changed += notifications.Enqueue;
        var texts = Enumerable.Range(0, 16).Select(index => $"summary-{index}-" + new string('x', 500)).ToArray();

        await Task.WhenAll(texts.Select(text => Task.Run(() => store.SetPlanSummaryAsync("sess-a", text, default)))).WaitAsync(TimeSpan.FromSeconds(60));

        var final = await store.GetPlanSummaryAsync("sess-a", default);
        Assert.NotNull(final);
        Assert.Contains(final.Text, texts);
        Assert.Equal(1L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM plan_summaries"));
        Assert.Equal(16, notifications.Count);
        Assert.All(notifications, session => Assert.Equal("sess-a", session));
    }

    [Fact]
    public async Task Concurrent_writers_to_different_columns_of_the_same_items_never_lose_each_others_data()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var ids = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            ids.Add((await store.AddAsync("sess-a", $"Item {index}", null, null, default)).Id);
        }

        var notifications = new ConcurrentQueue<string>();
        store.Changed += notifications.Enqueue;
        var tasks = new List<Task>();
        foreach (var id in ids)
        {
            tasks.Add(Task.Run(() => store.UpdateAsync(id, null, null, null, new TodoStepFields(CommitSubject: "subject-" + id), default)));
            tasks.Add(Task.Run(() => store.UpdateAsync(id, null, null, null, new TodoStepFields(Summary: "summary-" + id), default)));
            tasks.Add(Task.Run(() => store.UpdateAsync(id, null, null, null, new TodoStepFields(Verification: "verification-" + id), default)));
            tasks.Add(Task.Run(() => store.UpdateAsync(id, null, TodoStatus.Done, null, default)));
            tasks.Add(Task.Run(() => store.SetCommitResultAsync(id, "sha-" + id, null, default)));
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        var items = await store.ListAsync("sess-a", default);
        Assert.Equal(8, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(TodoStatus.Done, item.Status);
            Assert.Equal(Triple("subject-" + item.Id, "summary-" + item.Id, "verification-" + item.Id), Fields(item));
            Assert.Equal("sha-" + item.Id, item.CommitId);
            Assert.Null(item.CommitError);
        }

        Assert.Equal(Enumerable.Range(0, 8), items.Select(item => item.Order));
        Assert.Equal(40, notifications.Count);
    }

    [Fact]
    public async Task A_cancelled_token_stops_every_new_operation_before_it_changes_anything()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync("sess-a", "Title", null, null, default);
        await store.SetPlanSummaryAsync("sess-a", "kept", default);
        var itemBefore = Assert.Single(await store.ListAsync("sess-a", default));
        var changed = new List<string>();
        store.Changed += changed.Add;
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.UpdateAsync(item.Id, "Other", TodoStatus.Done, "n", new TodoStepFields("s", "m", "v"), cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetCommitResultAsync(item.Id, "sha", null, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetPlanSummaryAsync("sess-a", "replaced", cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPlanSummaryAsync("sess-a", cancelled));

        Assert.Empty(changed);
        Assert.Equal(itemBefore, Assert.Single(await store.ListAsync("sess-a", default)));
        Assert.Equal("kept", (await store.GetPlanSummaryAsync("sess-a", default))?.Text);

        // A cancelled call does not poison the store: the next call with a live token works.
        var later = await store.SetCommitResultAsync(item.Id, "sha", null, default);
        Assert.Equal("sha", later?.CommitId);
    }
}
