using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Core.Tests.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Smoke checks for the todo tool's step commit hook, step fields and plan summary operations.</summary>
public sealed class TodoToolStepCommitTests
{
    private sealed class RecordingCommitter(ITodoStore store) : IStepCommitter
    {
        public List<TodoItem> Items { get; } = [];

        public Func<TodoItem, StepCommitResult> Respond { get; set; } = _ => new StepCommitResult(StepCommitOutcome.Committed, "abc1234def5678", "committed");

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Items.Add(item);
            var result = Respond(item);
            await store.SetCommitResultAsync(
                item.Id,
                result.Outcome == StepCommitOutcome.Committed ? result.CommitId : null,
                result.Outcome == StepCommitOutcome.Failed ? result.Message : null,
                ct);
            return result;
        }
    }

    private sealed class ThrowingCommitter : IStepCommitter
    {
        public Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static ToolContext Context(string sessionId = "sess-a") => new(sessionId, Path.GetTempPath());

    private static ToolCall Call(string json) => new("call-1", "todo", json);

    /// <summary>Invokes the todo tool with <paramref name="json"/> in the default session context.</summary>
    private static ValueTask<ToolResult> InvokeAsync(TodoTool tool, string json) =>
        tool.InvokeAsync(Call(json), Context(), CancellationToken.None);

    private static async Task<string> AddAsync(TodoTool tool, string title)
    {
        var added = await InvokeAsync(tool, $$"""{"operation":"add","title":"{{title}}"}""");
        Assert.False(added.IsError);
        return added.Content[(added.Content.IndexOf('#') + 1)..added.Content.IndexOf(':')];
    }

    [Fact]
    public async Task Marking_done_commits_once_and_reports_the_short_sha()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var committer = new RecordingCommitter(store);
        var tool = new TodoTool(store, committer);
        var id = await AddAsync(tool, "Step one");

        var done = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done","commit_subject":"feat: one","summary":"did one","verification":"tests"}""");

        Assert.False(done.IsError);
        Assert.Contains("Step commit: committed abc1234", done.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abc1234d", done.Content, StringComparison.Ordinal);
        var seen = Assert.Single(committer.Items);
        Assert.Equal(TodoStatus.Done, seen.Status);
        Assert.Equal("feat: one", seen.CommitSubject);
        Assert.Equal("did one", seen.Summary);
        Assert.Equal("tests", seen.Verification);

        var again = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done"}""");
        Assert.False(again.IsError);
        Assert.DoesNotContain("Step commit", again.Content, StringComparison.Ordinal);
        Assert.Single(committer.Items);

        var other = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"in_progress"}""");
        Assert.DoesNotContain("Step commit", other.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_commit_is_not_an_error_and_retries_when_done_is_set_again()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var committer = new RecordingCommitter(store)
        {
            Respond = _ => new StepCommitResult(StepCommitOutcome.Failed, null, "pre-commit hook rejected the commit"),
        };
        var tool = new TodoTool(store, committer);
        var id = await AddAsync(tool, "Step");

        var first = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done"}""");
        Assert.False(first.IsError);
        Assert.Contains("Step commit: FAILED: pre-commit hook rejected the commit", first.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync("sess-a", default)).Status);

        committer.Respond = _ => new StepCommitResult(StepCommitOutcome.Committed, "fedcba9876", "ok");
        var retry = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done"}""");
        Assert.Contains("Step commit: committed fedcba9", retry.Content, StringComparison.Ordinal);
        Assert.Equal(2, committer.Items.Count);
        var stored = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal("fedcba9876", stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task Skipped_and_no_changes_outcomes_are_reported()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var committer = new RecordingCommitter(store)
        {
            Respond = _ => new StepCommitResult(StepCommitOutcome.Skipped, null, "step commits are off"),
        };
        var tool = new TodoTool(store, committer);
        var first = await AddAsync(tool, "A");
        var second = await AddAsync(tool, "B");

        var skipped = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{first}}","status":"done"}""");
        Assert.Contains("Step commit: skipped (step commits are off)", skipped.Content, StringComparison.Ordinal);

        committer.Respond = _ => new StepCommitResult(StepCommitOutcome.NoChanges, null, "nothing to commit");
        var none = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{second}}","status":"done"}""");
        Assert.Contains("Step commit: no changes", none.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throwing_committer_is_reported_as_failed_and_recorded_for_retry()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var tool = new TodoTool(store, new ThrowingCommitter());
        var id = await AddAsync(tool, "Step");

        var result = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done"}""");

        Assert.False(result.IsError);
        Assert.Contains("Step commit: FAILED: boom", result.Content, StringComparison.Ordinal);
        var stored = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Equal("boom", stored.CommitError);
    }

    [Fact]
    public async Task Without_a_committer_done_just_updates_and_step_fields_can_be_set_alone()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var tool = new TodoTool(store);
        var id = await AddAsync(tool, "Step");

        var done = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","status":"done","summary":"s"}""");
        Assert.False(done.IsError);
        Assert.DoesNotContain("Step commit", done.Content, StringComparison.Ordinal);

        var fieldsOnly = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}","verification":"v"}""");
        Assert.False(fieldsOnly.IsError);
        var stored = Assert.Single(await store.ListAsync("sess-a", default));
        Assert.Equal("s", stored.Summary);
        Assert.Equal("v", stored.Verification);

        var nothing = await InvokeAsync(tool, $$"""{"operation":"update","id":"{{id}}"}""");
        Assert.True(nothing.IsError);
    }

    [Fact]
    public async Task List_shows_commit_state_and_truncates_long_summaries()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var tool = new TodoTool(store);
        var done = await AddAsync(tool, "Done step");
        var failed = await AddAsync(tool, "Failed step");
        var longSummary = new string('x', 300);
        await store.UpdateAsync(done, null, TodoStatus.Done, null, new TodoStepFields("feat: done", longSummary, "line one\nline two"), default);
        await store.SetCommitResultAsync(done, "abcdef0123456789", null, default);
        await store.UpdateAsync(failed, null, TodoStatus.Done, null, default);
        await store.SetCommitResultAsync(failed, null, "hook failed", default);

        var list = await InvokeAsync(tool, """{"operation":"list"}""");

        Assert.Contains("[commit abcdef0]", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef01", list.Content, StringComparison.Ordinal);
        Assert.Contains("[commit failed: hook failed]", list.Content, StringComparison.Ordinal);
        Assert.Contains("commit_subject: feat: done", list.Content, StringComparison.Ordinal);
        Assert.Contains("verification: line one line two", list.Content, StringComparison.Ordinal);
        Assert.Contains(new string('x', 119) + "…", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 121), list.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_summary_operations_round_trip_and_resolve_side_effects()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var tool = new TodoTool(store);

        var empty = await InvokeAsync(tool, """{"operation":"plan_summary_get"}""");
        Assert.False(empty.IsError);
        Assert.Contains("No plan summary", empty.Content, StringComparison.Ordinal);

        var rejected = await InvokeAsync(tool, """{"operation":"plan_summary_set","text":"  "}""");
        Assert.True(rejected.IsError);

        var saved = await InvokeAsync(tool, """{"operation":"plan_summary_set","text":"## Summary\nIt works."}""");
        Assert.False(saved.IsError);
        var read = await InvokeAsync(tool, """{"operation":"plan_summary_get"}""");
        Assert.Equal("## Summary\nIt works.", read.Content);
        var other = await tool.InvokeAsync(Call("""{"operation":"plan_summary_get"}"""), Context("sess-b"), CancellationToken.None);
        Assert.Contains("No plan summary", other.Content, StringComparison.Ordinal);

        Assert.Equal(ToolSideEffect.ReadOnly, ToolRegistry.SideEffectOf(tool, Call("""{"operation":"list"}""")));
        Assert.Equal(ToolSideEffect.ReadOnly, ToolRegistry.SideEffectOf(tool, Call("""{"operation":"plan_summary_get"}""")));
        Assert.Equal(ToolSideEffect.Write, ToolRegistry.SideEffectOf(tool, Call("""{"operation":"plan_summary_set","text":"x"}""")));
        Assert.Equal(ToolSideEffect.Write, ToolRegistry.SideEffectOf(tool, Call("""{"operation":"update","id":"x","status":"done"}""")));

        var unknown = await InvokeAsync(tool, """{"operation":"frobnicate"}""");
        Assert.True(unknown.IsError);
        Assert.Contains("plan_summary_set", unknown.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_description_and_schema_advertise_the_new_surface()
    {
        var tool = new TodoTool(new RecordingStoreStub());

        Assert.Contains("git_log", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("Conventional Commits", tool.Definition.Description, StringComparison.Ordinal);
        foreach (var name in new[] { "plan_summary_get", "plan_summary_set", "commit_subject", "summary", "verification", "text" })
        {
            Assert.Contains($"\"{name}\"", tool.Definition.InputSchemaJson, StringComparison.Ordinal);
        }
    }

    private sealed class RecordingStoreStub : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) => throw new NotSupportedException();

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => throw new NotSupportedException();
    }
}
