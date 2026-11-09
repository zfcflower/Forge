using Avalonia.Headless;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins <see cref="TodoListViewModel"/>'s session wiring and the shape of what it shows: change events for another session
/// are ignored, attaching a new session re-subscribes (and detaches the old one), the stored items are split into the
/// unfinished groups, the loose queue and the done section, a group's status follows its steps, queueing an item reaches the
/// store as a user-added loose item, and delete/reorder reach the <see cref="ITodoStore"/>. A fake store keeps the tests
/// independent of any database.
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

        await RefreshAfterAttachAsync(session, panel, "s1");
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

        var titles = await session.Dispatch(() => panel.Loose.Select(static row => row.Title).ToList(), CancellationToken.None);
        Assert.Equal(["first"], titles);
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
        var titles = await session.Dispatch(() => panel.Loose.Select(static row => row.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-one"], titles);

        await RefreshAfterAttachAsync(session, panel, "s2");
        titles = await session.Dispatch(() => panel.Loose.Select(static row => row.Title).ToList(), CancellationToken.None);
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
        titles = await session.Dispatch(() => panel.Loose.Select(static row => row.Title).ToList(), CancellationToken.None);
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
        titles = await session.Dispatch(() => panel.Loose.Select(static row => row.Title).ToList(), CancellationToken.None);
        Assert.Equal(["from-two", "fresh"], titles);
    }

    [Fact]
    public async Task Queueing_an_item_reaches_the_store_as_a_user_added_loose_row()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var status = await session.Dispatch(() => (panel.StatusText, panel.HasStatus, panel.IsEmpty), CancellationToken.None);
        Assert.Equal(Loc.Instance[InspectorStrings.TodoEmpty], status.StatusText);
        Assert.True(status.HasStatus);
        Assert.True(status.IsEmpty);

        await session.Dispatch(
            () =>
            {
                panel.NewTitle = "check the spec tree";
                Assert.True(panel.AddCommand.CanExecute(null));
                panel.AddCommand.Execute(null);
                return 0;
            },
            CancellationToken.None);
        await store.Added.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // The panel queues a loose item the user added; the agent promotes it into a group later.
        Assert.Equal(TodoOrigin.User, store.LastAdd?.Origin);
        Assert.Null(store.LastAdd?.GroupId);
        Assert.Equal("check the spec tree", store.LastAdd?.Title);
        Assert.Equal(string.Empty, await session.Dispatch(() => panel.NewTitle, CancellationToken.None));

        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var added = await session.Dispatch(() => panel.Loose.Select(static row => (row.Title, row.IsUserAdded, row.CanDrag)).ToList(), CancellationToken.None);
        var row = Assert.Single(added);
        Assert.Equal("check the spec tree", row.Title);
        Assert.True(row.IsUserAdded);
        Assert.True(row.CanDrag);

        // With a row in the panel there is no status line any more (no summary, no counts).
        status = await session.Dispatch(() => (panel.StatusText, panel.HasStatus, panel.IsEmpty), CancellationToken.None);
        Assert.Equal(string.Empty, status.StatusText);
        Assert.False(status.HasStatus);
        Assert.False(status.IsEmpty);
    }

    [Fact]
    public async Task Delete_and_move_reach_the_store()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.Seed(new TodoItem("m1", "s1", "first", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow));
        store.Seed(new TodoItem("m2", "s1", "second", TodoStatus.Pending, null, 1, DateTimeOffset.UtcNow));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        // A drag inside the loose queue: the source lands on the target's position.
        await panel.MoveAsync("m1", 1, CancellationToken.None);
        Assert.Equal(("m1", 1), store.LastMove);
        Assert.Equal(["m2", "m1"], store.LayerOf("s1", null).Select(static item => item.Id).ToArray());
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var order = await session.Dispatch(() => panel.Loose.Select(static row => row.Id).ToList(), CancellationToken.None);
        Assert.Equal(["m2", "m1"], order);

        // Deleting a row removes it from the store and from the panel.
        await session.Dispatch(
            () =>
            {
                panel.Loose[0].RemoveCommand.Execute(null);
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
        var left = await session.Dispatch(() => panel.Loose.Select(static row => row.Id).ToList(), CancellationToken.None);
        Assert.Equal(["m1"], left);
        Assert.Single(store.LayerOf("s1", null));
    }

    [Fact]
    public async Task A_group_shows_its_steps_in_order_with_its_progress()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.SeedGroup(new TodoGroup("g1", "s1", "Fix the layout", 0, DateTimeOffset.UtcNow));
        store.Seed(new TodoItem("t1", "s1", "done step", TodoStatus.Done, null, 0, DateTimeOffset.UtcNow, GroupId: "g1"));
        store.Seed(new TodoItem("t2", "s1", "running step", TodoStatus.InProgress, null, 1, DateTimeOffset.UtcNow, GroupId: "g1"));
        store.Seed(new TodoItem("t3", "s1", "waiting step", TodoStatus.Pending, null, 2, DateTimeOffset.UtcNow, GroupId: "g1"));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var shown = await session.Dispatch(
            () =>
            {
                var group = Assert.Single(panel.Groups);
                return (group.Title, group.Progress, group.Status, Steps: group.Entries.Select(static entry => entry.Id).ToList(), group.IsExpanded, Done: panel.HasDone, Loose: panel.Loose.Count);
            },
            CancellationToken.None);

        Assert.Equal("Fix the layout", shown.Title);
        Assert.Equal("1/3", shown.Progress);
        Assert.Equal(TodoStatus.InProgress, shown.Status);
        Assert.Equal(["t1", "t2", "t3"], shown.Steps);
        Assert.False(shown.IsExpanded);
        Assert.False(shown.Done);
        Assert.Equal(0, shown.Loose);
    }

    [Theory]
    [InlineData(new[] { TodoStatus.Pending, TodoStatus.Paused }, TodoStatus.Paused)]
    [InlineData(new[] { TodoStatus.Paused, TodoStatus.InProgress, TodoStatus.Pending }, TodoStatus.InProgress)]
    [InlineData(new[] { TodoStatus.Done, TodoStatus.Done }, TodoStatus.Done)]
    public async Task A_group_status_follows_its_steps(TodoStatus[] steps, TodoStatus expected)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.SeedGroup(new TodoGroup("g1", "s1", "Plan", 0, DateTimeOffset.UtcNow));
        for (var i = 0; i < steps.Length; i++)
        {
            store.Seed(new TodoItem("t" + i, "s1", "step " + i, steps[i], null, i, DateTimeOffset.UtcNow, GroupId: "g1"));
        }

        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var status = await session.Dispatch(
            () => panel.Groups.Concat(panel.DoneGroups).Single().Status,
            CancellationToken.None);
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task An_empty_group_is_pending()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.SeedGroup(new TodoGroup("g1", "s1", "Nothing planned yet", 0, DateTimeOffset.UtcNow));

        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var shown = await session.Dispatch(
            () =>
            {
                var group = Assert.Single(panel.Groups);
                return (group.Status, group.Progress, group.TotalCount, Done: panel.HasDone);
            },
            CancellationToken.None);

        Assert.Equal(TodoStatus.Pending, shown.Status);
        Assert.Equal("0/0", shown.Progress);
        Assert.Equal(0, shown.TotalCount);
        Assert.False(shown.Done);
    }

    [Fact]
    public async Task A_finished_group_folds_into_the_done_section_and_opens_on_demand()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.SeedGroup(new TodoGroup("g1", "s1", "Finished plan", 0, DateTimeOffset.UtcNow));
        store.Seed(new TodoItem("t1", "s1", "step one", TodoStatus.Done, null, 0, DateTimeOffset.UtcNow, GroupId: "g1", CommitId: "0123456789abcdef"));
        store.Seed(new TodoItem("t2", "s1", "step two", TodoStatus.Done, null, 1, DateTimeOffset.UtcNow, GroupId: "g1"));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var shown = await session.Dispatch(
            () =>
            {
                var group = Assert.Single(panel.DoneGroups);
                return (Empty: panel.Groups.Count, Name: group.Title, group.Status, group.IsExpanded, Steps: group.StepsText, Progress: group.Progress, Done: panel.HasDone, Rows: group.Entries.Count);
            },
            CancellationToken.None);

        Assert.Equal(0, shown.Empty);
        Assert.Equal(TodoStatus.Done, shown.Status);
        Assert.False(shown.IsExpanded);
        Assert.Equal(2, shown.Rows);
        Assert.Equal(InspectorStrings.TodoGroupSteps(2).Resolve(), shown.Steps);
        Assert.Equal("2/2", shown.Progress);
        Assert.True(shown.Done);

        // The fold row is the only control of a finished group; it opens and closes its own steps.
        var expanded = await session.Dispatch(
            () =>
            {
                var group = panel.DoneGroups[0];
                group.ToggleCommand.Execute(null);
                var opened = group.IsExpanded;
                group.ToggleCommand.Execute(null);
                return (Opened: opened, Closed: group.IsExpanded);
            },
            CancellationToken.None);
        Assert.Equal((true, false), expanded);
    }

    [Fact]
    public async Task Finished_loose_items_move_to_the_done_section_read_only()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        store.Seed(new TodoItem("p", "s1", "queued", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow, Origin: TodoOrigin.User));
        store.Seed(new TodoItem("d", "s1", "queued too", TodoStatus.Done, null, 1, DateTimeOffset.UtcNow));
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);
        await RefreshAfterAttachAsync(session, panel, "s1");

        var shown = await session.Dispatch(
            () => (
                Queue: panel.Loose.Select(static row => (row.Id, row.IsReadOnly, row.IsUserAdded)).ToList(),
                Done: panel.DoneLoose.Select(static row => (row.Id, row.IsReadOnly, row.CanDrag)).ToList(),
                panel.HasDone),
            CancellationToken.None);

        Assert.Equal(("p", false, true), Assert.Single(shown.Queue));
        Assert.Equal(("d", true, false), Assert.Single(shown.Done));
        Assert.True(shown.HasDone);
    }

    [Fact]
    public async Task The_status_line_reports_a_missing_session_and_a_failing_store()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var store = new FakeTodoStore();
        var panel = await session.Dispatch(() => new TodoListViewModel(store), CancellationToken.None);

        var detached = await session.Dispatch(() => (panel.StatusText, panel.HasStatus), CancellationToken.None);
        Assert.Equal(Loc.Instance[InspectorStrings.TodoNoSession], detached.StatusText);
        Assert.True(detached.HasStatus);

        await session.Dispatch(
            async () =>
            {
                panel.Attach("s1");
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        Assert.Equal(Loc.Instance[InspectorStrings.TodoEmpty], await session.Dispatch(() => panel.StatusText, CancellationToken.None));

        store.ListFailure = new InvalidOperationException("the checklist is unreadable");
        await session.Dispatch(
            async () =>
            {
                await panel.RefreshAsync(CancellationToken.None);
                return 0;
            },
            CancellationToken.None);
        var failed = await session.Dispatch(() => (panel.StatusText, panel.HasStatus), CancellationToken.None);
        Assert.Equal("the checklist is unreadable", failed.StatusText);
        Assert.True(failed.HasStatus);
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
        private readonly Dictionary<string, TodoGroup> groups = new(StringComparer.Ordinal);

        public event Action<string>? Changed;

        public int ListCalls;

        public Exception? ListFailure { get; set; }

        public TaskCompletionSource<object?> Added { get; private set; } = NewTcs();

        public TaskCompletionSource<object?> Removed { get; private set; } = NewTcs();

        public TaskCompletionSource<object?> Moved { get; private set; } = NewTcs();

        public TodoAddRequest? LastAdd { get; private set; }

        public (string Id, int Order)? LastMove { get; private set; }

        public void Seed(TodoItem item) => items[item.Id] = item;

        public void SeedGroup(TodoGroup group) => groups[group.Id] = group;

        public void Emit(string sessionId) => Changed?.Invoke(sessionId);

        /// <summary>The items of one layer (the loose queue for a null group, or a group), in their own order.</summary>
        public IReadOnlyList<TodoItem> LayerOf(string sessionId, string? groupId) =>
            items.Values
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) &&
                               string.Equals(item.GroupId, groupId, StringComparison.Ordinal))
                .OrderBy(static item => item.Order)
                .ToList();

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct)
        {
            ListCalls++;
            if (ListFailure is { } failure)
            {
                return Task.FromException<IReadOnlyList<TodoItem>>(failure);
            }

            IReadOnlyList<TodoItem> stored = items.Values
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(static item => item.GroupId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Order)
                .ToList();
            return Task.FromResult(stored);
        }

        public Task<IReadOnlyList<TodoGroup>> ListGroupsAsync(string sessionId, CancellationToken ct)
        {
            IReadOnlyList<TodoGroup> stored = groups.Values
                .Where(group => string.Equals(group.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(static group => group.Order)
                .ToList();
            return Task.FromResult(stored);
        }

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            AddAsync(new TodoAddRequest(sessionId, title, note, afterId), ct);

        public Task<TodoItem> AddAsync(TodoAddRequest request, CancellationToken ct)
        {
            LastAdd = request;
            var item = new TodoItem(
                Guid.NewGuid().ToString("n"),
                request.SessionId,
                request.Title,
                TodoStatus.Pending,
                request.Note,
                LayerOf(request.SessionId, request.GroupId).Count,
                DateTimeOffset.UtcNow,
                GroupId: request.GroupId,
                Origin: request.Origin);
            items[item.Id] = item;
            Added.TrySetResult(null);
            Changed?.Invoke(item.SessionId);
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

            LastMove = (id, order);
            var layer = LayerOf(current.SessionId, current.GroupId).Where(item => item.Id != id).ToList();
            layer.Insert(Math.Clamp(order, 0, layer.Count), current);
            for (var i = 0; i < layer.Count; i++)
            {
                items[layer[i].Id] = layer[i] with { Order = i };
            }

            Moved.TrySetResult(null);
            Changed?.Invoke(current.SessionId);
            return Task.FromResult(true);
        }

        private static TaskCompletionSource<object?> NewTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
