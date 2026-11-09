using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Core.Tests.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// Pins the v4 groups surface of <see cref="TodoTool"/>: grouped adds, promotion, paused/in-progress semantics and
/// the grouped list rendering. Step-commit edge cases live in <see cref="TodoToolStepCommitEdgeTests"/>.
/// </summary>
public sealed class TodoToolGroupTests
{
    private const string Session = "sess-a";

    private static ToolCall Call(string json) => new("call-1", "todo", json);

    private static ToolContext Context(string session = Session) => new(session, Path.GetTempPath());

    private sealed class RecordingCommitter(ITodoStore store) : IStepCommitter
    {
        public List<TodoItem> Items { get; } = [];

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Items.Add(item);
            var result = new StepCommitResult(StepCommitOutcome.Committed, "abc1234def5678", "committed");
            await store.SetCommitResultAsync(item.Id, result.CommitId, null, ct);
            return result;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();

        public Rig()
        {
            Store = new SqliteTodoStore(directory.Path);
            Committer = new RecordingCommitter(Store);
            Tool = new TodoTool(Store, Committer);
        }

        public SqliteTodoStore Store { get; }

        public RecordingCommitter Committer { get; }

        public TodoTool Tool { get; }

        public ValueTask<ToolResult> InvokeAsync(string json, string session = Session) =>
            Tool.InvokeAsync(Call(json), Context(session), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            directory.Dispose();
        }
    }

    /// <summary>A store that returns an item whose group is missing, so the list must show it under Loose.</summary>
    private sealed class OrphanStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>(
            [
                new TodoItem("orphan-1", sessionId, "Orphan", TodoStatus.Pending, null, 0, DateTimeOffset.UnixEpoch, GroupId: "missing-group"),
            ]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<TodoGroup>> ListGroupsAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoGroup>>([]);
    }

