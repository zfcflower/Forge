using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// Pins the plan-summary revision history of <see cref="SqliteTodoStore"/> (v11): a replacement records the old text in
/// the same write, writing the same text records nothing, the list is newest first and clamped to
/// 1..<see cref="SqliteTodoStore.PlanRevisionLimit"/>, the history is trimmed per session and isolated between sessions;
/// the table migrates without backfill, is copied on handover and removed on purge, and its <c>replaced_at</c> column is
/// write-guarded like every other timestamp column.
/// </summary>
public sealed class SqlitePlanRevisionTests
{
    private const string SessionA = "sess-a";
    private const string SessionB = "sess-b";

    [Fact]
    public async Task First_write_records_no_revision()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "plan v1", default);

        Assert.Empty(await store.ListPlanRevisionsAsync(SessionA, 10, default));
        Assert.Equal("plan v1", (await store.GetPlanSummaryAsync(SessionA, default))?.Text);
        Assert.Equal(0L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM plan_summary_revisions"));
    }

    [Fact]
    public async Task Replacing_the_summary_records_the_old_text_newest_first()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var before = DateTimeOffset.UtcNow;
        var first = await store.SetPlanSummaryAsync(SessionA, "A", default);
        var second = await store.SetPlanSummaryAsync(SessionA, "B", default);
        var after = DateTimeOffset.UtcNow;

        var replaced = Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default));
        Assert.Equal(SessionA, replaced.SessionId);
        Assert.Equal("A", replaced.Text);
        Assert.InRange(replaced.ReplacedAt, before, after);
        Assert.InRange(replaced.ReplacedAt, first.UpdatedAt, second.UpdatedAt);

        await store.SetPlanSummaryAsync(SessionA, "C", default);
        var revisions = await store.ListPlanRevisionsAsync(SessionA, 10, default);
        Assert.Equal(["B", "A"], revisions.Select(revision => revision.Text).ToArray());
        Assert.True(revisions[0].ReplacedAt >= revisions[1].ReplacedAt);
    }

    [Fact]
    public async Task Writing_the_same_text_records_nothing_but_still_updates_the_summary()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var first = await store.SetPlanSummaryAsync(SessionA, "same", default);
        var second = await store.SetPlanSummaryAsync(SessionA, "same", default);

        // Upsert 照常:时间戳前进,摘要行仍是本次写入。
        Assert.True(second.UpdatedAt >= first.UpdatedAt);
        var read = await store.GetPlanSummaryAsync(SessionA, default);
        Assert.NotNull(read);
        Assert.Equal(second.UpdatedAt, read.UpdatedAt);
        Assert.Empty(await store.ListPlanRevisionsAsync(SessionA, 10, default));

        // 发生过替换之后,再写同一文本也不追加修订。
        await store.SetPlanSummaryAsync(SessionA, "next", default);
        var replaced = Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default));
        Assert.Equal("same", replaced.Text);
        await store.SetPlanSummaryAsync(SessionA, "next", default);
        Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default));
    }

    [Fact]
    public async Task A_case_only_difference_is_a_replacement()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "Plan text", default);
        await store.SetPlanSummaryAsync(SessionA, "plan text", default);

        var replaced = Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default));
        Assert.Equal("Plan text", replaced.Text);
        Assert.Equal("plan text", (await store.GetPlanSummaryAsync(SessionA, default))?.Text);
    }

    [Fact]
    public async Task History_is_trimmed_to_the_newest_PlanRevisionLimit_rows()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        var total = SqliteTodoStore.PlanRevisionLimit + 11;
        for (var index = 0; index < total; index++)
        {
            await store.SetPlanSummaryAsync(SessionA, $"plan-{index}", default);
        }

        var revisions = await store.ListPlanRevisionsAsync(SessionA, 100, default);
        Assert.Equal(SqliteTodoStore.PlanRevisionLimit, revisions.Count);
        // 留下的是最新 50 条:最新被替换的在前,最早保留的恰好是被第 12 次写入替换掉的文本。
        Assert.Equal($"plan-{total - 2}", revisions[0].Text);
        Assert.Equal($"plan-{total - SqliteTodoStore.PlanRevisionLimit - 1}", revisions[^1].Text);
        Assert.DoesNotContain(revisions, revision => revision.Text == $"plan-{total - SqliteTodoStore.PlanRevisionLimit - 2}");
        Assert.Equal(
            SqliteTodoStore.PlanRevisionLimit,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM plan_summary_revisions WHERE session_id = $session",
                ("$session", SessionA)));
    }

    [Fact]
    public async Task Limit_is_clamped_between_one_and_PlanRevisionLimit()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "A", default);
        await store.SetPlanSummaryAsync(SessionA, "B", default);
        await store.SetPlanSummaryAsync(SessionA, "C", default);
        await store.SetPlanSummaryAsync(SessionA, "D", default);

        Assert.Equal("C", Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 0, default)).Text);
        Assert.Equal("C", Assert.Single(await store.ListPlanRevisionsAsync(SessionA, -5, default)).Text);
        Assert.Equal(
            ["C", "B", "A"],
            (await store.ListPlanRevisionsAsync(SessionA, 9999, default)).Select(revision => revision.Text).ToArray());
        Assert.Equal(
            ["C", "B"],
            (await store.ListPlanRevisionsAsync(SessionA, 2, default)).Select(revision => revision.Text).ToArray());
    }

    [Fact]
    public async Task Revisions_are_isolated_per_session()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "A", default);
        await store.SetPlanSummaryAsync(SessionA, "B", default);
        await store.SetPlanSummaryAsync(SessionB, "X", default);
        await store.SetPlanSummaryAsync(SessionB, "Y", default);

        Assert.Equal(["A"], (await store.ListPlanRevisionsAsync(SessionA, 10, default)).Select(revision => revision.Text).ToArray());
        Assert.Equal(["X"], (await store.ListPlanRevisionsAsync(SessionB, 10, default)).Select(revision => revision.Text).ToArray());
        Assert.Empty(await store.ListPlanRevisionsAsync("sess-c", 10, default));
    }

    [Fact]
    public async Task Replacing_the_summary_raises_Changed_once_with_the_session_id()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var changed = new List<string>();
        store.Changed += changed.Add;

        await store.SetPlanSummaryAsync(SessionA, "A", default);
        await store.SetPlanSummaryAsync(SessionA, "B", default);

        Assert.Equal([SessionA, SessionA], changed);
    }

    [Fact]
    public async Task A_foreign_spelling_of_replaced_at_is_refused_by_the_write_guard()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "A", default);
        await store.SetPlanSummaryAsync(SessionA, "B", default);
        var replaced = Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default));

        var failure = await Assert.ThrowsAsync<SqliteException>(() => TodoSchemaRaw.ExecuteAsync(
            directory,
            "UPDATE plan_summary_revisions SET replaced_at = '2026-11-04 07:44:28' WHERE session_id = 'sess-a'"));
        Assert.Contains("plan_summary_revisions.replaced_at", failure.Message, StringComparison.Ordinal);
        Assert.Contains("canonical", failure.Message, StringComparison.OrdinalIgnoreCase);

        // 被拒绝的写入没有改动任何行:值仍是产品代码写出的规范形状。
        Assert.Equal(
            replaced.ReplacedAt.ToString("O", CultureInfo.InvariantCulture),
            await TodoSchemaRaw.ScalarAsync<string>(
                directory,
                "SELECT replaced_at FROM plan_summary_revisions WHERE session_id = 'sess-a'"));
    }

    [Fact]
    public async Task A_v10_database_migrates_to_v11_without_backfilling_history()
    {
        using var directory = new TempDirectory();
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            await seed.SetPlanSummaryAsync(SessionA, "v10 text", default);
            await seed.SetPlanSummaryAsync(SessionA, "v10 replacement", default);
        }

        await DowngradeToV10Async(directory);

        await using var store = new SqliteTodoStore(directory.Path);
        // 老库里的 plan_summaries 文本原样保留,读取本身就完成迁移。
        Assert.Equal("v10 replacement", (await store.GetPlanSummaryAsync(SessionA, default))?.Text);
        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.True(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summary_revisions"));
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_plan_summary_revisions_session'"));
        Assert.Equal(
            2L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' " +
                "AND name IN ('trg_plan_summary_revisions_replaced_at_insert', 'trg_plan_summary_revisions_replaced_at_update')"));

        // 迁移不回填:迁移之前发生过的替换不留历史。
        Assert.Empty(await store.ListPlanRevisionsAsync(SessionA, 10, default));
        Assert.Equal(0L, await TodoSchemaRaw.ScalarAsync<long>(directory, "SELECT count(*) FROM plan_summary_revisions"));

        // 从这一刻起,替换才开始记历史。
        await store.SetPlanSummaryAsync(SessionA, "v11 text", default);
        Assert.Equal(
            "v10 replacement",
            Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default)).Text);
    }

    [Fact]
    public async Task A_v11_step_that_runs_again_over_its_own_table_keeps_the_rows()
    {
        using var directory = new TempDirectory();
        await using (var seed = new SqliteTodoStore(directory.Path))
        {
            await seed.SetPlanSummaryAsync(SessionA, "A", default);
            await seed.SetPlanSummaryAsync(SessionA, "B", default);
        }

        // A second process that read user_version 10 before the first one committed runs the step over the finished
        // table: every statement of the step is IF NOT EXISTS / DROP-then-CREATE, so the history survives.
        SqliteConnection.ClearAllPools();
        await TodoSchemaRaw.ExecuteAsync(directory, "PRAGMA user_version = 10;");
        SqliteConnection.ClearAllPools();

        await using var store = new SqliteTodoStore(directory.Path);
        Assert.Equal("A", Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default)).Text);
        Assert.Equal(11L, await TodoSchemaRaw.UserVersionAsync(directory));
        await store.SetPlanSummaryAsync(SessionA, "C", default);
        Assert.Equal(
            ["B", "A"],
            (await store.ListPlanRevisionsAsync(SessionA, 10, default)).Select(revision => revision.Text).ToArray());
    }

    [Fact]
    public async Task Concurrent_replacements_record_every_replaced_text_once()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        await store.SetPlanSummaryAsync(SessionA, "seed", default);

        const int writers = 20;
        await Task.WhenAll(
            Enumerable.Range(0, writers)
                .Select(index => Task.Run(() => store.SetPlanSummaryAsync(SessionA, "v" + index.ToString(CultureInfo.InvariantCulture), default))));

        // Each write read the text it replaced inside its own write transaction, so the history is a chain: the seed and
        // every text but the final one appear exactly once, and the final text is the current summary.
        var current = (await store.GetPlanSummaryAsync(SessionA, default))!.Text;
        var replaced = (await store.ListPlanRevisionsAsync(SessionA, SqliteTodoStore.PlanRevisionLimit, default))
            .Select(revision => revision.Text)
            .ToArray();
        Assert.Equal(writers, replaced.Length);
        Assert.Equal(replaced.Length, replaced.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(current, replaced);
        Assert.Equal("seed", replaced[^1]);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "seed" };
        expected.UnionWith(Enumerable.Range(0, writers).Select(index => "v" + index.ToString(CultureInfo.InvariantCulture)));
        Assert.True(expected.SetEquals(replaced.Append(current)));
    }

    [Fact]
    public async Task Handover_copies_the_revision_history_and_leaves_the_source_untouched()
    {
        using var directory = new TempDirectory();
        await using var sessions = new SqliteSessionStore(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);
        var worktree = directory.Combine("wt");
        var old = await sessions.CreateAsync(new SessionCreate(worktree, "t"), default);

        await todos.SetPlanSummaryAsync(old.Id, "A", default);
        await todos.SetPlanSummaryAsync(old.Id, "B", default);
        await todos.SetPlanSummaryAsync(old.Id, "C", default);
        var sourceBefore = await todos.ListPlanRevisionsAsync(old.Id, 10, default);
        Assert.Equal(["B", "A"], sourceBefore.Select(revision => revision.Text).ToArray());

        var created = await sessions.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(new SessionCreate(worktree, "copy")) { CopyTodosAndPlans = true },
            default);

        // 条数相同、新→旧顺序相同、ReplacedAt 保留(会话 id 当然换成新会话的)。
        Assert.Equal(
            sourceBefore.Select(revision => (revision.Text, revision.ReplacedAt)).ToArray(),
            (await todos.ListPlanRevisionsAsync(created.Id, 10, default)).Select(revision => (revision.Text, revision.ReplacedAt)).ToArray());
        Assert.All(
            await todos.ListPlanRevisionsAsync(created.Id, 10, default),
            revision => Assert.Equal(created.Id, revision.SessionId));

        // 修改新会话的摘要不影响源会话的历史。
        await todos.SetPlanSummaryAsync(created.Id, "C2", default);
        Assert.Equal(
            ["C", "B", "A"],
            (await todos.ListPlanRevisionsAsync(created.Id, 10, default)).Select(revision => revision.Text).ToArray());
        Assert.Equal(
            sourceBefore.Select(revision => (revision.Text, revision.ReplacedAt)).ToArray(),
            (await todos.ListPlanRevisionsAsync(old.Id, 10, default)).Select(revision => (revision.Text, revision.ReplacedAt)).ToArray());
        Assert.Equal("C", (await todos.GetPlanSummaryAsync(old.Id, default))?.Text);
    }

    [Fact]
    public async Task Purge_removes_the_sessions_revisions_and_leaves_other_sessions_alone()
    {
        using var directory = new TempDirectory();
        await using var sessions = new SqliteSessionStore(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);

        var root = await sessions.CreateAsync(new SessionCreate(directory.Combine("wt"), "t"), default);
        var other = await sessions.CreateAsync(new SessionCreate(directory.Combine("other"), "t"), default);
        await todos.SetPlanSummaryAsync(root.Id, "A", default);
        await todos.SetPlanSummaryAsync(root.Id, "B", default);
        await todos.SetPlanSummaryAsync(other.Id, "X", default);
        await todos.SetPlanSummaryAsync(other.Id, "Y", default);

        Assert.Equal(1, await sessions.SoftDeleteAsync([root.Id], default));
        Assert.Equal([root.Id], await sessions.PurgeAsync(root.Id, default));

        Assert.Empty(await todos.ListPlanRevisionsAsync(root.Id, 10, default));
        Assert.Equal(
            ["X"],
            (await todos.ListPlanRevisionsAsync(other.Id, 10, default)).Select(revision => revision.Text).ToArray());
        Assert.Equal(
            0L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM plan_summary_revisions WHERE session_id = $session",
                ("$session", root.Id)));
        Assert.Equal(
            1L,
            await TodoSchemaRaw.ScalarAsync<long>(
                directory,
                "SELECT count(*) FROM plan_summary_revisions WHERE session_id = $session",
                ("$session", other.Id)));
    }

    [Fact]
    public async Task A_cancelled_token_stops_reading_and_writing_revisions()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);

        await store.SetPlanSummaryAsync(SessionA, "A", default);
        await store.SetPlanSummaryAsync(SessionA, "B", default);
        var changed = new List<string>();
        store.Changed += changed.Add;
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListPlanRevisionsAsync(SessionA, 10, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetPlanSummaryAsync(SessionA, "C", cancelled));

        // 摘要与修订都没有变,也没有事件。
        Assert.Empty(changed);
        Assert.Equal("B", (await store.GetPlanSummaryAsync(SessionA, default))?.Text);
        Assert.Equal(
            "A",
            Assert.Single(await store.ListPlanRevisionsAsync(SessionA, 10, default)).Text);

        // 已取消的调用不毒化后续写入。
        await store.SetPlanSummaryAsync(SessionA, "C", default);
        Assert.Equal(
            ["B", "A"],
            (await store.ListPlanRevisionsAsync(SessionA, 10, default)).Select(revision => revision.Text).ToArray());
    }

    /// <summary>
    /// Turns a current file back into the shape a v10 build left: the v11 revision table, its index and its guards are
    /// removed and <c>user_version</c> is 10, so the next open runs the real v11 step over it. Callers must have disposed
    /// their stores first.
    /// </summary>
    private static async Task DowngradeToV10Async(TempDirectory directory)
    {
        SqliteConnection.ClearAllPools();
        await TodoSchemaRaw.ExecuteAsync(
            directory,
            """
            DROP TRIGGER IF EXISTS trg_plan_summary_revisions_replaced_at_insert;
            DROP TRIGGER IF EXISTS trg_plan_summary_revisions_replaced_at_update;
            DROP INDEX IF EXISTS ix_plan_summary_revisions_session;
            DROP TABLE IF EXISTS plan_summary_revisions;
            PRAGMA user_version = 10;
            """);
        SqliteConnection.ClearAllPools();

        // 降级必须真的产生 v10 形状,否则迁移断言会空转。
        Assert.Equal(10L, await TodoSchemaRaw.UserVersionAsync(directory));
        Assert.False(await TodoSchemaRaw.TableExistsAsync(directory, "plan_summary_revisions"));
    }
}
