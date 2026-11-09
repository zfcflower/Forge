using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The TODO panel's half of step commits. The panel no longer starts one: the agent's <c>todo</c> tool owns "done", so a
/// refresh that brings in a finished step only shows what the store recorded - the short sha, or the failure - and never
/// calls the committer. The one commit path left in the panel is "Retry commit": it reads the item again and commits that
/// item only while it is still done. A recording committer isolates the panel; the end-to-end class below puts the real
/// committer, store and git behind the same panel.
///
/// Negative assertions follow a fence (the store write the trigger caused has landed) and a short settle delay: the commit
/// would run right after that write on the same task, so a call that should not happen has had ample time to show up.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TodoStepCommitTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    // ------------------------------------------------------------ the panel does not commit

    [Fact]
    public async Task A_refresh_that_brings_in_a_step_the_agent_finished_shows_it_without_committing()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Agent step");
        await rig.AttachAsync();
        Assert.False(await rig.Ui(() => rig.Row(item.Id).IsDone));

        // The agent's todo tool writes through the store, not through the panel.
        await rig.Store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("fix: agent", "Did it.", null), CancellationToken.None);
        await rig.Store.SetCommitResultAsync(item.Id, Sha, null, CancellationToken.None);
        await rig.RefreshAsync();
        await rig.Ui(() => Dispatcher.UIThread.RunJobs());
        await rig.RefreshAsync();
        await PanelRig.SettleAsync();

        var row = await rig.Ui(() => rig.Row(item.Id));
        Assert.True(await rig.Ui(() => row.IsDone));
        Assert.True(await rig.Ui(() => row.IsReadOnly));
        Assert.Equal(Sha[..7], await rig.Ui(() => row.CommitShort));
        Assert.Empty(rig.Committer.Calls);
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("remove")]
    [InlineData("move")]
    public async Task A_refresh_a_delete_and_a_reorder_never_commit(string action)
    {
        await using var rig = await PanelRig.CreateAsync();
        var first = await rig.AddAsync("first");
        await rig.AddAsync("second");
        await rig.AttachAsync();

        switch (action)
        {
            case "refresh":
                await rig.RefreshAsync();
                break;
            case "remove":
                await rig.Ui(() => rig.Row(first.Id).RemoveCommand.Execute(null));
                await rig.WaitUntilAsync(async () => (await rig.ListAsync()).All(stored => stored.Id != first.Id), "the item to be removed");
                break;
            case "move":
                await rig.Panel.MoveAsync(first.Id, 1, CancellationToken.None);
                await rig.StoredAsync(first.Id, static stored => stored.Order == 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        await rig.RefreshAsync();
        await PanelRig.SettleAsync();
        Assert.Empty(rig.Committer.Calls);
    }

    // ------------------------------------------------------------ what the row shows

    [Fact]
    public async Task A_done_row_with_a_commit_shows_the_short_id_and_no_failure()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Done and committed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, Sha, null, CancellationToken.None);
        await rig.AttachAsync();

        var shown = await rig.Ui(
            () =>
            {
                var row = rig.Row(item.Id);
                return (row.HasCommitId, row.CommitId, row.CommitShort, row.HasCommitError, row.CommitError, Retry: row.RetryCommitCommand.CanExecute(null), row.HasDetailLine);
            });

        Assert.True(shown.HasCommitId);
        Assert.Equal(Sha, shown.CommitId);
        Assert.Equal("0123456", shown.CommitShort);
        Assert.False(shown.HasCommitError);
        Assert.Null(shown.CommitError);
        Assert.False(shown.Retry);
        Assert.True(shown.HasDetailLine);
    }

    [Fact]
    public async Task A_done_row_with_a_failure_shows_it_and_offers_the_retry()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Done but failed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "pre-commit hook failed", CancellationToken.None);
        await rig.AttachAsync();

        var shown = await rig.Ui(
            () =>
            {
                var row = rig.Row(item.Id);
                return (row.HasCommitError, row.CommitError, row.HasCommitId, row.CommitShort, Retry: row.RetryCommitCommand.CanExecute(null));
            });

        Assert.True(shown.HasCommitError);
        Assert.Equal("pre-commit hook failed", shown.CommitError);
        Assert.False(shown.HasCommitId);
        Assert.Equal(string.Empty, shown.CommitShort);
        Assert.True(shown.Retry);
    }

    [Theory]
    [InlineData(TodoStatus.Done, null, null, false, false, "")]
    [InlineData(TodoStatus.Done, "", "", false, false, "")]
    [InlineData(TodoStatus.Done, "", "   ", false, false, "")]
    [InlineData(TodoStatus.Done, null, "\t\r\n", false, false, "")]
    [InlineData(TodoStatus.Done, "abc", null, true, false, "abc")]
    [InlineData(TodoStatus.Done, "abcdefg", null, true, false, "abcdefg")]
    [InlineData(TodoStatus.Done, "abcdefgh", null, true, false, "abcdefg")]
    [InlineData(TodoStatus.Done, Sha, null, true, false, "0123456")]
    [InlineData(TodoStatus.Done, null, "boom", false, true, "")]
    [InlineData(TodoStatus.Pending, null, "boom", false, true, "")]
    [InlineData(TodoStatus.InProgress, null, "boom", false, true, "")]
    [InlineData(TodoStatus.Pending, Sha, null, true, false, "0123456")]
    public async Task The_row_normalizes_the_commit_fields_it_shows(
        TodoStatus status,
        string? commitId,
        string? commitError,
        bool hasCommitId,
        bool hasCommitError,
        string commitShort)
    {
        await using var rig = await PanelRig.CreateAsync();

        var shown = await rig.Ui(
            () =>
            {
                var item = new TodoItem("row", PanelRig.SessionId, "row", status, null, 0, DateTimeOffset.UtcNow, CommitId: commitId, CommitError: commitError);
                var row = new TodoRowViewModel(rig.Panel, item);
                return (row.HasCommitId, row.CommitShort, row.HasCommitError, Retry: row.RetryCommitCommand.CanExecute(null), row.CommitError, row.CommitId, row.HasDetailLine);
            });

        Assert.Equal(hasCommitId, shown.HasCommitId);
        Assert.Equal(commitShort, shown.CommitShort);
        Assert.Equal(hasCommitError, shown.HasCommitError);
        Assert.Equal(hasCommitError, shown.Retry);
        Assert.Equal(hasCommitId || hasCommitError, shown.HasDetailLine);
        Assert.Equal(hasCommitId ? commitId : null, shown.CommitId);
        Assert.Equal(commitError is { } error && !string.IsNullOrWhiteSpace(error) ? error : null, shown.CommitError);
    }

    [Theory]
    [InlineData(TodoStatus.Pending, true, false, false, false)]
    [InlineData(TodoStatus.InProgress, false, true, false, false)]
    [InlineData(TodoStatus.Paused, false, false, true, false)]
    [InlineData(TodoStatus.Done, false, false, false, true)]
    public async Task The_row_reports_its_status_and_only_a_not_started_row_may_be_dragged(
        TodoStatus status,
        bool pending,
        bool inProgress,
        bool paused,
        bool done)
    {
        await using var rig = await PanelRig.CreateAsync();

        var shown = await rig.Ui(
            () =>
            {
                var item = new TodoItem("row", PanelRig.SessionId, "row", status, null, 0, DateTimeOffset.UtcNow);
                var row = new TodoRowViewModel(rig.Panel, item);
                return (row.IsPending, row.IsInProgress, row.IsPaused, row.IsDone, row.CanDrag, row.IsUserAdded);
            });

        Assert.Equal((pending, inProgress, paused, done), (shown.IsPending, shown.IsInProgress, shown.IsPaused, shown.IsDone));
        Assert.Equal(pending, shown.CanDrag);
        Assert.False(shown.IsUserAdded);
    }

    [Fact]
    public async Task A_row_the_user_queued_is_marked_as_added_by_hand_and_not_read_only()
    {
        await using var rig = await PanelRig.CreateAsync();

        var shown = await rig.Ui(
            () =>
            {
                var item = new TodoItem("row", PanelRig.SessionId, "row", TodoStatus.Pending, null, 0, DateTimeOffset.UtcNow, Origin: TodoOrigin.User);
                var row = new TodoRowViewModel(rig.Panel, item);
                var doneRow = new TodoRowViewModel(rig.Panel, item with { Status = TodoStatus.Done }, readOnly: true);
                return (row.IsUserAdded, row.IsReadOnly, Done: doneRow.IsReadOnly, DoneDrag: doneRow.CanDrag);
            });

        Assert.True(shown.IsUserAdded);
        Assert.False(shown.IsReadOnly);
        Assert.True(shown.Done);
        Assert.False(shown.DoneDrag);
    }

    [Fact]
    public void The_row_has_no_way_to_tick_a_step_done()
    {
        // The user tick path (and with it "ticking commits a step") is gone: the status comes from the store, and the only
        // command left on a row besides delete is the retry.
        Assert.Null(typeof(TodoRowViewModel).GetProperty(nameof(TodoRowViewModel.IsDone))!.SetMethod);
        Assert.Null(typeof(TodoRowViewModel).GetProperty(nameof(TodoRowViewModel.Title))!.SetMethod);
    }

    // ------------------------------------------------------------ retry

    [Fact]
    public async Task Retry_reads_the_item_again_and_commits_only_that_item()
    {
        await using var rig = await PanelRig.CreateAsync();
        var first = await rig.AddAsync("First", new TodoStepFields("feat: first", "old summary", null), TodoStatus.Done);
        var second = await rig.AddAsync("Second", new TodoStepFields("feat: second", "second summary", null), TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(first.Id, null, "boom one", CancellationToken.None);
        await rig.Store.SetCommitResultAsync(second.Id, null, "boom two", CancellationToken.None);
        await rig.AttachAsync();

        // The agent rewrites the summary after the panel last refreshed; the retry must not commit the stale snapshot.
        await rig.Store.UpdateAsync(first.Id, null, null, null, new TodoStepFields(null, "fresh summary", null), CancellationToken.None);
        await rig.RetryAsync(first.Id);

        var call = Assert.Single(rig.Committer.Calls);
        Assert.Equal(first.Id, call.Id);
        Assert.Equal("fresh summary", call.Summary);
        Assert.Equal("feat: first", call.CommitSubject);
        Assert.Equal(TodoStatus.Done, call.Status);
        await PanelRig.SettleAsync();
        Assert.Single(rig.Committer.Calls);
    }

    [Fact]
    public async Task Retry_does_nothing_when_the_step_is_no_longer_done()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Reopened", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        await rig.Store.UpdateAsync(item.Id, null, TodoStatus.InProgress, null, CancellationToken.None);

        await rig.RetryAsync(item.Id);
        await PanelRig.SettleAsync();

        Assert.Empty(rig.Committer.Calls);
    }

    [Fact]
    public async Task Retry_does_nothing_when_the_item_was_removed_meanwhile()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Gone", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var statusBefore = await rig.Ui(() => rig.Panel.StatusText);
        await rig.Store.RemoveAsync(item.Id, CancellationToken.None);

        await rig.RetryAsync(item.Id);
        await PanelRig.SettleAsync();

        Assert.Empty(rig.Committer.Calls);
        Assert.Equal(statusBefore, await rig.Ui(() => rig.Panel.StatusText));
    }

    [Fact]
    public async Task Retry_without_a_committer_does_nothing()
    {
        await using var rig = await PanelRig.CreateAsync(withCommitter: false);
        var item = await rig.AddAsync("No committer", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var statusBefore = await rig.Ui(() => rig.Panel.StatusText);

        await rig.RetryAsync(item.Id);

        var stored = await rig.StoredAsync(item.Id);
        Assert.Equal("boom", stored.CommitError);
        Assert.Null(stored.CommitId);
        Assert.Equal(statusBefore, await rig.Ui(() => rig.Panel.StatusText));
    }

    [Fact]
    public async Task Retry_after_the_panel_detached_does_nothing()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Detached", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var row = await rig.Ui(() => rig.Row(item.Id));
        await rig.Ui(() => rig.Panel.Attach(null));

        await rig.Ui(() => RunAsync(row.RetryCommitCommand));
        await PanelRig.SettleAsync();

        Assert.Empty(rig.Committer.Calls);
    }

    [Fact]
    public async Task The_retry_button_is_disabled_while_the_commit_runs_so_a_double_click_commits_once()
    {
        await using var rig = await PanelRig.CreateAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Committer.Hold = hold;
        var item = await rig.AddAsync("Double click", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var row = await rig.Ui(() => rig.Row(item.Id));

        // The whole click sequence runs inside one awaiting UI dispatch: the command's own continuation is posted to the UI
        // thread, and the headless platform only processes it while a dispatch is alive.
        var observed = await rig.Ui(
            async () =>
            {
                row.RetryCommitCommand.Execute(null);
                var disabledWhileRunning = !row.RetryCommitCommand.CanExecute(null);
                row.RetryCommitCommand.Execute(null);

                await rig.Committer.WaitForCallsAsync(1);
                await Task.Delay(300);
                var callsWhileHeld = rig.Committer.Calls.Count;
                var stillDisabled = !row.RetryCommitCommand.CanExecute(null);

                hold.SetResult();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!row.RetryCommitCommand.CanExecute(null))
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new TimeoutException("timed out waiting for the retry button to come back");
                    }

                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(15);
                }

                return (disabledWhileRunning, callsWhileHeld, stillDisabled);
            });

        Assert.True(observed.disabledWhileRunning);
        Assert.Equal(1, observed.callsWhileHeld);
        Assert.True(observed.stillDisabled);
        Assert.Single(rig.Committer.Calls);
    }

    [Fact]
    public async Task A_committer_that_throws_on_retry_shows_the_message_in_the_status_line()
    {
        await using var rig = await PanelRig.CreateAsync();
        rig.Committer.Failure = new IOException("git is not installed");
        var item = await rig.AddAsync("Retry boom", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();

        await rig.RetryAsync(item.Id);

        Assert.Equal("git is not installed", await rig.Ui(() => rig.Panel.StatusText));
        Assert.Single(rig.Committer.Calls);
    }

    // ------------------------------------------------------------ the rendered row

    [Fact]
    public async Task The_view_shows_the_short_id_for_a_committed_row_and_nothing_else()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Committed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, Sha, null, CancellationToken.None);

        var shown = await rig.InViewAsync(
            static view => (
                Id: view.TextOf("todo-commit-id"),
                IdVisible: view.IsVisible("todo-commit-id"),
                RetryVisible: view.IsVisible("todo-retry-commit"),
                ErrorVisible: view.IsVisible("todo-commit-error")));

        Assert.Equal("0123456", shown.Id);
        Assert.True(shown.IdVisible);
        Assert.False(shown.RetryVisible);
        Assert.False(shown.ErrorVisible);
    }

    [Fact]
    public async Task The_view_shows_the_failure_with_a_retry_and_no_id_for_a_failed_row()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Failed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "hook rejected: lint failed", CancellationToken.None);

        var shown = await rig.InViewAsync(
            static view => (
                Error: view.TextOf("todo-commit-error"),
                ErrorVisible: view.IsVisible("todo-commit-error"),
                RetryVisible: view.IsVisible("todo-retry-commit"),
                IdVisible: view.IsVisible("todo-commit-id")));

        Assert.Equal("hook rejected: lint failed", shown.Error);
        Assert.True(shown.ErrorVisible);
        Assert.True(shown.RetryVisible);
        Assert.False(shown.IdVisible);
    }

    [Fact]
    public async Task The_view_hides_the_failure_once_the_store_reopened_the_step()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Failed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);

        var shown = await rig.WithViewAsync(
            async view =>
            {
                var before = (Retry: view.IsVisible("todo-retry-commit"), Error: view.IsVisible("todo-commit-error"));

                // Only the agent reopens a step now; the row follows the store on the next refresh.
                await rig.Store.UpdateAsync(item.Id, null, TodoStatus.InProgress, null, CancellationToken.None);
                await rig.Panel.RefreshAsync(CancellationToken.None);
                await Task.Delay(300);
                view.Settle();
                return (before, After: (Retry: view.IsVisible("todo-retry-commit"), Error: view.IsVisible("todo-commit-error")));
            });

        Assert.Equal((true, true), shown.before);
        Assert.Equal((false, false), shown.After);
        Assert.Empty(rig.Committer.Calls);
    }

    [Fact]
    public async Task The_view_has_no_check_box_and_no_delete_button_for_a_finished_row()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Historical", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, Sha, null, CancellationToken.None);

        var shown = await rig.InViewAsync(
            static view => (
                CheckBox: view.Has("todo-done"),
                Row: view.Has("todo-row"),
                DeleteVisible: view.IsVisible("todo-delete"),
                DoneSection: view.IsVisible("todo-done-section"),
                Title: view.TextOf("todo-commit-id")));

        Assert.False(shown.CheckBox);
        Assert.True(shown.Row);
        Assert.False(shown.DeleteVisible);
        Assert.True(shown.DoneSection);
        Assert.Equal("0123456", shown.Title);
    }

    [Fact]
    public async Task The_view_marks_a_row_the_user_queued_with_the_line_user_icon()
    {
        await using var rig = await PanelRig.CreateAsync();
        await rig.Store.AddAsync(new TodoAddRequest(PanelRig.SessionId, "queued by hand", Origin: TodoOrigin.User), CancellationToken.None);

        var shown = await rig.InViewAsync(
            static view => (User: view.IsVisible("todo-source-user"), Delete: view.Has("todo-delete")));

        Assert.True(shown.User);
        Assert.True(shown.Delete);
    }

    [Fact]
    public async Task Clicking_the_retry_button_commits_that_row_again()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Failed", new TodoStepFields("fix: it", "Summary text.", null), TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);

        await rig.InViewAsync(
            static view =>
            {
                view.Click("todo-retry-commit");
                return 0;
            });
        await rig.Committer.WaitForCallsAsync(1);

        var call = Assert.Single(rig.Committer.Calls);
        Assert.Equal(item.Id, call.Id);
        Assert.Equal("fix: it", call.CommitSubject);
    }

    private static async Task RunAsync(AsyncRelayCommand command)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        command.CanExecuteChanged += (_, _) =>
        {
            // One notification when the run starts, one when it ends.
            if (++notifications == 2)
            {
                finished.TrySetResult();
            }
        };
        command.Execute(null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ------------------------------------------------------------ rig

    /// <summary>One headless session, a real temp store, a recording committer and a panel on the UI thread.</summary>
    private sealed class PanelRig : IAsyncDisposable
    {
        public const string SessionId = "session-todo";

        private readonly HeadlessUnitTestSession session;
        private readonly string directory;

        private PanelRig(HeadlessUnitTestSession session, string directory, SqliteTodoStore store, RecordingCommitter committer, TodoListViewModel panel)
        {
            this.session = session;
            this.directory = directory;
            Store = store;
            Committer = committer;
            Panel = panel;
        }

        public SqliteTodoStore Store { get; }

        public RecordingCommitter Committer { get; }

        public TodoListViewModel Panel { get; }

        public static async Task<PanelRig> CreateAsync(bool withCommitter = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "forge-todo-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var store = new SqliteTodoStore(directory);
            var committer = new RecordingCommitter();
            var panel = await session.Dispatch(() => new TodoListViewModel(store, withCommitter ? committer : null), CancellationToken.None);
            return new PanelRig(session, directory, store, committer, panel);
        }

        public Task<T> Ui<T>(Func<T> action) => session.Dispatch(action, CancellationToken.None);

        /// <summary>An awaiting dispatch that returns a value; the UI thread is pumped while it awaits.</summary>
        public Task<T> Ui<T>(Func<Task<T>> action) => session.Dispatch(action, CancellationToken.None);

        public Task Ui(Action action) =>
            session.Dispatch(
                () =>
                {
                    action();
                    return 0;
                },
                CancellationToken.None);

        public Task Ui(Func<Task> action) =>
            session.Dispatch(
                async () =>
                {
                    await action();
                    return 0;
                },
                CancellationToken.None);

        /// <summary>The row of one item, wherever the panel put it (the queue, a group or the done section).</summary>
        public TodoRowViewModel Row(string id) =>
            Panel.Loose
                .Concat(Panel.DoneLoose)
                .Concat(Panel.Groups.Concat(Panel.DoneGroups).SelectMany(static group => group.Entries))
                .Single(row => string.Equals(row.Id, id, StringComparison.Ordinal));

        public Task<TodoItem> AddAsync(string title, TodoStepFields? step = null, TodoStatus status = TodoStatus.Pending) =>
            AddCoreAsync(step, status, title);

        private async Task<TodoItem> AddCoreAsync(TodoStepFields? step, TodoStatus status, string title)
        {
            var added = await Store.AddAsync(SessionId, title, null, null, CancellationToken.None);
            if (step is null && status == TodoStatus.Pending)
            {
                return added;
            }

            return await Store.UpdateAsync(added.Id, null, status, null, step, CancellationToken.None)
                ?? throw new InvalidOperationException("the item vanished");
        }

        public async Task<IReadOnlyList<TodoItem>> ListAsync() => await Store.ListAsync(SessionId, CancellationToken.None);

        public async Task<TodoItem> StoredAsync(string id) => (await ListAsync()).Single(item => item.Id == id);

        public async Task<TodoItem> StoredAsync(string id, Func<TodoItem, bool> predicate)
        {
            TodoItem? found = null;
            await WaitUntilAsync(
                async () =>
                {
                    found = (await ListAsync()).FirstOrDefault(item => item.Id == id);
                    return found is not null && predicate(found);
                },
                "the stored item to reach the expected state");
            return found!;
        }

        public Task AttachAsync() =>
            Ui(async () =>
            {
                Panel.Attach(SessionId);
                await Panel.RefreshAsync(CancellationToken.None);
            });

        public Task RefreshAsync() => Ui(() => Panel.RefreshAsync(CancellationToken.None));

        /// <summary>Runs a row's retry command to the end on the UI thread (the command is an async void, so its two notifications mark the end).</summary>
        public Task RetryAsync(string id) =>
            Ui(() => RunAsync(Row(id).RetryCommitCommand));

        public async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!await condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("timed out waiting for " + what);
                }

                await Task.Delay(15);
            }
        }

        public static Task SettleAsync() => Task.Delay(300);

        /// <summary>Shows the real <see cref="TodoToolView"/> over the panel (refreshed first) and runs <paramref name="inspect"/> on the UI thread.</summary>
        public Task<T> InViewAsync<T>(Func<ViewProbe, T> inspect) =>
            WithViewAsync(view => Task.FromResult(inspect(view)));

        /// <summary>
        /// Like <see cref="InViewAsync{T}"/> for an inspection that awaits (the UI thread keeps running while it does, so the
        /// panel's own refreshes are processed). It must not call back into <c>Ui</c>, which would wait on the thread it runs on.
        /// </summary>
        public Task<T> WithViewAsync<T>(Func<ViewProbe, Task<T>> inspect) =>
            session.Dispatch(
                async () =>
                {
                    Panel.Attach(SessionId);
                    await Panel.RefreshAsync(CancellationToken.None);
                    var window = new Window { Width = 520, Height = 400, Content = new TodoToolView { DataContext = Panel } };
                    window.Show();
                    try
                    {
                        var probe = new ViewProbe(window);
                        probe.Settle();
                        return await inspect(probe);
                    }
                    finally
                    {
                        window.Close();
                    }
                },
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            // Let the posted refreshes of the last write run before the store goes away.
            await Task.Delay(50);
            await Ui(Panel.Dispose);
            await session.DisposeAsync();
            await Store.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            StepCommitRepo.DeleteTree(directory);
        }
    }

    /// <summary>Finds controls of the rendered panel by automation id.</summary>
    private sealed class ViewProbe(Window window)
    {
        public void Settle()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }

        public bool Has(string automationId) =>
            window.GetVisualDescendants()
                .OfType<Control>()
                .Any(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

        public Control Find(string automationId) =>
            window.GetVisualDescendants()
                .OfType<Control>()
                .First(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

        public bool IsVisible(string automationId) => Find(automationId).IsEffectivelyVisible;

        public string? TextOf(string automationId) => ((Avalonia.Controls.TextBlock)Find(automationId)).Text;

        /// <summary>Presses and releases the left pointer over the control, the way a user click reaches its command.</summary>
        public void Click(string automationId)
        {
            var control = Find(automationId);
            var center = Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("the control is not in the window");
            window.MouseDown(center, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center, Avalonia.Input.MouseButton.Left);
        }
    }

    /// <summary>Records every call and can hold a commit open or fail it.</summary>
    private sealed class RecordingCommitter : IStepCommitter
    {
        private readonly Lock gate = new();
        private readonly List<TodoItem> calls = [];

        /// <summary>When set, a commit waits for it before it returns.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>When set, a commit throws it (after any hold).</summary>
        public Exception? Failure { get; set; }

        public IReadOnlyList<TodoItem> Calls
        {
            get
            {
                lock (gate)
                {
                    return [.. calls];
                }
            }
        }

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            lock (gate)
            {
                calls.Add(item);
            }

            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            return new StepCommitResult(StepCommitOutcome.Committed, Sha, "Committed 0123456: " + item.Title);
        }

        public Task WaitForCallsAsync(int count) => WaitAsync(() => Calls.Count >= count, "the committer to be called " + count + " time(s)");

        private static async Task WaitAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("timed out waiting for " + what);
                }

                await Task.Delay(10);
            }
        }
    }
}

