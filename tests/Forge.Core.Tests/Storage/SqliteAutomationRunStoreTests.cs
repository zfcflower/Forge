using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public sealed class SqliteAutomationRunStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Insert_get_and_update_round_trip_every_field()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        var run = new AutomationRun("r1", "auto-a", "Nightly", AutomationTriggerSource.Chain, "k@2026-03-01T10:00:00Z", AutomationRunStatus.WaitingQuota, AutomationRuns.T0)
        {
            EndedAt = AutomationRuns.T0.AddMinutes(5),
            Detail = "detail",
            SessionId = "s1",
            TargetSessionId = "s2",
            CauseRunId = "r0",
            ChainDepth = 3,
            TargetKey = "ci:repo#7",
            ResumeAt = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8)),
            Usage = new TokenUsage(1, 2, 3, 4),
            Message = "msg",
        };

        Assert.True(await store.TryInsertAsync(run, Ct));
        Assert.Equal(run, await store.GetAsync("r1", Ct));

        var changed = run with { Status = AutomationRunStatus.Failed, Message = "boom", Usage = null, ResumeAt = null, SessionId = null };
        await store.UpdateAsync(changed, Ct);
        Assert.Equal(changed, await store.GetAsync("r1", Ct));
        Assert.Null(await store.GetAsync("missing", Ct));
    }

    [Fact]
    public async Task Optional_fields_read_back_as_null()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        var run = AutomationRuns.Make("r1");
        Assert.True(await store.TryInsertAsync(run, Ct));
        var loaded = await store.GetAsync("r1", Ct);
        Assert.Equal(run, loaded);
        Assert.Null(loaded!.Usage);
        Assert.Null(loaded.EndedAt);
    }

    [Fact]
    public async Task A_second_insert_of_the_same_occurrence_returns_false_without_throwing_or_changing_the_row()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        Assert.True(await store.TryInsertAsync(AutomationRuns.Make("r1", occurrence: "same"), Ct));
        Assert.False(await store.TryInsertAsync(AutomationRuns.Make("r2", occurrence: "same"), Ct));

        Assert.Null(await store.GetAsync("r2", Ct));
        Assert.Equal("r1", (await store.GetAsync("r1", Ct))!.Id);
        Assert.Single(await store.QueryAsync(new AutomationRunQuery(), Ct));
    }

    [Fact]
    public async Task A_duplicate_run_id_with_a_new_occurrence_is_a_real_error()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        Assert.True(await store.TryInsertAsync(AutomationRuns.Make("r1", occurrence: "a"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryInsertAsync(AutomationRuns.Make("r1", occurrence: "b"), Ct));
    }

    [Fact]
    public async Task Updating_an_unknown_run_changes_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.UpdateAsync(AutomationRuns.Make("ghost"), Ct);
        Assert.Empty(await store.QueryAsync(new AutomationRunQuery(), Ct));
    }

    [Fact]
    public async Task Update_keeps_the_occurrence_and_start_time()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        var run = AutomationRuns.Make("r1", status: AutomationRunStatus.Running);
        await store.TryInsertAsync(run, Ct);
        await store.UpdateAsync(run with { Occurrence = "other", StartedAt = AutomationRuns.T0.AddDays(1), Status = AutomationRunStatus.Completed }, Ct);
        var loaded = await store.GetAsync("r1", Ct);
        Assert.Equal(run.Occurrence, loaded!.Occurrence);
        Assert.Equal(run.StartedAt, loaded.StartedAt);
        Assert.Equal(AutomationRunStatus.Completed, loaded.Status);
    }

    [Fact]
    public async Task Query_is_newest_first_across_offsets_and_honours_every_filter_and_the_limit()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        // r-late started 10:30Z but is written with a +08:00 offset: text order would put it first, instant order last.
        await store.TryInsertAsync(AutomationRuns.Make("r-early", minute: 0), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("r-late", minute: 30) with { StartedAt = AutomationRuns.T0.AddMinutes(30).ToOffset(TimeSpan.FromHours(8)) }, Ct);
        await store.TryInsertAsync(AutomationRuns.Make("r-mid", automationId: "auto-b", status: AutomationRunStatus.Failed, minute: 15) with { TargetSessionId = "t1" }, Ct);

        var all = await store.QueryAsync(new AutomationRunQuery(), Ct);
        Assert.Equal(["r-late", "r-mid", "r-early"], all.Select(r => r.Id));

        Assert.Equal(["r-late", "r-early"], (await store.QueryAsync(new AutomationRunQuery { AutomationId = "auto-a" }, Ct)).Select(r => r.Id));
        Assert.Equal(["r-mid"], (await store.QueryAsync(new AutomationRunQuery { Statuses = [AutomationRunStatus.Failed] }, Ct)).Select(r => r.Id));
        Assert.Equal(["r-late", "r-early"], (await store.QueryAsync(new AutomationRunQuery { Statuses = [AutomationRunStatus.Completed, AutomationRunStatus.Running] }, Ct)).Select(r => r.Id));
        Assert.Empty(await store.QueryAsync(new AutomationRunQuery { Statuses = [] }, Ct));
        Assert.Equal(["r-mid"], (await store.QueryAsync(new AutomationRunQuery { TargetSessionId = "t1" }, Ct)).Select(r => r.Id));
        Assert.Equal(["r-late", "r-mid"], (await store.QueryAsync(new AutomationRunQuery { Limit = 2 }, Ct)).Select(r => r.Id));
        Assert.Empty(await store.QueryAsync(new AutomationRunQuery { AutomationId = "auto-a", TargetSessionId = "t1" }, Ct));
    }

    [Fact]
    public async Task MarkInterrupted_changes_Running_and_WaitingIdle_only_and_leaves_WaitingQuota()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.TryInsertAsync(AutomationRuns.Make("run", status: AutomationRunStatus.Running, minute: 1), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("idle", status: AutomationRunStatus.WaitingIdle, minute: 2), Ct);
        var quota = AutomationRuns.Make("quota", status: AutomationRunStatus.WaitingQuota, minute: 3) with { ResumeAt = AutomationRuns.T0.AddHours(1) };
        await store.TryInsertAsync(quota, Ct);
        await store.TryInsertAsync(AutomationRuns.Make("done", status: AutomationRunStatus.Completed, minute: 4), Ct);

        var endedAt = AutomationRuns.T0.AddMinutes(10);
        Assert.Equal(2, await store.MarkInterruptedAsync(endedAt, "restart", Ct));

        foreach (var id in new[] { "run", "idle" })
        {
            var run = (await store.GetAsync(id, Ct))!;
            Assert.Equal(AutomationRunStatus.Interrupted, run.Status);
            Assert.Equal(endedAt, run.EndedAt);
            Assert.Equal("restart", run.Message);
        }

        Assert.Equal(quota, await store.GetAsync("quota", Ct));
        Assert.Equal(AutomationRunStatus.Completed, (await store.GetAsync("done", Ct))!.Status);
        Assert.Equal(0, await store.MarkInterruptedAsync(endedAt, "again", Ct));
    }

    [Fact]
    public async Task Trim_keeps_the_newest_per_automation_then_the_newest_overall()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        for (var i = 0; i < 5; i++)
        {
            await store.TryInsertAsync(AutomationRuns.Make("a" + i, "auto-a", minute: i), Ct);
        }

        for (var i = 0; i < 3; i++)
        {
            await store.TryInsertAsync(AutomationRuns.Make("b" + i, "auto-b", minute: 10 + i), Ct);
        }

        Assert.Equal(2, await store.TrimAsync(perAutomation: 3, total: 100, Ct));
        Assert.Equal(["b2", "b1", "b0", "a4", "a3", "a2"], (await store.QueryAsync(new AutomationRunQuery(), Ct)).Select(r => r.Id));

        Assert.Equal(2, await store.TrimAsync(perAutomation: 100, total: 4, Ct));
        Assert.Equal(["b2", "b1", "b0", "a4"], (await store.QueryAsync(new AutomationRunQuery(), Ct)).Select(r => r.Id));
        Assert.Equal(0, await store.TrimAsync(perAutomation: 100, total: 100, Ct));
    }

    [Fact]
    public async Task Trim_never_deletes_active_runs_even_when_they_are_old()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.TryInsertAsync(AutomationRuns.Make("old-running", status: AutomationRunStatus.Running, minute: 0), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("old-idle", status: AutomationRunStatus.WaitingIdle, minute: 1), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("old-quota", status: AutomationRunStatus.WaitingQuota, minute: 2), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("old-done", status: AutomationRunStatus.Completed, minute: 3), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("new-done", status: AutomationRunStatus.Completed, minute: 10), Ct);

        Assert.Equal(1, await store.TrimAsync(perAutomation: 1, total: 1, Ct));

        var ids = (await store.QueryAsync(new AutomationRunQuery(), Ct)).Select(r => r.Id).ToArray();
        Assert.Contains("old-running", ids);
        Assert.Contains("old-idle", ids);
        Assert.Contains("old-quota", ids);
        Assert.Contains("new-done", ids);
        Assert.DoesNotContain("old-done", ids);
    }

    [Fact]
    public async Task Trim_with_zero_limits_still_keeps_active_runs()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.TryInsertAsync(AutomationRuns.Make("active", status: AutomationRunStatus.Running), Ct);
        await store.TryInsertAsync(AutomationRuns.Make("done", minute: 1), Ct);
        await store.TrimAsync(0, 0, Ct);
        Assert.Equal(["active"], (await store.QueryAsync(new AutomationRunQuery(), Ct)).Select(r => r.Id));
    }

    [Fact]
    public async Task State_set_get_overwrite_and_remove()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        Assert.Null(await store.GetStateAsync("alive", Ct));
        await store.SetStateAsync("alive", "1", Ct);
        Assert.Equal("1", await store.GetStateAsync("alive", Ct));
        await store.SetStateAsync("alive", "2", Ct);
        Assert.Equal("2", await store.GetStateAsync("alive", Ct));
        await store.SetStateAsync("other", "x", Ct);
        await store.SetStateAsync("alive", null, Ct);
        Assert.Null(await store.GetStateAsync("alive", Ct));
        Assert.Equal("x", await store.GetStateAsync("other", Ct));
        await store.SetStateAsync("missing", null, Ct);
        await store.SetStateAsync("empty", string.Empty, Ct);
        Assert.Equal(string.Empty, await store.GetStateAsync("empty", Ct));
    }

    [Fact]
    public async Task Data_survives_reopening_the_store()
    {
        using var directory = new TempDirectory();
        await using (var first = new SqliteAutomationRunStore(directory.Path))
        {
            await first.TryInsertAsync(AutomationRuns.Make("r1"), Ct);
            await first.SetStateAsync("k", "v", Ct);
        }

        await using var second = new SqliteAutomationRunStore(directory.Path);
        Assert.NotNull(await second.GetAsync("r1", Ct));
        Assert.Equal("v", await second.GetStateAsync("k", Ct));
    }

    [Fact]
    public async Task A_cancelled_token_writes_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryInsertAsync(AutomationRuns.Make("r1"), cts.Token));
        Assert.Null(await store.GetAsync("r1", Ct));
    }

    [Fact]
    public async Task Concurrent_inserts_of_one_occurrence_let_exactly_one_win()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => store.TryInsertAsync(AutomationRuns.Make("r" + i, occurrence: "dup"), Ct)));
        Assert.Equal(1, results.Count(static r => r));
    }

    [Fact]
    public async Task A_row_with_a_corrupt_status_fails_with_a_named_error()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.TryInsertAsync(AutomationRuns.Make("r1"), Ct);
        await TodoSchemaRaw.ExecuteAsync(directory, "UPDATE automation_runs SET status = 'Bogus' WHERE id = 'r1'");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync("r1", Ct));
        Assert.Contains("status", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_refuses_a_non_canonical_timestamp()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteAutomationRunStore(directory.Path);
        await store.TryInsertAsync(AutomationRuns.Make("r1"), Ct);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            TodoSchemaRaw.ExecuteAsync(directory, "UPDATE automation_runs SET started_at = '2026-01-01 00:00:00' WHERE id = 'r1'"));
    }
}