    [Fact]
    public async Task Add_with_a_group_creates_the_group_and_reuses_it_for_the_same_title()
    {
        await using var rig = new Rig();

        var first = await rig.InvokeAsync("""{"operation":"add","title":"Step one","group":"Plan"}""");
        Assert.False(first.IsError);
        Assert.Contains("to group \"Plan\"", first.Content, StringComparison.Ordinal);
        var group = Assert.Single(await rig.Store.ListGroupsAsync(Session, default));

        var second = await rig.InvokeAsync("""{"operation":"add","title":"Step two","group":"  Plan  "}""");
        Assert.False(second.IsError);
        Assert.Contains($"to group \"Plan\"", second.Content, StringComparison.Ordinal);

        Assert.Single(await rig.Store.ListGroupsAsync(Session, default));
        var steps = await rig.Store.ListAsync(Session, default);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, item => Assert.Equal(group.Id, item.GroupId));
        Assert.Equal("Plan", (await rig.Store.ListGroupsAsync(Session, default)).Single().Title);
    }

    [Fact]
    public async Task Add_without_a_group_lands_in_the_loose_queue()
    {
        await using var rig = new Rig();

        var added = await rig.InvokeAsync("""{"operation":"add","title":"Queued"}""");

        Assert.False(added.IsError);
        Assert.StartsWith("Added #", added.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("to group", added.Content, StringComparison.Ordinal);
        var stored = Assert.Single(await rig.Store.ListAsync(Session, default));
        Assert.Null(stored.GroupId);
        Assert.Empty(await rig.Store.ListGroupsAsync(Session, default));
    }

    [Fact]
    public async Task Promote_turns_a_loose_item_into_an_empty_group_and_the_list_shows_it_with_zero_steps()
    {
        await using var rig = new Rig();
        var loose = await rig.Store.AddAsync(new TodoAddRequest(Session, "Take me"), default);
        var other = await rig.Store.AddAsync(new TodoAddRequest(Session, "Stay"), default);

        var promoted = await rig.InvokeAsync($$"""{"operation":"promote","id":"{{loose.Id}}"}""");

        Assert.False(promoted.IsError);
        Assert.Contains($"Promoted #{loose.Id} into group", promoted.Content, StringComparison.Ordinal);
        Assert.Contains("add its steps with add and group", promoted.Content, StringComparison.Ordinal);
        var group = Assert.Single(await rig.Store.ListGroupsAsync(Session, default));
        Assert.Equal(loose.Id, group.Id);
        Assert.Equal("Take me", group.Title);
        Assert.DoesNotContain(await rig.Store.ListAsync(Session, default), item => item.Id == loose.Id);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");
        Assert.False(list.IsError);
        Assert.Contains($"Group {group.Id} \"Take me\" 0/0", list.Content, StringComparison.Ordinal);
        Assert.Contains("Loose (the queue you may promote):", list.Content, StringComparison.Ordinal);
        Assert.Contains(other.Id, list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Take me\" 1/", list.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promote_with_a_title_override_trims_for_the_new_group()
    {
        await using var rig = new Rig();
        var loose = await rig.Store.AddAsync(new TodoAddRequest(Session, "Raw"), default);

        var promoted = await rig.InvokeAsync($$"""{"operation":"promote","id":"{{loose.Id}}","title":"  Better  "}""");

        Assert.False(promoted.IsError);
        Assert.Contains("into group \"Better\"", promoted.Content, StringComparison.Ordinal);
        Assert.Equal("Better", (await rig.Store.ListGroupsAsync(Session, default)).Single().Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Promote_without_an_id_is_rejected_before_touching_the_store(string? id)
    {
        await using var rig = new Rig();
        await rig.Store.AddAsync(new TodoAddRequest(Session, "Queued"), default);
        var beforeGroups = await rig.Store.ListGroupsAsync(Session, default);
        var beforeItems = await rig.Store.ListAsync(Session, default);

        var json = id is null
            ? """{"operation":"promote"}"""
            : $$"""{"operation":"promote","id":"{{id}}"}""";
        var result = await rig.InvokeAsync(json);

        Assert.True(result.IsError);
        Assert.Contains("'id'", result.Content, StringComparison.Ordinal);
        Assert.Equal(beforeGroups, await rig.Store.ListGroupsAsync(Session, default));
        Assert.Equal(beforeItems, await rig.Store.ListAsync(Session, default));
    }

    [Fact]
    public async Task Promote_of_a_group_step_is_rejected()
    {
        await using var rig = new Rig();
        var group = await rig.Store.FindOrCreateGroupAsync(Session, "Plan", default);
        var step = await rig.Store.AddAsync(new TodoAddRequest(Session, "Step", GroupId: group.Id), default);

        var result = await rig.InvokeAsync($$"""{"operation":"promote","id":"{{step.Id}}"}""");

        Assert.True(result.IsError);
        Assert.Contains("already is a step", result.Content, StringComparison.Ordinal);
        Assert.Single(await rig.Store.ListGroupsAsync(Session, default));
    }

    [Fact]
    public async Task Promote_of_a_missing_id_is_rejected()
    {
        await using var rig = new Rig();
        await rig.Store.AddAsync(new TodoAddRequest(Session, "Queued"), default);

        var result = await rig.InvokeAsync("""{"operation":"promote","id":"does-not-exist"}""");

        Assert.True(result.IsError);
        Assert.Contains("does-not-exist", result.Content, StringComparison.Ordinal);
        Assert.Empty(await rig.Store.ListGroupsAsync(Session, default));
    }

    [Fact]
    public async Task Update_to_paused_succeeds_and_never_runs_the_step_committer()
    {
        await using var rig = new Rig();
        var id = (await rig.Store.AddAsync(new TodoAddRequest(Session, "Waiting"), default)).Id;

        var paused = await rig.InvokeAsync($$"""{"operation":"update","id":"{{id}}","status":"paused"}""");

        Assert.False(paused.IsError);
        Assert.Contains("[-]", paused.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Step commit", paused.Content, StringComparison.Ordinal);
        Assert.Empty(rig.Committer.Items);
        Assert.Equal(TodoStatus.Paused, Assert.Single(await rig.Store.ListAsync(Session, default)).Status);

        var waiting = await rig.InvokeAsync($$"""{"operation":"update","id":"{{id}}","status":"waiting"}""");
        Assert.False(waiting.IsError);
        Assert.Empty(rig.Committer.Items);
        Assert.Equal(TodoStatus.Paused, Assert.Single(await rig.Store.ListAsync(Session, default)).Status);
    }

    [Fact]
    public async Task Setting_in_progress_returns_the_previous_step_to_pending_in_the_store()
    {
        await using var rig = new Rig();
        var first = await rig.Store.AddAsync(new TodoAddRequest(Session, "First"), default);
        var second = await rig.Store.AddAsync(new TodoAddRequest(Session, "Second"), default);

        var started = await rig.InvokeAsync($$"""{"operation":"update","id":"{{first.Id}}","status":"in_progress"}""");
        Assert.False(started.IsError);
        Assert.Empty(rig.Committer.Items);

        var moved = await rig.InvokeAsync($$"""{"operation":"update","id":"{{second.Id}}","status":"in_progress"}""");
        Assert.False(moved.IsError);
        Assert.Empty(rig.Committer.Items);

        var byId = (await rig.Store.ListAsync(Session, default)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        Assert.Equal(TodoStatus.Pending, byId[first.Id].Status);
        Assert.Equal(TodoStatus.InProgress, byId[second.Id].Status);
    }

    [Fact]
    public async Task List_groups_steps_by_group_with_done_counts_and_the_loose_queue_last()
    {
        await using var rig = new Rig();
        var red = await rig.Store.FindOrCreateGroupAsync(Session, "Red", default);
        var blue = await rig.Store.FindOrCreateGroupAsync(Session, "Blue", default);
        var redOne = await rig.Store.AddAsync(new TodoAddRequest(Session, "Red one", GroupId: red.Id), default);
        var redTwo = await rig.Store.AddAsync(new TodoAddRequest(Session, "Red two", GroupId: red.Id), default);
        var blueOne = await rig.Store.AddAsync(new TodoAddRequest(Session, "Blue one", GroupId: blue.Id), default);
        var loose = await rig.Store.AddAsync(new TodoAddRequest(Session, "Loose one", Note: "queue note"), default);
        await rig.Store.UpdateAsync(redOne.Id, null, TodoStatus.Done, null, default);

        var list = await rig.InvokeAsync("""{"operation":"list"}""");

        Assert.False(list.IsError);
        Assert.StartsWith("2 groups, 4 items:\n", list.Content, StringComparison.Ordinal);
        var redHeader = list.Content.IndexOf($"Group {red.Id} \"Red\" 1/2", StringComparison.Ordinal);
        var blueHeader = list.Content.IndexOf($"Group {blue.Id} \"Blue\" 0/1", StringComparison.Ordinal);
        var looseHeader = list.Content.IndexOf("Loose (the queue you may promote):", StringComparison.Ordinal);
        Assert.True(redHeader >= 0 && blueHeader > redHeader && looseHeader > blueHeader);

        Assert.Contains($"  1. [done] {redOne.Id} Red one", list.Content, StringComparison.Ordinal);
        Assert.Contains($"  2. [pending] {redTwo.Id} Red two", list.Content, StringComparison.Ordinal);
        Assert.Contains($"  1. [pending] {blueOne.Id} Blue one", list.Content, StringComparison.Ordinal);
        Assert.Contains($"  1. [pending] {loose.Id} Loose one", list.Content, StringComparison.Ordinal);
        Assert.Contains("     note: queue note", list.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(" — ", list.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_puts_items_whose_group_is_missing_into_the_loose_section()
    {
        var tool = new TodoTool(new OrphanStore());
        var result = await tool.InvokeAsync(Call("""{"operation":"list"}"""), Context(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("0 groups, 1 item:", result.Content, StringComparison.Ordinal);
        Assert.Contains("Loose (the queue you may promote):", result.Content, StringComparison.Ordinal);
        Assert.Contains("  1. [pending] orphan-1 Orphan", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"operation":"list"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"plan_summary_get"}""", ToolSideEffect.ReadOnly)]
    [InlineData("""{"operation":"promote","id":"x"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"add","title":"x","group":"Plan"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"update","id":"x","status":"paused"}""", ToolSideEffect.Write)]
    [InlineData("""{"operation":"remove","id":"x"}""", ToolSideEffect.Write)]
    public void Side_effect_resolves_promote_as_a_write_and_list_as_a_read(string json, ToolSideEffect expected)
    {
        var tool = new TodoTool(new OrphanStore());

        Assert.Equal(expected, ToolRegistry.SideEffectOf(tool, Call(json)));
    }
}
