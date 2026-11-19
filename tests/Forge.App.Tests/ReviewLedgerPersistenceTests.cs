using Forge.Abstractions;
using Forge.App.Git;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Core.Tools;
using Forge.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

public sealed class ReviewLedgerPersistenceTests : IDisposable
{
    private const string Session = "s1";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "forge-ledger-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ReviewStore NewReviews() => new(dir);

    private static StepReviewLedger Ledger(ReviewStore reviews) => new(new StepReviewLedgerStore(reviews));

    [Fact]
    public async Task Verdict_tree_commit_and_rounds_survive_a_new_ledger()
    {
        var reviews = NewReviews();
        var first = Ledger(reviews);
        first.Record(Session, "1", ReviewVerdict.RequestChanges, "c1", "T1");
        first.Record(Session, "1", ReviewVerdict.RequestChanges, "c2", "T2");
        first.Record(Session, "2", ReviewVerdict.Approve, "c3", "T3");
        await first.FlushAsync();

        var second = Ledger(reviews);
        await second.EnsureLoadedAsync(Session, CancellationToken.None);

        var one = second.Find(Session, "1");
        Assert.NotNull(one);
        Assert.Equal(ReviewVerdict.RequestChanges, one.Verdict);
        Assert.Equal("c2", one.CommitId);
        Assert.Equal("T2", one.Tree);
        Assert.Equal(2, second.Rounds(Session, "1"));
        Assert.Equal(ReviewVerdict.Approve, second.Find(Session, "2")!.Verdict);
        Assert.Equal(0, second.Rounds(Session, "2"));
    }

    [Fact]
    public async Task Forget_is_persisted()
    {
        var reviews = NewReviews();
        var first = Ledger(reviews);
        first.Record(Session, "1", ReviewVerdict.Approve, "c1", "T1");
        first.Forget(Session, "1");
        await first.FlushAsync();

        var second = Ledger(reviews);
        await second.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Null(second.Find(Session, "1"));
    }

    [Fact]
    public async Task Entry_recorded_in_this_host_wins_over_the_stored_one()
    {
        var reviews = NewReviews();
        var first = Ledger(reviews);
        first.Record(Session, "1", ReviewVerdict.Approve, "old", "T1");
        await first.FlushAsync();

        var second = Ledger(reviews);
        second.Record(Session, "1", ReviewVerdict.RequestChanges, "new", "T2");
        await second.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Equal("new", second.Find(Session, "1")!.CommitId);
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_verdicts()
    {
        var reviews = NewReviews();
        var first = Ledger(reviews);
        first.Record(Session, "1", ReviewVerdict.Approve, "c1", "T1");
        first.Record("other", "1", ReviewVerdict.Approve, "c9", "T9");
        await first.FlushAsync();

        await reviews.DeleteSessionAsync(Session);

        var second = Ledger(reviews);
        await second.EnsureLoadedAsync(Session, CancellationToken.None);
        await second.EnsureLoadedAsync("other", CancellationToken.None);
        Assert.Null(second.Find(Session, "1"));
        Assert.NotNull(second.Find("other", "1"));
    }

    private sealed class FlakyStore : IStepReviewStore
    {
        public int Loads;

        public Task<IReadOnlyList<StepReviewEntry>> LoadAsync(string sessionId, CancellationToken ct)
        {
            if (++Loads == 1)
            {
                throw new IOException("locked");
            }

            return Task.FromResult<IReadOnlyList<StepReviewEntry>>([new StepReviewEntry("1", new StepReviewRecord(ReviewVerdict.Approve, "c", DateTimeOffset.UtcNow, "T"), 0)]);
        }

        public Task SaveAsync(string sessionId, StepReviewEntry entry, CancellationToken ct) => throw new IOException("locked");

        public Task DeleteAsync(string sessionId, string stepId, CancellationToken ct) => throw new IOException("locked");
    }

    [Fact]
    public async Task A_failing_store_never_throws_and_a_failed_load_is_retried()
    {
        var store = new FlakyStore();
        var ledger = new StepReviewLedger(store);

        await ledger.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Null(ledger.Find(Session, "1"));

        ledger.Record(Session, "2", ReviewVerdict.Approve, "c", "T");
        ledger.Forget(Session, "2");
        await ledger.FlushAsync();

        await ledger.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Equal(2, store.Loads);
        Assert.NotNull(ledger.Find(Session, "1"));
    }

    [Fact]
    public async Task A_forget_before_the_load_runs_is_not_undone_by_the_stored_row()
    {
        var reviews = NewReviews();
        var first = Ledger(reviews);
        first.Record(Session, "1", ReviewVerdict.Approve, "c1", "T1");
        await first.FlushAsync();

        var second = Ledger(reviews);
        var loading = second.EnsureLoadedAsync(Session, CancellationToken.None);
        second.Forget(Session, "1");
        await loading;
        await second.FlushAsync();
        Assert.Null(second.Find(Session, "1"));

        var third = Ledger(reviews);
        await third.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Null(third.Find(Session, "1"));
    }

    [Fact]
    public async Task Concurrent_records_and_forgets_leave_memory_and_database_in_the_same_state()
    {
        var reviews = NewReviews();
        for (var round = 0; round < 20; round++)
        {
            var ledger = Ledger(reviews);
            var step = "r" + round;
            await Task.WhenAll(
                Task.Run(() => ledger.Record(Session, step, ReviewVerdict.Approve, "c", "T")),
                Task.Run(() => ledger.Forget(Session, step)));
            await ledger.FlushAsync();

            var fresh = Ledger(reviews);
            await fresh.EnsureLoadedAsync(Session, CancellationToken.None);
            // Both sides must agree on the entry itself, not just on whether it exists: same verdict, commit and tree.
            Assert.Equal(ledger.Find(Session, step), fresh.Find(Session, step));
        }
    }

    private sealed class FakeProbe(Func<StepChangeProbe> answer) : IStepChangeProbe
    {
        public Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct) => Task.FromResult(answer());
    }

