using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Core.Tests.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Edge behaviour of the <c>todo</c> tool's step commit hook, step fields and plan summary: when exactly the committer runs,
/// what each outcome reports, what a misbehaving committer or store can and cannot do, and the exact list rendering. The
/// smoke checks live in <see cref="TodoToolStepCommitTests"/>.
/// </summary>
public sealed class TodoToolStepCommitEdgeTests
{
    private const string Session = "sess-a";

    private static ToolCall Call(string json) => new("call-1", "todo", json);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string UpdateJson(string id, params string[] properties) =>
        "{\"operation\":\"update\",\"id\":" + Quote(id) + (properties.Length == 0 ? string.Empty : "," + string.Join(",", properties)) + "}";

    private static string LineStartingWith(string content, string prefix) =>
        Assert.Single(content.Split('\n'), line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Forwards everything to a real store and can be told to fail the commit-result write.</summary>
    private sealed class FaultableStore(ITodoStore inner) : ITodoStore
    {
        public Exception? SetCommitResultFault { get; set; }

        public event Action<string>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) => inner.ListAsync(sessionId, ct);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            inner.AddAsync(sessionId, title, note, afterId, ct);

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            inner.UpdateAsync(id, title, status, note, ct);

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, TodoStepFields? step, CancellationToken ct) =>
            inner.UpdateAsync(id, title, status, note, step, ct);

        public Task<TodoItem?> SetCommitResultAsync(string id, string? commitId, string? commitError, CancellationToken ct) =>
            SetCommitResultFault is { } fault ? throw fault : inner.SetCommitResultAsync(id, commitId, commitError, ct);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => inner.RemoveAsync(id, ct);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => inner.MoveAsync(id, order, ct);