/// <summary>
/// The panel over the real committer, the real store and a real repository: the agent's own commit attempt records the
/// failure the panel shows, and the panel's "Retry commit" runs the real commit once the cause is fixed.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TodoStepCommitEndToEndTests
{
    [Fact]
    public async Task A_failed_step_shows_on_the_row_and_the_retry_commits_after_the_cause_is_fixed()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Add feature", new TodoStepFields("feat: add feature", "Adds the feature.", "dotnet test"), TodoStatus.Done);
        rig.Repo.WriteFile("feature.txt", "feature\n");
        rig.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        var before = await rig.Repo.CommitCountAsync();

        // The agent marks the step done; its own commit fails and the store keeps the reason.
        await rig.Harness.Committer.CommitStepAsync(await rig.StoredAsync(item.Id), CancellationToken.None);
        var failed = await rig.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, failed.Status);
        Assert.Contains("hook rejected: lint failed", failed.CommitError, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());

        await rig.AttachAsync();
        var failedRow = await rig.Ui(() => (rig.Row(item.Id).HasCommitError, Retry: rig.Row(item.Id).RetryCommitCommand.CanExecute(null), rig.Row(item.Id).HasCommitId));
        Assert.True(failedRow.HasCommitError);
        Assert.True(failedRow.Retry);
        Assert.False(failedRow.HasCommitId);

        // Retrying while the hook still rejects fails again and keeps the row retryable.
        await rig.RetryAsync(item.Id);
        Assert.Contains("hook rejected: lint failed", (await rig.StoredAsync(item.Id)).CommitError, StringComparison.Ordinal);

        rig.Repo.RemoveHook("pre-commit");
        await rig.RetryAsync(item.Id);

        var fixedItem = await rig.StoredAsync(item.Id);
        var head = await rig.Repo.HeadAsync();
        Assert.Equal(head, fixedItem.CommitId);
        Assert.Null(fixedItem.CommitError);
        Assert.Equal(before + 1, await rig.Repo.CommitCountAsync());
        await rig.RefreshAsync();
        var fixedRow = await rig.Ui(() => (rig.Row(item.Id).HasCommitError, rig.Row(item.Id).CommitShort));
        Assert.False(fixedRow.HasCommitError);
        Assert.Equal(head[..7], fixedRow.CommitShort);
    }

    [Fact]
    public async Task The_retry_commits_everything_uncommitted_in_the_worktree_not_only_the_agents_files()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Manual step", status: TodoStatus.Done);
        rig.Repo.WriteFile("seed.txt", "edited by hand\n");
        rig.Repo.WriteFile("new/dir/file.txt", "new\n");
        await rig.Harness.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();

        await rig.RetryAsync(item.Id);
        await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitId is not null);

        Assert.Equal(["A\tnew/dir/file.txt", "M\tseed.txt"], await rig.Repo.LastCommitChangesAsync());
        Assert.Equal("Manual step", await rig.Repo.LastMessageAsync());
        Assert.Equal(string.Empty, await rig.Repo.StatusAsync());
    }

    /// <summary>The panel over a <see cref="StepCommitHarness"/>; its committer is wrapped so the test can wait for each call to finish.</summary>
    private sealed class EndToEndRig : IAsyncDisposable
    {
        private readonly HeadlessUnitTestSession session;

        private EndToEndRig(HeadlessUnitTestSession session, StepCommitHarness harness, ObservingCommitter committer, TodoListViewModel panel)
        {
            this.session = session;
            Harness = harness;
            Committer = committer;
            Panel = panel;
        }

        public StepCommitHarness Harness { get; }

        public StepCommitRepo Repo => Harness.Repo;

        public ObservingCommitter Committer { get; }

        public TodoListViewModel Panel { get; }

        public static async Task<EndToEndRig> CreateAsync()
        {
            var harness = await StepCommitHarness.CreateAsync();
            var session = HeadlessUnitTestSession.StartNew(typeof(App));
            var committer = new ObservingCommitter(harness.Committer);
            var panel = await session.Dispatch(() => new TodoListViewModel(harness.Store, committer), CancellationToken.None);
            return new EndToEndRig(session, harness, committer, panel);
        }

        public Task<T> Ui<T>(Func<T> action) => session.Dispatch(action, CancellationToken.None);

        public Task Ui(Action action) =>
            session.Dispatch(
                () =>
                {
                    action();
                    return 0;
                },
                CancellationToken.None);

        public Task Ui(Func<Task> action) =>
            session.Dispatch(
                async () =>
                {
                    await action();
                    return 0;
                },
                CancellationToken.None);

        /// <summary>The row of one item, wherever the panel put it (the queue, a group or the done section).</summary>
        public TodoRowViewModel Row(string id) =>
            Panel.Loose
                .Concat(Panel.DoneLoose)
                .Concat(Panel.Groups.Concat(Panel.DoneGroups).SelectMany(static group => group.Entries))
                .Single(row => string.Equals(row.Id, id, StringComparison.Ordinal));

        public async Task<TodoItem> AddAsync(string title, TodoStepFields? step = null, TodoStatus status = TodoStatus.Pending)
        {
            var added = await Harness.Store.AddAsync(StepCommitHarness.SessionId, title, null, null, CancellationToken.None);
            if (step is null && status == TodoStatus.Pending)
            {
                return added;
            }

            return await Harness.Store.UpdateAsync(added.Id, null, status, null, step, CancellationToken.None)
                ?? throw new InvalidOperationException("the item vanished");
        }

        public Task<TodoItem> StoredAsync(string id) => Harness.StoredAsync(id);

        public async Task<TodoItem> WaitForStoredAsync(string id, Func<TodoItem, bool> predicate)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (true)
            {
                var item = await Harness.StoredAsync(id);
                if (predicate(item))
                {
                    return item;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("timed out waiting for the stored item to reach the expected state");
                }

                await Task.Delay(20);
            }
        }

        public Task AttachAsync() =>
            Ui(async () =>
            {
                Panel.Attach(StepCommitHarness.SessionId);
                await Panel.RefreshAsync(CancellationToken.None);
            });

        public Task RefreshAsync() => Ui(() => Panel.RefreshAsync(CancellationToken.None));

        /// <summary>Runs the row's retry command to its end (the committer wrapper tells when the commit itself finished).</summary>
        public async Task RetryAsync(string id)
        {
            var calls = Committer.Started;
            await Ui(
                async () =>
                {
                    var command = Row(id).RetryCommitCommand;
                    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var notifications = 0;
                    command.CanExecuteChanged += (_, _) =>
                    {
                        if (++notifications == 2)
                        {
                            finished.TrySetResult();
                        }
                    };
                    command.Execute(null);
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
                });
            Assert.Equal(calls + 1, Committer.Started);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(50);
            await Ui(() => Panel.Dispose());
            await session.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>Forwards to the real committer and counts started calls.</summary>
    private sealed class ObservingCommitter(IStepCommitter inner) : IStepCommitter
    {
        private int started;

        public int Started => Volatile.Read(ref started);

        public Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Interlocked.Increment(ref started);
            return inner.CommitStepAsync(item, ct);
        }
    }
}
