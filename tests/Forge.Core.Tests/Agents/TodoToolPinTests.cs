using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>Pins the session checklist tool: per-session scoping and per-call side effects.</summary>
public sealed class TodoToolPinTests
{
    private sealed class FakeTodoStore : ITodoStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, TodoItem> items = new(StringComparer.Ordinal);
        private int order;

        public event Action<string>? Changed;

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<TodoItem>>(
                    items.Values.Where(i => i.SessionId == sessionId).OrderBy(i => i.Order).ToArray());
            }
        }

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct)
        {
            lock (gate)
            {
                if (afterId is not null && (!items.TryGetValue(afterId, out var anchor) || anchor.SessionId != sessionId))
                {
                    throw new ArgumentException($"Unknown item '{afterId}'.", nameof(afterId));
                }

                var item = new TodoItem(Guid.NewGuid().ToString("n"), sessionId, title, TodoStatus.Pending, note, ++order, DateTimeOffset.UtcNow);
                items[item.Id] = item;
                Changed?.Invoke(sessionId);
                return Task.FromResult(item);
            }
        }

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct)
        {
            lock (gate)
            {
                if (!items.TryGetValue(id, out var item))
                {
                    return Task.FromResult<TodoItem?>(null);
                }

                var updated = item with
                {
                    Title = title ?? item.Title,
                    Status = status ?? item.Status,
                    Note = note ?? item.Note,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                items[id] = updated;
                Changed?.Invoke(item.SessionId);
                return Task.FromResult<TodoItem?>(updated);
            }
        }

        public Task<bool> RemoveAsync(string id, CancellationToken ct)
        {
            lock (gate)
            {
                if (!items.TryGetValue(id, out var item))
                {
                    return Task.FromResult(false);
                }

                items.Remove(id);
                Changed?.Invoke(item.SessionId);
                return Task.FromResult(true);
            }
        }

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct)
        {
            lock (gate)
            {
                if (!items.TryGetValue(id, out var item))
                {
                    return Task.FromResult(false);
                }

                items[id] = item with { Order = order };
                Changed?.Invoke(item.SessionId);
                return Task.FromResult(true);
            }
        }
    }

    private static ToolContext Context(string sessionId) => new(sessionId, Path.GetTempPath());

    private static ToolCall Call(string operation, string extra = "") =>
        new("call-1", "todo", "{\"operation\":\"" + operation + "\"" + extra + "}");

    [Fact]
    public async Task List_is_scoped_to_the_calling_session()
    {
        var store = new FakeTodoStore();
        var tool = new TodoTool(store);

        var added = await tool.InvokeAsync(Call("add", ",\"title\":\"Task A\""), Context("sess-a"), CancellationToken.None);
        Assert.False(added.IsError);

        var same = await tool.InvokeAsync(Call("list"), Context("sess-a"), CancellationToken.None);
        Assert.Contains("Task A", same.Content, StringComparison.Ordinal);

        var other = await tool.InvokeAsync(Call("list"), Context("sess-b"), CancellationToken.None);
        Assert.Contains("empty", other.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_is_scoped_to_the_calling_session()
    {
        var store = new FakeTodoStore();
        var tool = new TodoTool(store);

        await tool.InvokeAsync(Call("add", ",\"title\":\"Task A\""), Context("sess-a"), CancellationToken.None);
        await tool.InvokeAsync(Call("add", ",\"title\":\"Task B\""), Context("sess-b"), CancellationToken.None);

        Assert.Single(await store.ListAsync("sess-a", CancellationToken.None));
        Assert.Single(await store.ListAsync("sess-b", CancellationToken.None));
        Assert.Equal("Task A", (await store.ListAsync("sess-a", CancellationToken.None)).Single().Title);
        Assert.Equal("Task B", (await store.ListAsync("sess-b", CancellationToken.None)).Single().Title);
    }

    [Fact]
    public async Task List_is_ReadOnly_while_mutations_are_Write()
    {
        var tool = new TodoTool(new FakeTodoStore());

        Assert.Equal(ToolSideEffect.ReadOnly, ToolRegistry.SideEffectOf(tool, Call("list")));
        Assert.Equal(ToolSideEffect.Write, tool.SideEffect);
        Assert.Equal(ToolSideEffect.Write, ToolRegistry.SideEffectOf(tool, Call("add", ",\"title\":\"x\"" )));
        Assert.Equal(ToolSideEffect.Write, ToolRegistry.SideEffectOf(tool, Call("update", ",\"id\":\"x\",\"title\":\"y\"" )));
        Assert.Equal(ToolSideEffect.Write, ToolRegistry.SideEffectOf(tool, Call("remove", ",\"id\":\"x\"" )));
    }

    [Fact]
    public async Task Approval_policy_sees_the_per_call_effect()
    {
        var policy = new DefaultApprovalPolicy();
        var tool = new TodoTool(new FakeTodoStore());

        var list = new ApprovalRequest("todo", """{"operation":"list"}""", ToolRegistry.SideEffectOf(tool, Call("list")), Path.GetTempPath());
        var add = new ApprovalRequest("todo", """{"operation":"add","title":"x"}""", ToolRegistry.SideEffectOf(tool, Call("add", ",\"title\":\"x\"" )), Path.GetTempPath());

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(list));
        // A write without trusted directories or a handler asks the user.
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(add));
    }

    [Fact]
    public async Task Update_remove_round_trip_through_the_tool()
    {
        var store = new FakeTodoStore();
        var tool = new TodoTool(store);

        var added = await tool.InvokeAsync(Call("add", ",\"title\":\"Write tests\",\"note\":\"fast\""), Context("sess-a"), CancellationToken.None);
        Assert.False(added.IsError);
        var id = (await store.ListAsync("sess-a", CancellationToken.None)).Single().Id;

        var updated = await tool.InvokeAsync(
            Call("update", $"\"id\":\"{id}\",\"status\":\"in_progress\"".Insert(0, ",")),
            Context("sess-a"),
            CancellationToken.None);
        Assert.False(updated.IsError);
        Assert.Contains("[~]", updated.Content, StringComparison.Ordinal);
        Assert.Contains(id, updated.Content, StringComparison.Ordinal);

        var removed = await tool.InvokeAsync(Call("remove", $"\"id\":\"{id}\"".Insert(0, ",")), Context("sess-a"), CancellationToken.None);
        Assert.False(removed.IsError);
        Assert.Empty(await store.ListAsync("sess-a", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_item_ids_are_reported_as_errors()
    {
        var tool = new TodoTool(new FakeTodoStore());

        var updated = await tool.InvokeAsync(Call("update", ",\"id\":\"missing\",\"title\":\"x\""), Context("sess-a"), CancellationToken.None);
        Assert.True(updated.IsError);
        Assert.Contains("missing", updated.Content, StringComparison.Ordinal);

        var removed = await tool.InvokeAsync(Call("remove", ",\"id\":\"missing\""), Context("sess-a"), CancellationToken.None);
        Assert.True(removed.IsError);
    }

    [Fact]
    public async Task Update_remove_reject_other_sessions_items()
    {
        var store = new FakeTodoStore();
        var tool = new TodoTool(store);

        await tool.InvokeAsync(Call("add", ",\"title\":\"Task A\""), Context("sess-a"), CancellationToken.None);
        var id = (await store.ListAsync("sess-a", CancellationToken.None)).Single().Id;

        var updated = await tool.InvokeAsync(Call("update", $"\"id\":\"{id}\",\"title\":\"hijacked\"".Insert(0, ",")), Context("sess-b"), CancellationToken.None);
        Assert.True(updated.IsError);

        var removed = await tool.InvokeAsync(Call("remove", $"\"id\":\"{id}\"".Insert(0, ",")), Context("sess-b"), CancellationToken.None);
        Assert.True(removed.IsError);
        Assert.Single(await store.ListAsync("sess-a", CancellationToken.None));
    }
}