        public Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct) => inner.GetPlanSummaryAsync(sessionId, ct);

        public Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct) =>
            inner.SetPlanSummaryAsync(sessionId, text, ct);
    }

    /// <summary>Records every call (item, token, what the store held at that moment) and answers from a script.</summary>
    private sealed class ScriptedCommitter(ITodoStore store) : IStepCommitter
    {
        public List<TodoItem> Items { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public List<TodoItem> StoredAtCall { get; } = [];

        public StepCommitResult Result { get; set; } = new(StepCommitOutcome.Committed, "abc1234def5678", "committed");

        public Func<TodoItem, CancellationToken, Task<StepCommitResult>>? Handler { get; set; }

        /// <summary>When true the committer persists its outcome the way the contract asks real committers to.</summary>
        public bool Persist { get; set; } = true;

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Items.Add(item);
            Tokens.Add(ct);
            StoredAtCall.Add(Assert.Single(await store.ListAsync(item.SessionId, ct), stored => stored.Id == item.Id));
            var result = Handler is null ? Result : await Handler(item, ct);
            if (Persist)
            {
                await store.SetCommitResultAsync(
                    item.Id,
                    result.Outcome == StepCommitOutcome.Committed ? result.CommitId : null,
                    result.Outcome == StepCommitOutcome.Failed ? result.Message : null,
                    ct);
            }

            return result;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();

        public Rig(bool withCommitter = true)
        {
            Sqlite = new SqliteTodoStore(directory.Path);
            Store = new FaultableStore(Sqlite);
            Committer = new ScriptedCommitter(Store);
            Tool = new TodoTool(Store, withCommitter ? Committer : null);
        }

        public SqliteTodoStore Sqlite { get; }

        public FaultableStore Store { get; }

        public ScriptedCommitter Committer { get; }

        public TodoTool Tool { get; }

        public async Task<string> AddAsync(string title = "Step", string session = Session) =>
            (await Sqlite.AddAsync(session, title, null, null, default)).Id;

        public async Task<TodoItem> GetAsync(string id, string session = Session) =>
            Assert.Single(await Sqlite.ListAsync(session, default), item => item.Id == id);

        public async Task<ToolResult> InvokeAsync(string json, string session = Session, CancellationToken ct = default) =>
            await Tool.InvokeAsync(Call(json), new ToolContext(session, Path.GetTempPath()), ct);

        public async ValueTask DisposeAsync()
        {
            await Sqlite.DisposeAsync();
            directory.Dispose();
        }
    }

    /// <summary>A store that predates step fields and plan summaries: only the original members, the defaults do the rest.</summary>
    private sealed class LegacyStore : ITodoStore
    {
        private readonly List<TodoItem> items = [];

        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([.. items.Where(item => item.SessionId == sessionId)]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct)
        {
            var item = new TodoItem(Guid.NewGuid().ToString("n"), sessionId, title, TodoStatus.Pending, note, items.Count, DateTimeOffset.UtcNow);
            items.Add(item);
            return Task.FromResult(item);
        }

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct)
        {
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return Task.FromResult<TodoItem?>(null);
            }

            items[index] = items[index] with
            {
                Title = title ?? items[index].Title,
                Status = status ?? items[index].Status,
                Note = note ?? items[index].Note,
            };
            return Task.FromResult<TodoItem?>(items[index]);
        }

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(items.RemoveAll(item => item.Id == id) > 0);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }

    // ---------------------------------------------------------------- side-effect resolution

    [Theory]
    [InlineData("""{"operation":"list"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"List"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"plan_summary_get"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"PLAN_SUMMARY_GET"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"add","title":"x"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"update","id":"x","status":"done"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"update","id":"x","summary":"s"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"remove","id":"x"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"plan_summary_set","text":"t"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"Plan_Summary_Set","text":"t"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"frobnicate"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":""}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":null}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":1}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":["list"]}""", ToolSideEffect.Write)]
    [InlineData("""{"op":"list"}""", ToolSideEffect.Write)]
    [InlineData("""{}""", ToolSideEffect.Write)]
    public void Side_effect_is_resolved_per_call_from_the_operation(string json, ToolSideEffect expected)
    {
        var tool = new TodoTool(new LegacyStore());

        Assert.Equal(expected, ToolRegistry.SideEffectOf(tool, Call(json)));
        Assert.Equal(ToolSideEffect.Write, tool.SideEffect);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"operation":"update","id":"x",""")]
    public async Task Malformed_json_resolves_read_only_because_invoking_it_is_a_no_op_error(string json)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        var itemsBefore = await rig.Sqlite.ListAsync(Session, default);

        // ReadOnly is only safe if the very same call really changes nothing.
        Assert.Equal(ToolSideEffect.ReadOnly, ToolRegistry.SideEffectOf(rig.Tool, Call(json)));
        var result = await rig.InvokeAsync(json);

        Assert.True(result.IsError);
        Assert.Contains("invalid", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(itemsBefore, await rig.Sqlite.ListAsync(Session, default));
        Assert.Null(await rig.Sqlite.GetPlanSummaryAsync(Session, default));
        Assert.Empty(rig.Committer.Items);
        Assert.Equal("Step", (await rig.GetAsync(id)).Title);
    }

    [Theory]
    [InlineData("""{"operation":"list","id":"IDPLACEHOLDER","title":"hijack","status":"done","note":"n","commit_subject":"s","summary":"m","verification":"v","text":"plan","after_id":"IDPLACEHOLDER"}""")]
    [InlineData("""{"operation":"plan_summary_get","id":"IDPLACEHOLDER","title":"hijack","status":"done","summary":"m","text":"plan"}""")]
    public async Task Read_only_operations_ignore_write_arguments_and_never_change_anything(string template)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        await rig.Sqlite.SetPlanSummaryAsync(Session, "kept plan", default);
        var itemsBefore = await rig.Sqlite.ListAsync(Session, default);
        var json = template.Replace("IDPLACEHOLDER", id, StringComparison.Ordinal);
        Assert.Equal(ToolSideEffect.ReadOnly, ToolRegistry.SideEffectOf(rig.Tool, Call(json)));

        var result = await rig.InvokeAsync(json);

        Assert.False(result.IsError);
        Assert.Equal(itemsBefore, await rig.Sqlite.ListAsync(Session, default));
        Assert.Equal("kept plan", (await rig.Sqlite.GetPlanSummaryAsync(Session, default))?.Text);
        Assert.Empty(rig.Committer.Items);
    }

    // Defect probe: a model that sends valid JSON which is not an object (an array, a scalar, null) makes the resolver throw.
    // AgentLoop calls it outside any try block, so the whole turn fails instead of the call returning an error result.
    [Theory(Skip = "defect: TodoTool.Resolve throws InvalidOperationException (TryGetProperty on a non-object root; only JsonException is caught) for arguments like [] , null, 5 or \"list\"; AgentLoop.ExecuteToolsAsync calls SideEffectOf outside any try/catch, so the whole agent turn fails with AgentFailed")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"list\"")]
    public void Resolve_does_not_throw_for_arguments_that_are_valid_json_but_not_an_object(string json)
    {
        var tool = new TodoTool(new LegacyStore());

        var exception = Record.Exception(() => ToolRegistry.SideEffectOf(tool, Call(json)));

        Assert.Null(exception);
    }

    [Theory(Skip = "defect: TodoTool.InvokeAsync throws InvalidOperationException (TryGetProperty on a non-object root; only JsonException is caught) instead of returning an invalid-arguments ToolResult for arguments like [] , null, 5 or \"list\"; AgentLoop.RunOneAsync catches it and turns it into an error result, so only direct callers of the tool see the throw")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"list\"")]
    public async Task Invoke_reports_arguments_that_are_valid_json_but_not_an_object_as_a_tool_error(string json)
    {
        var tool = new TodoTool(new LegacyStore());

        var result = await tool.InvokeAsync(Call(json), new ToolContext(Session, Path.GetTempPath()), CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ---------------------------------------------------------------- update: step fields without a status change

    [Theory]
    [InlineData("commit_subject")]
    [InlineData("summary")]
    [InlineData("verification")]
    public async Task Update_with_only_one_step_field_stores_it_and_neither_commits_nor_changes_the_status(string field)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, $"\"{field}\":\"value\""));

        Assert.False(result.IsError);
        Assert.Equal($"Updated #{id}: [ ] #{id} Step", result.Content);
        var stored = await rig.GetAsync(id);
        Assert.Equal(TodoStatus.Pending, stored.Status);
        Assert.Equal(field == "commit_subject" ? "value" : null, stored.CommitSubject);
        Assert.Equal(field == "summary" ? "value" : null, stored.Summary);
        Assert.Equal(field == "verification" ? "value" : null, stored.Verification);
        Assert.Empty(rig.Committer.Items);
    }

    [Fact]
    public async Task Update_with_an_empty_string_clears_a_step_field_and_null_or_absent_keeps_it()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        await rig.InvokeAsync(UpdateJson(id, "\"commit_subject\":\"s\"", "\"summary\":\"m\"", "\"verification\":\"v\""));

        var kept = await rig.InvokeAsync(UpdateJson(id, "\"note\":\"only a note\"", "\"summary\":null"));
        Assert.False(kept.IsError);
        var afterKept = await rig.GetAsync(id);
        Assert.Equal(("s", "m", "v"), (afterKept.CommitSubject, afterKept.Summary, afterKept.Verification));

        var cleared = await rig.InvokeAsync(UpdateJson(id, "\"verification\":\"\""));
        Assert.False(cleared.IsError);
        var afterCleared = await rig.GetAsync(id);
        Assert.Equal(("s", "m", (string?)null), (afterCleared.CommitSubject, afterCleared.Summary, afterCleared.Verification));
    }

    [Fact]
    public async Task Update_without_any_field_or_with_a_blank_title_changes_nothing()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        var before = await rig.GetAsync(id);

        var nothing = await rig.InvokeAsync(UpdateJson(id));
        var blankTitle = await rig.InvokeAsync(UpdateJson(id, "\"title\":\"  \"", "\"summary\":\"s\"", "\"status\":\"done\""));

        Assert.True(nothing.IsError);
        Assert.Contains("requires", nothing.Content, StringComparison.Ordinal);
        Assert.True(blankTitle.IsError);
        Assert.Equal(before, await rig.GetAsync(id));
        Assert.Empty(rig.Committer.Items);
    }

    // ---------------------------------------------------------------- when the committer runs

    [Fact]
    public async Task Pending_to_done_commits_exactly_once_with_the_item_as_updated_by_this_very_call()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Old title");
        await rig.Sqlite.UpdateAsync(id, null, null, "the note", default);

        var result = await rig.InvokeAsync(
            UpdateJson(id, "\"title\":\"New title\"", "\"status\":\"done\"", "\"commit_subject\":\"feat: new\"", "\"summary\":\"did it\"", "\"verification\":\"ran tests\""));

        Assert.False(result.IsError);
        var seen = Assert.Single(rig.Committer.Items);
        Assert.Equal(id, seen.Id);
        Assert.Equal(Session, seen.SessionId);
        Assert.Equal("New title", seen.Title);
        Assert.Equal(TodoStatus.Done, seen.Status);
        Assert.Equal("the note", seen.Note);
        Assert.Equal(0, seen.Order);
        Assert.Equal(("feat: new", "did it", "ran tests"), (seen.CommitSubject, seen.Summary, seen.Verification));
        Assert.Null(seen.CommitId);
        Assert.Null(seen.CommitError);

        // By the time the committer runs the done status is already durable, so a failing commit can never undo it.
        var stored = Assert.Single(rig.Committer.StoredAtCall);
        Assert.Equal(seen, stored);
    }

    [Fact]
    public async Task The_tool_passes_its_cancellation_token_to_the_committer()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        using var cts = new CancellationTokenSource();

        await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""), ct: cts.Token);

        Assert.Equal(cts.Token, Assert.Single(rig.Committer.Tokens));
    }

    [Fact]
    public async Task In_progress_to_done_commits()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        var started = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"in_progress\""));
        Assert.DoesNotContain("Step commit", started.Content, StringComparison.Ordinal);
        Assert.Empty(rig.Committer.Items);

        var finished = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.Contains("Step commit: committed abc1234", finished.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(rig.Committer.Items).Status);
    }

    [Theory]
    [InlineData(StepCommitOutcome.Committed)]
    [InlineData(StepCommitOutcome.NoChanges)]
    [InlineData(StepCommitOutcome.Skipped)]
    public async Task Done_to_done_does_not_commit_again_when_the_last_attempt_left_no_error(StepCommitOutcome outcome)
    {
        await using var rig = new Rig();
        rig.Committer.Result = new StepCommitResult(outcome, outcome == StepCommitOutcome.Committed ? "abc1234def" : null, "message");
        var id = await rig.AddAsync();
        await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));
        Assert.Single(rig.Committer.Items);

        var again = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));
        var withFields = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"", "\"summary\":\"edited later\""));
        var aliased = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"completed\""));

        foreach (var result in new[] { again, withFields, aliased })
        {
            Assert.False(result.IsError);
            Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        }

        Assert.Single(rig.Committer.Items);
        Assert.Equal("edited later", (await rig.GetAsync(id)).Summary);
    }

    [Fact]
    public async Task Done_with_a_commit_error_retries_on_every_done_until_it_succeeds_and_then_stops()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        rig.Committer.Result = new StepCommitResult(StepCommitOutcome.Failed, null, "hook failed");

        var first = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"", "\"summary\":\"first try\""));
        var second = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"", "\"summary\":\"second try\""));
        rig.Committer.Result = new StepCommitResult(StepCommitOutcome.Committed, "fedcba9876", "ok");
        var third = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));
        var fourth = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.Contains("FAILED: hook failed", first.Content, StringComparison.Ordinal);
        Assert.Contains("FAILED: hook failed", second.Content, StringComparison.Ordinal);
        Assert.Contains("Step commit: committed fedcba9", third.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Step commit", fourth.Content, StringComparison.Ordinal);
        Assert.Equal(3, rig.Committer.Items.Count);
        Assert.Equal(["first try", "second try", "second try"], rig.Committer.Items.Select(item => item.Summary!).ToArray());
        Assert.All(rig.Committer.StoredAtCall, stored => Assert.Equal(TodoStatus.Done, stored.Status));
        Assert.Equal("hook failed", rig.Committer.StoredAtCall[2].CommitError);
        Assert.Null(rig.Committer.StoredAtCall[2].CommitId);
        var stored = await rig.GetAsync(id);
        Assert.Equal("fedcba9876", stored.CommitId);
        Assert.Null(stored.CommitError);
        Assert.Equal(TodoStatus.Done, stored.Status);
    }

    [Theory]
    [InlineData(false, "\"status\":\"pending\"")]
    [InlineData(false, "\"status\":\"in_progress\"")]
    [InlineData(false, "\"note\":\"n\"")]
    [InlineData(false, "\"title\":\"renamed\"")]
    [InlineData(false, "\"summary\":\"s\"")]
    [InlineData(false, "\"commit_subject\":\"c\"")]
    [InlineData(true, "\"status\":\"pending\"")]
    [InlineData(true, "\"status\":\"in_progress\"")]
    [InlineData(true, "\"note\":\"n\"")]
    [InlineData(true, "\"title\":\"renamed\"")]
    [InlineData(true, "\"summary\":\"s\"")]
    [InlineData(true, "\"verification\":\"v\"")]
    public async Task Updates_that_do_not_say_done_never_commit_even_on_an_item_with_a_failed_commit(bool startDoneWithError, string property)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        if (startDoneWithError)
        {
            await rig.Sqlite.UpdateAsync(id, null, TodoStatus.Done, null, default);
            await rig.Sqlite.SetCommitResultAsync(id, null, "earlier failure", default);
        }

        var result = await rig.InvokeAsync(UpdateJson(id, property));

        Assert.False(result.IsError);
        Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        Assert.Empty(rig.Committer.Items);
        if (startDoneWithError && property.Contains("\"summary\"", StringComparison.Ordinal))
        {
            Assert.Equal("earlier failure", (await rig.GetAsync(id)).CommitError);
        }
    }

    [Fact]
    public async Task Reopening_a_committed_step_and_finishing_it_again_commits_again()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));
        await rig.InvokeAsync(UpdateJson(id, "\"status\":\"pending\""));

        var again = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.Contains("Step commit: committed", again.Content, StringComparison.Ordinal);
        Assert.Equal(2, rig.Committer.Items.Count);
    }

    [Theory]
    [InlineData("done", true)]
    [InlineData("DONE", true)]
    [InlineData("  Done  ", true)]
    [InlineData("completed", true)]
    [InlineData("closed", true)]
    [InlineData("pending", false)]
    [InlineData("todo", false)]
    [InlineData("open", false)]
    [InlineData("in_progress", false)]
    [InlineData("in-progress", false)]
    [InlineData("inprogress", false)]
    [InlineData("doing", false)]
    [InlineData("running", false)]
    public async Task Status_aliases_decide_whether_the_step_commits(string status, bool commits)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();

        var result = await rig.InvokeAsync(UpdateJson(id, $"\"status\":{Quote(status)}"));

        Assert.False(result.IsError);
        Assert.Equal(commits ? 1 : 0, rig.Committer.Items.Count);
        Assert.Equal(commits, result.Content.Contains("Step commit", StringComparison.Ordinal));
        Assert.Equal(commits, (await rig.GetAsync(id)).Status == TodoStatus.Done);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("")]
    [InlineData("done!")]
    public async Task An_unknown_status_is_rejected_before_anything_is_written_or_committed(string status)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        var before = await rig.GetAsync(id);

        var result = await rig.InvokeAsync(UpdateJson(id, $"\"status\":{Quote(status)}", "\"summary\":\"must not be stored\""));

        Assert.True(result.IsError);
        Assert.Contains("status must be", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.GetAsync(id));
        Assert.Empty(rig.Committer.Items);
    }

    [Fact]
    public async Task Without_a_committer_no_update_ever_mentions_a_step_commit()
    {
        await using var rig = new Rig(withCommitter: false);
        var id = await rig.AddAsync();
        var failedId = await rig.AddAsync("Failed earlier");
        await rig.Sqlite.UpdateAsync(failedId, null, TodoStatus.Done, null, default);
        await rig.Sqlite.SetCommitResultAsync(failedId, null, "earlier failure", default);

        var fresh = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"", "\"summary\":\"s\""));
        var retry = await rig.InvokeAsync(UpdateJson(failedId, "\"status\":\"done\""));

        foreach (var result in new[] { fresh, retry })
        {
            Assert.False(result.IsError);
            Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        }

        Assert.Empty(rig.Committer.Items);
        Assert.Equal(TodoStatus.Done, (await rig.GetAsync(id)).Status);
        Assert.Equal("earlier failure", (await rig.GetAsync(failedId)).CommitError);
    }

    // ---------------------------------------------------------------- what each outcome reports

    [Theory]
    [InlineData(StepCommitOutcome.Committed, "abc1234def5678", "committed", "Step commit: committed abc1234")]
    [InlineData(StepCommitOutcome.Committed, "abcdefg", "m", "Step commit: committed abcdefg")]
    [InlineData(StepCommitOutcome.Committed, "abcdef", "m", "Step commit: committed abcdef")]
    [InlineData(StepCommitOutcome.Committed, "ab", "m", "Step commit: committed ab")]
    [InlineData(StepCommitOutcome.Committed, null, "made the commit", "Step commit: committed (made the commit)")]
    [InlineData(StepCommitOutcome.Committed, "", "from message", "Step commit: committed (from message)")]
    [InlineData(StepCommitOutcome.Committed, null, "   ", "Step commit: committed")]
    [InlineData(StepCommitOutcome.Committed, null, "", "Step commit: committed")]
    [InlineData(StepCommitOutcome.NoChanges, null, "nothing to commit", "Step commit: no changes")]
    [InlineData(StepCommitOutcome.NoChanges, null, "", "Step commit: no changes")]
    [InlineData(StepCommitOutcome.Skipped, null, "not a git repository", "Step commit: skipped (not a git repository)")]
    [InlineData(StepCommitOutcome.Skipped, null, "", "Step commit: skipped")]
    [InlineData(StepCommitOutcome.Skipped, null, "  \t", "Step commit: skipped")]
    public async Task Each_outcome_is_reported_in_its_documented_words_and_is_never_a_tool_error(
        StepCommitOutcome outcome,
        string? commitId,
        string message,
        string expectedLine)
    {
        await using var rig = new Rig();
        rig.Committer.Persist = false;
        rig.Committer.Result = new StepCommitResult(outcome, commitId, message);
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.False(result.IsError);
        Assert.Equal($"Updated #{id}: [x] #{id} Step\n{expectedLine}", result.Content);
        Assert.Equal(TodoStatus.Done, (await rig.GetAsync(id)).Status);
    }

    [Theory]
    [InlineData("pre-commit hook rejected the commit", "Step commit: FAILED: pre-commit hook rejected the commit")]
    [InlineData("", "Step commit: FAILED: the commit failed without a message")]
    [InlineData("   ", "Step commit: FAILED: the commit failed without a message")]
    public async Task A_failed_outcome_names_the_cause_says_the_item_stays_done_and_is_not_a_tool_error(string message, string expectedStart)
    {
        await using var rig = new Rig();
        rig.Committer.Persist = false;
        rig.Committer.Result = new StepCommitResult(StepCommitOutcome.Failed, null, message);
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.False(result.IsError);
        Assert.StartsWith($"Updated #{id}: [x] #{id} Step\n{expectedStart}", result.Content, StringComparison.Ordinal);
        Assert.Contains("the item stays done", result.Content, StringComparison.Ordinal);
        Assert.Contains("status done again", result.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, (await rig.GetAsync(id)).Status);
    }

    // ---------------------------------------------------------------- a misbehaving committer or store

    [Theory]
    [InlineData("boom", "boom")]
    [InlineData("", "the commit failed")]
    public async Task A_committer_that_throws_is_reported_as_failed_recorded_for_retry_and_leaves_the_item_done(string exceptionMessage, string recordedError)
    {
        await using var rig = new Rig();
        rig.Committer.Handler = (_, _) => throw new InvalidOperationException(exceptionMessage);
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"", "\"summary\":\"kept\""));

        Assert.False(result.IsError);
        Assert.Contains("Step commit: FAILED: ", result.Content, StringComparison.Ordinal);
        Assert.Contains(exceptionMessage.Length == 0 ? "the commit failed without a message" : exceptionMessage, result.Content, StringComparison.Ordinal);
        var stored = await rig.GetAsync(id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Equal("kept", stored.Summary);
        Assert.Equal(recordedError, stored.CommitError);
        Assert.Null(stored.CommitId);

        // The recorded error is what opens the retry path, and the retry runs the committer again.
        rig.Committer.Handler = null;
        var retry = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));
        Assert.Contains("Step commit: committed abc1234", retry.Content, StringComparison.Ordinal);
        Assert.Equal(2, rig.Committer.Items.Count);
        Assert.Null((await rig.GetAsync(id)).CommitError);
    }

    [Fact]
    public async Task A_committer_failure_is_still_reported_when_recording_it_in_the_store_fails_too()
    {
        await using var rig = new Rig();
        rig.Committer.Handler = (_, _) => throw new InvalidOperationException("committer boom");
        rig.Store.SetCommitResultFault = new InvalidOperationException("store boom");
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.False(result.IsError);
        Assert.Contains("Step commit: FAILED: committer boom", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("store boom", result.Content, StringComparison.Ordinal);
        var stored = await rig.GetAsync(id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task Cancellation_thrown_by_the_committer_propagates_and_is_not_recorded_as_a_failure()
    {
        await using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.Committer.Handler = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new StepCommitResult(StepCommitOutcome.Committed, "abc", "unreachable"));
        };
        var id = await rig.AddAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""), ct: cts.Token));

        var stored = await rig.GetAsync(id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitError);
        Assert.Null(stored.CommitId);
    }

    [Fact]
    public async Task A_cancellation_exception_from_the_committer_propagates_even_when_the_callers_token_is_not_cancelled()
    {
        await using var rig = new Rig();
        rig.Committer.Handler = (_, _) => throw new TaskCanceledException("internal timeout");
        var id = await rig.AddAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\"")));

        Assert.Null((await rig.GetAsync(id)).CommitError);
    }

    [Theory]
    [InlineData("""{"operation":"list"}""")]
    [InlineData("""{"operation":"add","title":"new"}""")]
    [InlineData("""{"operation":"update","id":"ID","status":"done"}""")]
    [InlineData("""{"operation":"remove","id":"ID"}""")]
    [InlineData("""{"operation":"plan_summary_get"}""")]
    [InlineData("""{"operation":"plan_summary_set","text":"plan"}""")]
    public async Task A_cancelled_token_cancels_every_operation_instead_of_turning_into_a_tool_error(string template)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        var before = await rig.Sqlite.ListAsync(Session, default);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rig.InvokeAsync(template.Replace("ID", id, StringComparison.Ordinal), ct: new CancellationToken(canceled: true)));

        Assert.Equal(before, await rig.Sqlite.ListAsync(Session, default));
        Assert.Null(await rig.Sqlite.GetPlanSummaryAsync(Session, default));
        Assert.Empty(rig.Committer.Items);
    }

    // ---------------------------------------------------------------- sessions and ids

    [Fact]
    public async Task An_item_of_another_session_is_rejected_without_touching_it_or_calling_the_committer()
    {
        await using var rig = new Rig();
        var foreign = await rig.AddAsync("Foreign", session: "sess-b");
        var before = await rig.GetAsync(foreign, "sess-b");

        var update = await rig.InvokeAsync(UpdateJson(foreign, "\"status\":\"done\"", "\"summary\":\"stolen\""));
        var remove = await rig.InvokeAsync($$"""{"operation":"remove","id":"{{foreign}}"}""");

        Assert.True(update.IsError);
        Assert.Contains(foreign, update.Content, StringComparison.Ordinal);
        Assert.Contains("in this session", update.Content, StringComparison.Ordinal);
        Assert.True(remove.IsError);
        Assert.Equal(before, await rig.GetAsync(foreign, "sess-b"));
        Assert.Empty(rig.Committer.Items);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("../escape")]
    [InlineData("has space")]
    public async Task An_unknown_or_unsafe_item_id_is_a_tool_error_and_never_reaches_the_committer(string id)
    {
        await using var rig = new Rig();
        await rig.AddAsync();

        var result = await rig.InvokeAsync(UpdateJson(id, "\"status\":\"done\""));

        Assert.True(result.IsError);
        Assert.Contains("in this session", result.Content, StringComparison.Ordinal);
        Assert.Empty(rig.Committer.Items);
    }

    // ---------------------------------------------------------------- plan summary

    [Theory]
    [InlineData("""{"operation":"plan_summary_set"}""")]
    [InlineData("""{"operation":"plan_summary_set","text":""}""")]
    [InlineData("""{"operation":"plan_summary_set","text":"   "}""")]
    [InlineData("""{"operation":"plan_summary_set","text":"\n\t "}""")]
    [InlineData("""{"operation":"plan_summary_set","text":null}""")]
    [InlineData("""{"operation":"plan_summary_set","text":5}""")]
    public async Task Plan_summary_set_without_real_text_is_rejected_and_keeps_the_stored_summary(string json)
    {
        await using var rig = new Rig();
        await rig.Sqlite.SetPlanSummaryAsync(Session, "kept", default);

        var result = await rig.InvokeAsync(json);

        Assert.True(result.IsError);
        Assert.Contains("non-empty 'text'", result.Content, StringComparison.Ordinal);
        Assert.Equal("kept", (await rig.Sqlite.GetPlanSummaryAsync(Session, default))?.Text);
    }

    [Fact]
    public async Task Plan_summary_is_stored_verbatim_per_session_and_the_confirmation_counts_characters()
    {
        await using var rig = new Rig();
        var text = "\n## Summary\nIt works 汉字.\n\n## Testing\n- unit  \n";

        var saved = await rig.InvokeAsync(JsonSerializer.Serialize(new { operation = "plan_summary_set", text }));
        var read = await rig.InvokeAsync("""{"operation":"plan_summary_get"}""");
        var other = await rig.InvokeAsync("""{"operation":"plan_summary_get"}""", session: "sess-b");
        var replaced = await rig.InvokeAsync("""{"operation":"plan_summary_set","text":"second"}""");
        var readAgain = await rig.InvokeAsync("""{"operation":"plan_summary_get"}""");

        Assert.False(saved.IsError);
        Assert.Equal($"Plan summary saved ({text.Length} characters).", saved.Content);
        Assert.Equal(text, read.Content);
        Assert.False(other.IsError);
        Assert.Equal("No plan summary has been written for this session yet.", other.Content);
        Assert.Equal("Plan summary saved (6 characters).", replaced.Content);
        Assert.Equal("second", readAgain.Content);
    }

    [Fact]
    public async Task Plan_summary_operations_never_touch_the_checklist_or_the_committer()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync();
        var before = await rig.GetAsync(id);

        await rig.InvokeAsync("""{"operation":"plan_summary_set","text":"plan"}""");
        await rig.InvokeAsync("""{"operation":"plan_summary_get"}""");

        Assert.Equal(before, await rig.GetAsync(id));
        Assert.Empty(rig.Committer.Items);
    }

    // ---------------------------------------------------------------- stores that predate the new members

    [Fact]
    public async Task A_store_without_step_support_still_serves_status_updates_and_commits_but_rejects_step_fields()
    {
        var store = new LegacyStore();
        var committer = new ScriptedLegacyCommitter();
        var tool = new TodoTool(store, committer);
        var context = new ToolContext(Session, Path.GetTempPath());
        var item = await store.AddAsync(Session, "Step", null, null, default);

        var fields = await tool.InvokeAsync(Call(UpdateJson(item.Id, "\"summary\":\"s\"")), context, CancellationToken.None);
        Assert.True(fields.IsError);
        Assert.Contains("step fields", fields.Content, StringComparison.Ordinal);

        var done = await tool.InvokeAsync(Call(UpdateJson(item.Id, "\"status\":\"done\"")), context, CancellationToken.None);
        Assert.False(done.IsError);
        Assert.Contains("Step commit: committed abc1234", done.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(committer.Items).Status);

        var get = await tool.InvokeAsync(Call("""{"operation":"plan_summary_get"}"""), context, CancellationToken.None);
        Assert.False(get.IsError);
        Assert.Contains("No plan summary", get.Content, StringComparison.Ordinal);
        var set = await tool.InvokeAsync(Call("""{"operation":"plan_summary_set","text":"plan"}"""), context, CancellationToken.None);
        Assert.True(set.IsError);
        Assert.Contains("plan summaries", set.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_without_step_support_cannot_record_a_failure_but_the_tool_still_reports_it()
    {
        var store = new LegacyStore();
        var tool = new TodoTool(store, new ThrowingLegacyCommitter());
        var item = await store.AddAsync(Session, "Step", null, null, default);

        var result = await tool.InvokeAsync(
            Call(UpdateJson(item.Id, "\"status\":\"done\"")),
            new ToolContext(Session, Path.GetTempPath()),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Step commit: FAILED: legacy boom", result.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync(Session, default)).Status);
    }

    private sealed class ScriptedLegacyCommitter : IStepCommitter
    {
        public List<TodoItem> Items { get; } = [];

        public Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Items.Add(item);
            return Task.FromResult(new StepCommitResult(StepCommitOutcome.Committed, "abc1234def", "ok"));
        }
    }

    private sealed class ThrowingLegacyCommitter : IStepCommitter
    {
        public Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct) =>
            throw new InvalidOperationException("legacy boom");
    }

    // ---------------------------------------------------------------- list rendering

    [Fact]
    public async Task List_renders_status_note_commit_state_and_step_fields_in_the_documented_layout()
    {
        await using var rig = new Rig();
        var done = await rig.AddAsync("Done step");
        var running = await rig.AddAsync("Running step");
        var pending = await rig.AddAsync("Pending step");
        await rig.Sqlite.UpdateAsync(done, null, TodoStatus.Done, "a note", new TodoStepFields("feat: done", "what changed", "ran the tests"), default);
        await rig.Sqlite.SetCommitResultAsync(done, "abcdef0123456789", null, default);
        await rig.Sqlite.UpdateAsync(running, null, TodoStatus.InProgress, null, new TodoStepFields(Summary: "partial"), default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.False(list.IsError);
        Assert.Equal(
            "3 items:\n"
            + $"1. [x] #{done} Done step — a note [commit abcdef0]\n"
            + "   commit_subject: feat: done\n"
            + "   summary: what changed\n"
            + "   verification: ran the tests\n"
            + $"2. [~] #{running} Running step\n"
            + "   summary: partial\n"
            + $"3. [ ] #{pending} Pending step",
            list.Content);
    }

    [Fact]
    public async Task List_uses_the_singular_header_and_an_explicit_empty_message()
    {
        await using var rig = new Rig();
        var empty = await rig.InvokeAsync("""{"operation":"list"}""");
        var id = await rig.AddAsync("Only");
        var one = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal("The checklist is empty.", empty.Content);
        Assert.Equal($"1 item:\n1. [ ] #{id} Only", one.Content);
    }

    [Theory]
    [InlineData("abcdef0123456789", "[commit abcdef0]")]
    [InlineData("abcdefg", "[commit abcdefg]")]
    [InlineData("abc", "[commit abc]")]
    public async Task List_shortens_the_commit_id_to_seven_characters(string commitId, string expected)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, TodoStatus.Done, null, default);
        await rig.Sqlite.SetCommitResultAsync(id, commitId, null, default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal($"1 item:\n1. [x] #{id} Step {expected}", list.Content);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(120, false)]
    [InlineData(121, true)]
    [InlineData(500, true)]
    public async Task List_cuts_a_step_field_at_120_characters_and_marks_the_cut(int length, bool cut)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, null, null, new TodoStepFields(Summary: new string('x', length)), default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal("   summary: " + new string('x', Math.Min(length, 120)) + (cut ? "…" : string.Empty), LineStartingWith(list.Content, "   summary: "));
    }

    [Theory]
    [MemberData(nameof(CompactionCases))]
    public async Task List_collapses_whitespace_runs_and_cuts_on_character_boundaries(string stored, string expectedLine)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, null, null, new TodoStepFields(Verification: stored), default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal("   verification: " + expectedLine, LineStartingWith(list.Content, "   verification: "));
    }

    public static TheoryData<string, string> CompactionCases() => new()
    {
        { "a   b\n\n c\t d  ", "a b c d" },
        { "  \n lead", "lead" },
        { "line one\r\nline two", "line one line two" },
        { new string('x', 119) + " y", new string('x', 119) + "…" },
        { new string('x', 118) + " y", new string('x', 118) + " y" },
        { new string('x', 119) + "😀", new string('x', 119) + "…" },
        { new string('x', 118) + "😀", new string('x', 118) + "😀" },
        { new string('x', 120) + "😀", new string('x', 120) + "…" },
    };

    [Fact]
    public async Task List_omits_step_fields_that_are_blank()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, null, null, new TodoStepFields("   ", " \t\n", "real"), default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.DoesNotContain("commit_subject:", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("summary:", list.Content, StringComparison.Ordinal);
        Assert.Contains("   verification: real", list.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public async Task List_cuts_a_commit_error_at_200_characters(int length, bool cut)
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, TodoStatus.Done, null, default);
        await rig.Sqlite.SetCommitResultAsync(id, null, new string('e', length), default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal($"1 item:\n1. [x] #{id} Step [commit failed: {new string('e', 200)}{(cut ? "…" : string.Empty)}]", list.Content);
    }

    [Fact]
    public async Task List_shows_a_multi_line_commit_error_on_one_line()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");
        await rig.Sqlite.UpdateAsync(id, null, TodoStatus.Done, "note", default);
        await rig.Sqlite.SetCommitResultAsync(id, null, "hook failed:\n  lint error\r\n  see log", default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.Equal($"1 item:\n1. [x] #{id} Step — note [commit failed: hook failed: lint error see log]", list.Content);
    }

    [Fact]
    public async Task The_update_result_shows_only_the_item_line_never_the_step_fields_or_commit_details()
    {
        await using var rig = new Rig();
        var id = await rig.AddAsync("Step");

        var result = await rig.InvokeAsync(UpdateJson(id, "\"note\":\"n\"", "\"summary\":\"long summary\"", "\"verification\":\"proof\""));

        Assert.Equal($"Updated #{id}: [ ] #{id} Step — n", result.Content);
    }

    // ---------------------------------------------------------------- the advertised surface

    [Fact]
    public void The_schema_enumerates_every_operation_and_status_and_types_the_new_properties_as_strings()
    {
        var tool = new TodoTool(new LegacyStore());
        using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
        var root = schema.RootElement;
        var properties = root.GetProperty("properties");

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["operation"], root.GetProperty("required").EnumerateArray().Select(element => element.GetString()!).ToArray());
        Assert.Equal(
            ["list", "add", "update", "remove", "plan_summary_get", "plan_summary_set"],
            properties.GetProperty("operation").GetProperty("enum").EnumerateArray().Select(element => element.GetString()!).ToArray());
        Assert.Equal(
            ["pending", "in_progress", "done"],
            properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(element => element.GetString()!).ToArray());
        foreach (var name in new[] { "id", "title", "status", "note", "commit_subject", "summary", "verification", "text", "after_id" })
        {
            Assert.Equal("string", properties.GetProperty(name).GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(properties.GetProperty(name).GetProperty("description").GetString()));
        }

        foreach (var operation in new[] { "list", "add", "update", "remove", "plan_summary_get", "plan_summary_set" })
        {
            Assert.Contains(operation, tool.Definition.Description, StringComparison.Ordinal);
        }

        Assert.Equal("todo", tool.Definition.Name);
    }
}
