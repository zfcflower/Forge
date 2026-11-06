using Avalonia.Headless;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins <see cref="TodoListViewModel"/> session wiring: change events for another session are ignored,
/// attaching a new session re-subscribes (and detaches the old one), and add/check/remove/move reach the
/// <see cref="ITodoStore"/>. A fake store keeps the tests independent of any database.
///
/// Headless note: the panel's fire-and-forget refreshes (<c>Attach</c>, the <c>Changed</c> post) do not
/// reliably drain on a headless UI loop, so every step below follows the trigger with an explicit,
/// awaited <c>RefreshAsync</c> on the dispatcher instead of polling for the background refresh.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TodoPanelTests
{
    [Fact]
    public async Task Changed_for_another_session_is_ignored()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.Seed(new TodoItem("t1", "s1", "first", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);

        await session.Dispatch(
            async () =>
            {
                panel.Attach("s1");
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var callsBefore = store.ListCalls;

        // Pump the dispatcher while the foreign event is in flight: a buggy handler would post a
        // refresh here whose store read we would then observe.
        await session.Dispatch(
            async () =>
            {
                store.Emit("s2");
                for (var i = 0; i < 20; i++)
                {
                    await Task.Yield();
                }

                return 0;
            },
            CancellationToken.None);

        var items = await session.Dispatch(() => panel.Items.Select(static item => item.Title).ToList(), CancellationToken.None);
        Assert.Equal(["first"], items);
        Assert.Equal(callsBefore, store.ListCalls);
    }

    [Fact]
    public async Task Attach_to_a_new_session_resubscribes_and_detaches_the_old()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.Seed(new TodoItem("a", "s1", "from-one", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow));
        store.Seed(new TodoItem("b", "s2", "from-two", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);

        await RefreshAfterAttachAsync(session, panel, "s1");
        var titles = await session.Dispatch(() => panel.Items.Select(static item => item.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-one"], titles);

        await RefreshAfterAttachAsync(session, panel, "s2");
        titles = await session.Dispatch(() => panel.Items.Select(static item => item.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-two"], titles);

        // Events from the old session no longer refresh the panel.
        await session.Dispatch(
            async () =>
            {
                store.Emit("s1");
                Dispatcher.UIThread.RunJobs();
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        titles = await session.Dispatch(() => panel.Items.Select(static item => item.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-two"], titles);

        // Events from the current session do. The posted refresh is run before the explicit one starts, so the explicit
        // refresh is the newest generation: its result is the one the panel keeps, whatever the background reads do.
        await session.Dispatch(
            async () =>
            {
                store.Seed(new TodoItem("c", "s2", "fresh", TodoStatus.Pending, null, 1, DateTimeOffset.UtcNow));
                store.Emit("s2");
                Dispatcher.UIThread.RunJobs();
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        titles = await session.Dispatch(() => panel.Items.Select(static item => item.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-two", "fresh"], titles);
    }

    [Fact]
    public async Task Add_check_remove_move_reach_the_store()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");
        var summary = await session.Dispatch(() => panel.Summary, CancellationToken.None);
        Assert.Equal(Loc.Instance[InspectorStrings.TodoEmpty], summary);

        // Add through the panel reaches the store and shows up after a refresh.
        await session.Dispatch(
            () =>
            {
                panel.NewTitle = "write tests";
                Assert.True(panel.AddCommand.CanExecute(null));
                panel.AddCommand.Execute(null);
                return 0;
            },
            CancellationToken.None);
        await store.Added.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var added = Assert.Single(await session.Dispatch(() => panel.Items.Select(static item => (item.Id, item.Title)).ToList(), CancellationToken.None));
        Assert.Equal("write tests", added.Title);
        Assert.Single(store.ItemsOf("s1"));

        // Checking the entry updates the store.
        store.ResetUpdate();
        await session.Dispatch(
            () =>
            {
                panel.Items[0].IsDone = true;
                return 0;
            },
            CancellationToken.None);
        await store.Updated.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(TodoStatus.Done, store.ItemsOf("s1").Single().Status);
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        summary = await session.Dispatch(() => panel.Summary, CancellationToken.None);
        Assert.Equal(InspectorStrings.TodoSummary(1, 1).Resolve(), summary);

        // Removing the entry deletes the row.
        await session.Dispatch(
            () =>
            {
                panel.Items[0].RemoveCommand.Execute(null);
                return 0;
            },
            CancellationToken.None);
        await store.Removed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var empty = await session.Dispatch(() => panel.Items.Count, CancellationToken.None);
        Assert.Equal(0, empty);
        Assert.Empty(store.ItemsOf("s1"));

        // Moving reorders the rows.
        store.Seed(new TodoItem("m1", "s2", "first", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow));
        store.Seed(new TodoItem("m2", "s2", "second", TodoStatus.Pending, null, 1, DateTimeOffset.UtcNow));
        await RefreshAfterAttachAsync(session, panel, "s2");
        var order = await session.Dispatch(() => panel.Items.Select(static item => item.Id).ToList(), CancellationToken.None);
        Assert.Equal(["m1", "m2"], order);
        store.ResetMove();
        await session.Dispatch(
            () =>
            {
                panel.Items[0].MoveDownCommand.Execute(null);
                return 0;
            },
            CancellationToken.None);
        await store.Moved.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(["m2", "m1"], store.ItemsOf("s2").Select(static item => item.Id).ToArray());
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        order = await session.Dispatch(() => panel.Items.Select(static item => item.Id).ToList(), CancellationToken.None);
        Assert.Equal(["m2", "m1"], order);
    }

    private static async Task RefreshAfterAttachAsync(HeadlessUnitTestSession session, TodoListViewModel panel, string sessionId)
    {
        await session.Dispatch(
            async () =>
            {
                panel.Attach(sessionId);
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
    }

    private sealed class FakeTodoStore : ITodoStore
    {
        private readonly Dictionary<string, TodoItem> items = new(StringComparer.Ordinal);

        public event Action<string>? Changed;

        public int ListCalls;

        public TaskCompletionSource<object?> Added { get; private set; } = NewTcs();

        public TaskCompletionSource<object?> Updated { get; private set; } = NewTcs();

        public TaskCompletionSource<object?> Removed { get; private set; } = NewTcs();

        public TaskCompletionSource<object?> Moved { get; private set; } = NewTcs();

        public void Seed(TodoItem item) => items[item.Id] = item;

        public void Emit(string sessionId) => Changed?.Invoke(sessionId);

        public void ResetUpdate() => Updated = NewTcs();

        public void ResetMove() => Moved = NewTcs();

        public IReadOnlyList<TodoItem> ItemsOf(string sessionId) =>
            items.Values.Where(item => item.SessionId == sessionId).OrderBy(static item => item.Order).ToList();

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult(ItemsOf(sessionId));
        }

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct)
        {
            var item = new TodoItem(
                Guid.NewGuid().ToString("n"),
                sessionId,
                title,
                TodoStatus.Pending,
                note,
                items.Values.Count(item => item.SessionId == sessionId),
                DateTimeOffset.UtcNow);
            items[item.Id] = item;
            Added.TrySetResult(null);
            Changed?.Invoke(sessionId);
            return Task.FromResult(item);
        }

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct)
        {
            if (!items.TryGetValue(id, out var current))
            {
                return Task.FromResult<TodoItem?>(null);
            }

            var next = current with
            {
                Title = title ?? current.Title,
                Status = status ?? current.Status,
                Note = note ?? current.Note,
            };
            items[id] = next;
            Updated.TrySetResult(null);
            Changed?.Invoke(next.SessionId);
            return Task.FromResult<TodoItem?>(next);
        }

        public Task<bool> RemoveAsync(string id, CancellationToken ct)
        {
            if (!items.Remove(id, out var removed))
            {
                return Task.FromResult(false);
            }

            Removed.TrySetResult(null);
            Changed?.Invoke(removed.SessionId);
            return Task.FromResult(true);
        }

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct)
        {
            if (!items.TryGetValue(id, out var current))
            {
                return Task.FromResult(false);
            }

            var sessionItems = items.Values
                .Where(item => item.SessionId == current.SessionId)
                .OrderBy(static item => item.Order)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ToList();
            sessionItems.RemoveAll(item => item.Id == id);
            sessionItems.Insert(Math.Clamp(order, 0, sessionItems.Count), current);
            for (var i = 0; i < sessionItems.Count; i++)
            {
                items[sessionItems[i].Id] = sessionItems[i] with { Order = i };
            }

            Moved.TrySetResult(null);
            Changed?.Invoke(current.SessionId);
            return Task.FromResult(true);
        }

        private static TaskCompletionSource<object?> NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