    private static Task<ToolResult> Todo(TodoTool tool, string json) =>
        tool.InvokeAsync(new ToolCall("c", TodoTool.Name, json), new ToolContext(Session, "wd"), CancellationToken.None).AsTask();

    [Fact]
    public async Task TodoTool_captures_a_baseline_gates_done_on_a_review_and_forgets_it_when_reopened()
    {
        await using var todos = new SqliteTodoStore(Path.Combine(dir, "todos"));
        var ledger = Ledger(NewReviews());
        var tree = "T1";
        var gate = new StepGate(ledger, new FakeProbe(() => new StepChangeProbe(StepChangeState.Dirty, "1 change", tree)));
        var tool = new TodoTool(todos, null, gate);
        var item = await todos.AddAsync(Session, "step", null, null, CancellationToken.None);

        var started = await Todo(tool, $$"""{"operation":"update","id":"{{item.Id}}","status":"in_progress"}""");
        Assert.False(started.IsError, started.Content);
        Assert.Equal("T1", (await todos.ListAsync(Session, CancellationToken.None)).Single().BaselineTree);

        tree = "T2";
        var refused = await Todo(tool, $$"""{"operation":"update","id":"{{item.Id}}","status":"done","verification":"build and tests pass"}""");
        Assert.True(refused.IsError);
        Assert.Contains("request_review", refused.Content);

        ledger.Record(Session, item.Id, ReviewVerdict.Approve, "c1", "T2");
        var done = await Todo(tool, $$"""{"operation":"update","id":"{{item.Id}}","status":"done","verification":"build and tests pass"}""");
        Assert.False(done.IsError, done.Content);

        var reopened = await Todo(tool, $$"""{"operation":"update","id":"{{item.Id}}","status":"pending"}""");
        Assert.False(reopened.IsError, reopened.Content);
        await ledger.FlushAsync();
        Assert.Null(ledger.Find(Session, item.Id));
        Assert.Equal("T1", (await todos.ListAsync(Session, CancellationToken.None)).Single().BaselineTree);

        var fresh = Ledger(NewReviews());
        await fresh.EnsureLoadedAsync(Session, CancellationToken.None);
        Assert.Null(fresh.Find(Session, item.Id));
    }
}
