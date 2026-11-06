using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The TODO panel's half of step commits: a user tick from not-done to done commits that step exactly once, with the item
/// as the store returned it; nothing else the panel does (unticking, editing a title, reordering, removing, a refresh that
/// brings in an item the agent finished) ever commits; a failed commit shows on the row with a retry that reads the item
/// again and commits only that item while it is still done. A recording committer isolates the panel; the end-to-end
/// classes below put the real committer, store and git behind the same panel.
///
/// Negative assertions follow a fence (the store write the trigger caused has landed) and a short settle delay: the commit
/// would run right after that write on the same task, so a call that should not happen has had ample time to show up.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TodoStepCommitTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    // ------------------------------------------------------------ the user tick

    [Fact]
    public async Task Ticking_an_item_done_commits_that_step_once_with_the_item_as_the_store_returned_it()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Add feature", new TodoStepFields("feat: add feature", "Adds it.", "dotnet test"));
        await rig.AttachAsync();

        await rig.TickAsync(item.Id, done: true);
        await rig.Committer.WaitForCallsAsync(1);

        var call = Assert.Single(rig.Committer.Calls);
        Assert.Equal(item.Id, call.Id);
        Assert.Equal(PanelRig.SessionId, call.SessionId);
        Assert.Equal(TodoStatus.Done, call.Status);
        Assert.Equal("feat: add feature", call.CommitSubject);
        Assert.Equal("Adds it.", call.Summary);
        Assert.Equal("dotnet test", call.Verification);
        Assert.Equal(TodoStatus.Done, (await rig.StoredAsync(item.Id)).Status);

        // The refreshes the writes cause rebuild the row as done without ticking it, so they never commit again.
        await rig.RefreshAsync();
        await rig.RefreshAsync();
        await PanelRig.SettleAsync();
        Assert.Single(rig.Committer.Calls);
    }

    [Fact]
    public async Task The_commit_runs_off_the_UI_thread_and_does_not_block_it()
    {
        await using var rig = await PanelRig.CreateAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Committer.Hold = hold;
        var item = await rig.AddAsync("Slow step");
        await rig.AttachAsync();

        await rig.TickAsync(item.Id, done: true);
        await rig.Committer.WaitForCallsAsync(1);

        // The commit is in flight and the UI thread is free: a round trip to it still answers.
        Assert.False(rig.Committer.LastCallOnUiThread);
        Assert.Equal(TodoStatus.Done, (await rig.StoredAsync(item.Id)).Status);
        Assert.True(await rig.Ui(() => rig.Panel.Items.Single().IsDone));

        hold.SetResult();
        await rig.Committer.WaitForCompletedAsync(1);
    }

    [Fact]
    public async Task Ticking_again_after_an_untick_commits_again()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Flip");
        await rig.AttachAsync();

        await rig.TickAsync(item.Id, done: true);
        await rig.Committer.WaitForCallsAsync(1);
        await rig.StoredAsync(item.Id, static stored => stored.Status == TodoStatus.Done);

        await rig.TickAsync(item.Id, done: false);
        await rig.StoredAsync(item.Id, static stored => stored.Status == TodoStatus.Pending);
        await PanelRig.SettleAsync();
        Assert.Single(rig.Committer.Calls);

        await rig.TickAsync(item.Id, done: true);
        await rig.Committer.WaitForCallsAsync(2);
        Assert.All(rig.Committer.Calls, static call => Assert.Equal(TodoStatus.Done, call.Status));
    }

    [Theory]
    [InlineData("untick")]
    [InlineData("edit-title-of-pending-item")]
    [InlineData("edit-title-of-done-item")]
    [InlineData("move-down")]
    [InlineData("move-up")]
    [InlineData("remove")]
    public async Task Other_edits_never_commit(string edit)
    {
        await using var rig = await PanelRig.CreateAsync();
        var firstStatus = edit is "untick" or "edit-title-of-done-item" ? TodoStatus.Done : TodoStatus.Pending;
        var first = await rig.AddAsync("first", status: firstStatus);
        var second = await rig.AddAsync("second");
        await rig.AttachAsync();

        switch (edit)
        {
            case "untick":
                await rig.TickAsync(first.Id, done: false);
                await rig.StoredAsync(first.Id, static stored => stored.Status == TodoStatus.Pending);
                break;
            case "edit-title-of-pending-item":
            case "edit-title-of-done-item":
                await rig.Ui(() => rig.Panel.Items.Single(entry => entry.Id == first.Id).Title = "renamed");
                await rig.StoredAsync(first.Id, static stored => stored.Title == "renamed");
                break;
            case "move-down":
                await rig.Ui(() => rig.Panel.Items.Single(entry => entry.Id == first.Id).MoveDownCommand.Execute(null));
                await rig.StoredAsync(first.Id, static stored => stored.Order == 1);
                break;
            case "move-up":
                await rig.Ui(() => rig.Panel.Items.Single(entry => entry.Id == second.Id).MoveUpCommand.Execute(null));
                await rig.StoredAsync(second.Id, static stored => stored.Order == 0);
                break;
            case "remove":
                await rig.Ui(() => rig.Panel.Items.Single(entry => entry.Id == first.Id).RemoveCommand.Execute(null));
                await rig.WaitUntilAsync(async () => (await rig.ListAsync()).All(stored => stored.Id != first.Id), "the item to be removed");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edit), edit, null);
        }

        await rig.RefreshAsync();
        await PanelRig.SettleAsync();
        Assert.Empty(rig.Committer.Calls);
    }

    [Fact]
    public async Task A_refresh_that_brings_in_an_item_the_agent_finished_never_commits()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Agent step");
        await rig.AttachAsync();
        Assert.False(await rig.Ui(() => rig.Panel.Items.Single().IsDone));

        // The agent's todo tool writes through the store, not through the panel.
        await rig.Store.UpdateAsync(item.Id, null, TodoStatus.Done, null, new TodoStepFields("fix: agent", "Did it.", null), CancellationToken.None);
        await rig.Store.SetCommitResultAsync(item.Id, Sha, null, CancellationToken.None);
        await rig.RefreshAsync();
        await rig.Ui(() => Dispatcher.UIThread.RunJobs());
        await rig.RefreshAsync();
        await PanelRig.SettleAsync();

        var entry = await rig.Ui(() => rig.Panel.Items.Single());
        Assert.True(await rig.Ui(() => entry.IsDone));
        Assert.Equal(Sha[..7], await rig.Ui(() => entry.CommitShort));
        Assert.Empty(rig.Committer.Calls);
    }

    [Fact]
    public async Task Without_a_committer_ticking_only_marks_the_item_done()
    {
        await using var rig = await PanelRig.CreateAsync(withCommitter: false);
        var item = await rig.AddAsync("Plain");
        await rig.AttachAsync();
        var summaryBefore = await rig.Ui(() => rig.Panel.Summary);

        await rig.TickAsync(item.Id, done: true);
        await rig.StoredAsync(item.Id, static stored => stored.Status == TodoStatus.Done);
        await PanelRig.SettleAsync();

        Assert.Equal(summaryBefore, await rig.Ui(() => rig.Panel.Summary));
        Assert.Null((await rig.StoredAsync(item.Id)).CommitId);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_committer_that_throws_after_a_tick_shows_the_message_in_the_summary_line(Type exceptionType)
    {
        await using var rig = await PanelRig.CreateAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Committer.Hold = hold;
        rig.Committer.Failure = (Exception)Activator.CreateInstance(exceptionType, "the commit machinery fell over")!;
        var item = await rig.AddAsync("Boom");
        await rig.AttachAsync();
        var finished = InspectorStrings.TodoSummary(1, 1).Resolve();

        // The refresh the status write causes rebuilds the summary line, so the commit is held until that refresh has
        // landed; otherwise it could finish after the failure and overwrite the message (the summary is a transient line,
        // the next refresh replaces it). Everything runs in one awaiting dispatch: the headless UI thread only picks up
        // what other threads post to it while such a dispatch is alive.
        await rig.Ui(async () =>
        {
            await rig.Panel.RefreshAsync(CancellationToken.None);
            rig.Panel.Items.Single(entry => entry.Id == item.Id).IsDone = true;
            await PanelRig.PumpUntilAsync(() => rig.Panel.Summary == finished, "the panel to show the finished step");
            hold.SetResult();
            await PanelRig.PumpUntilAsync(
                () => rig.Panel.Summary == "the commit machinery fell over",
                "the committer's message in the summary line");
        });

        Assert.Equal(TodoStatus.Done, (await rig.StoredAsync(item.Id)).Status);
    }

    [Fact]
    public async Task A_cancelled_commit_after_a_tick_leaves_the_summary_alone()
    {
        await using var rig = await PanelRig.CreateAsync();
        rig.Committer.Failure = new OperationCanceledException();
        var item = await rig.AddAsync("Cancelled");
        await rig.AttachAsync();
        var summaryBefore = await rig.Ui(() => rig.Panel.Summary);

        await rig.TickAsync(item.Id, done: true);
        await rig.Committer.WaitForCallsAsync(1);
        await PanelRig.SettleAsync();
        await rig.Ui(() => Dispatcher.UIThread.RunJobs());

        Assert.Equal(summaryBefore, await rig.Ui(() => rig.Panel.Summary));
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
                var entry = rig.Panel.Items.Single();
                return (entry.HasCommitId, entry.CommitId, entry.CommitShort, entry.HasCommitError, entry.CommitError, Retry: entry.RetryCommitCommand.CanExecute(null));
            });

        Assert.True(shown.HasCommitId);
        Assert.Equal(Sha, shown.CommitId);
        Assert.Equal("0123456", shown.CommitShort);
        Assert.False(shown.HasCommitError);
        Assert.Null(shown.CommitError);
        Assert.False(shown.Retry);
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
                var entry = rig.Panel.Items.Single();
                return (entry.HasCommitError, entry.CommitError, entry.HasCommitId, entry.CommitShort, Retry: entry.RetryCommitCommand.CanExecute(null));
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
    [InlineData(TodoStatus.Pending, null, "boom", false, false, "")]
    [InlineData(TodoStatus.InProgress, null, "boom", false, false, "")]
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
                var entry = new TodoEntryViewModel(rig.Panel, item);
                return (entry.HasCommitId, entry.CommitShort, entry.HasCommitError, Retry: entry.RetryCommitCommand.CanExecute(null), entry.CommitError, entry.CommitId);
            });

        Assert.Equal(hasCommitId, shown.HasCommitId);
        Assert.Equal(commitShort, shown.CommitShort);
        Assert.Equal(hasCommitError, shown.HasCommitError);
        Assert.Equal(hasCommitError, shown.Retry);
        Assert.Equal(hasCommitId ? commitId : null, shown.CommitId);
        Assert.Equal(commitError is { } error && !string.IsNullOrWhiteSpace(error) ? error : null, shown.CommitError);
    }

    [Fact]
    public async Task Unticking_a_failed_row_hides_the_failure_and_disables_the_retry_at_once()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Failed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();

        var observed = await rig.Ui(
            () =>
            {
                var entry = rig.Panel.Items.Single();
                var changed = new List<string?>();
                var canExecuteChanged = 0;
                entry.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
                entry.RetryCommitCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
                var before = (entry.HasCommitError, Retry: entry.RetryCommitCommand.CanExecute(null));

                entry.IsDone = false;
                var unticked = (entry.HasCommitError, Retry: entry.RetryCommitCommand.CanExecute(null));
                return (before, unticked, Changed: changed.ToArray(), Notified: canExecuteChanged);
            });

        Assert.Equal((true, true), observed.before);
        Assert.Equal((false, false), observed.unticked);
        Assert.Contains(nameof(TodoEntryViewModel.HasCommitError), observed.Changed);
        Assert.True(observed.Notified >= 1);

        // Reopening a step is not a commit trigger, and it drops the recorded failure from the store.
        var reopened = await rig.StoredAsync(item.Id, static stored => stored.Status == TodoStatus.Pending);
        await PanelRig.SettleAsync();
        Assert.Null(reopened.CommitError);
        Assert.Empty(rig.Committer.Calls);
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
        var summaryBefore = await rig.Ui(() => rig.Panel.Summary);
        await rig.Store.RemoveAsync(item.Id, CancellationToken.None);

        await rig.RetryAsync(item.Id);
        await PanelRig.SettleAsync();

        Assert.Empty(rig.Committer.Calls);
        Assert.Equal(summaryBefore, await rig.Ui(() => rig.Panel.Summary));
    }

    [Fact]
    public async Task Retry_without_a_committer_does_nothing()
    {
        await using var rig = await PanelRig.CreateAsync(withCommitter: false);
        var item = await rig.AddAsync("No committer", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var summaryBefore = await rig.Ui(() => rig.Panel.Summary);

        await rig.RetryAsync(item.Id);

        var stored = await rig.StoredAsync(item.Id);
        Assert.Equal("boom", stored.CommitError);
        Assert.Null(stored.CommitId);
        Assert.Equal(summaryBefore, await rig.Ui(() => rig.Panel.Summary));
    }

    [Fact]
    public async Task Retry_after_the_panel_detached_does_nothing()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Detached", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();
        var entry = await rig.Ui(() => rig.Panel.Items.Single());
        await rig.Ui(() => rig.Panel.Attach(null));

        await rig.Ui(() => RunAsync(entry.RetryCommitCommand));
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
        var entry = await rig.Ui(() => rig.Panel.Items.Single());

        // The whole click sequence runs inside one awaiting UI dispatch: the command's own continuation is posted to the UI
        // thread, and the headless platform only processes it while a dispatch is alive.
        var observed = await rig.Ui(
            async () =>
            {
                entry.RetryCommitCommand.Execute(null);
                var disabledWhileRunning = !entry.RetryCommitCommand.CanExecute(null);
                entry.RetryCommitCommand.Execute(null);

                await rig.Committer.WaitForCallsAsync(1);
                await Task.Delay(300);
                var callsWhileHeld = rig.Committer.Calls.Count;
                var stillDisabled = !entry.RetryCommitCommand.CanExecute(null);

                hold.SetResult();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!entry.RetryCommitCommand.CanExecute(null))
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
    public async Task A_committer_that_throws_on_retry_shows_the_message_in_the_summary_line()
    {
        await using var rig = await PanelRig.CreateAsync();
        rig.Committer.Failure = new IOException("git is not installed");
        var item = await rig.AddAsync("Retry boom", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);
        await rig.AttachAsync();

        await rig.RetryAsync(item.Id);

        Assert.Equal("git is not installed", await rig.Ui(() => rig.Panel.Summary));
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
    public async Task The_view_hides_the_failure_when_the_row_is_unticked_and_it_stays_gone_after_the_store_reopened_the_step()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("Failed", status: TodoStatus.Done);
        await rig.Store.SetCommitResultAsync(item.Id, null, "boom", CancellationToken.None);

        var shown = await rig.WithViewAsync(
            async view =>
            {
                var before = (Retry: view.IsVisible("todo-retry-commit"), Error: view.IsVisible("todo-commit-error"));

                view.CheckBoxOf("todo-done").IsChecked = false;
                var immediately = (Retry: view.IsVisible("todo-retry-commit"), Error: view.IsVisible("todo-commit-error"));

                // Let the write land and the panel rebuild its rows from it: a reopened step has no commit outcome.
                await rig.StoredAsync(item.Id, static stored => stored.Status == TodoStatus.Pending);
                await rig.Panel.RefreshAsync(CancellationToken.None);
                await Task.Delay(300);
                view.Settle();
                var afterRefresh = (Retry: view.IsVisible("todo-retry-commit"), Error: view.IsVisible("todo-commit-error"));
                return (before, immediately, afterRefresh);
            });

        Assert.Equal((true, true), shown.before);
        Assert.Equal((false, false), shown.immediately);
        Assert.Equal((false, false), shown.afterRefresh);
        Assert.Empty(rig.Committer.Calls);
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

    [Fact]
    public async Task Ticking_the_checkbox_in_the_view_commits_the_step()
    {
        await using var rig = await PanelRig.CreateAsync();
        var item = await rig.AddAsync("From the box");

        await rig.InViewAsync(
            static view =>
            {
                view.CheckBoxOf("todo-done").IsChecked = true;
                return 0;
            });
        await rig.Committer.WaitForCallsAsync(1);

        Assert.Equal(item.Id, Assert.Single(rig.Committer.Calls).Id);
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
            committer.UiThreadId = await session.Dispatch(() => Environment.CurrentManagedThreadId, CancellationToken.None);
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

        public Task<TodoItem> AddAsync(string title, TodoStepFields? step = null, TodoStatus status = TodoStatus.Pending) =>
            AddCoreAsync(title, step, status);

        private async Task<TodoItem> AddCoreAsync(string title, TodoStepFields? step, TodoStatus status)
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

        /// <summary>
        /// Sets the row's check box the way a click does. The panel is refreshed first so the row mirrors the store: a
        /// late refresh from an earlier write could otherwise leave the row already in the requested state, and setting
        /// the same value is not a tick.
        /// </summary>
        public Task TickAsync(string id, bool done) =>
            Ui(async () =>
            {
                await Panel.RefreshAsync(CancellationToken.None);
                Panel.Items.Single(entry => entry.Id == id).IsDone = done;
            });

        /// <summary>Runs the row's retry command to the end on the UI thread (the command is an async void, so its two notifications mark the end).</summary>
        public Task RetryAsync(string id) =>
            Ui(() => RunAsync(Panel.Items.Single(entry => entry.Id == id).RetryCommitCommand));

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

        /// <summary>
        /// Waits for <paramref name="condition"/>, evaluated on the UI thread, while that thread keeps processing what the
        /// background posts to it: an async dispatch is pumped while it awaits, a synchronous one only when it asks.
        /// </summary>
        public Task WaitUntilOnUiAsync(Func<bool> condition, string what) => Ui(() => PumpUntilAsync(condition, what));

        /// <summary>
        /// The wait itself, for a caller that is already inside an awaiting dispatch (it must not call <c>Ui</c>, which would
        /// wait on the thread it runs on): processes what the background posts to the UI thread until the condition holds.
        /// </summary>
        public static async Task PumpUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                Dispatcher.UIThread.RunJobs();
                if (condition())
                {
                    return;
                }

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

        public Control Find(string automationId) =>
            window.GetVisualDescendants()
                .OfType<Control>()
                .First(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

        public bool IsVisible(string automationId) => Find(automationId).IsEffectivelyVisible;

        public string? TextOf(string automationId) => ((Avalonia.Controls.TextBlock)Find(automationId)).Text;

        public CheckBox CheckBoxOf(string automationId) => (CheckBox)Find(automationId);

        /// <summary>Presses and releases the left pointer over the control, the way a user click reaches its command.</summary>
        public void Click(string automationId)
        {
            var control = Find(automationId);
            var center = Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("the control is not in the window");
            window.MouseDown(center, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center, Avalonia.Input.MouseButton.Left);
        }

        public Button ButtonOf(string automationId) => (Button)Find(automationId);
    }

    /// <summary>Records every call; can hold a commit open, fail it, and tells which thread it ran on.</summary>
    private sealed class RecordingCommitter : IStepCommitter
    {
        private readonly Lock gate = new();
        private readonly List<TodoItem> calls = [];
        private int completed;
        private bool lastOnUiThread;
        private int uiThreadId = -1;

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

        /// <summary>
        /// The managed id of the UI thread. <c>Dispatcher.UIThread.CheckAccess()</c> answers true on every thread under the
        /// headless platform, so the thread is told apart by its id; the headless UI thread is dedicated to the session.
        /// </summary>
        public int UiThreadId
        {
            set
            {
                lock (gate)
                {
                    uiThreadId = value;
                }
            }
        }

        public bool LastCallOnUiThread
        {
            get
            {
                lock (gate)
                {
                    return lastOnUiThread;
                }
            }
        }

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            lock (gate)
            {
                calls.Add(item);
                lastOnUiThread = Environment.CurrentManagedThreadId == uiThreadId;
            }

            try
            {
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
            finally
            {
                lock (gate)
                {
                    completed++;
                }
            }
        }

        public Task WaitForCallsAsync(int count) => WaitAsync(() => Calls.Count >= count, "the committer to be called " + count + " time(s)");

        public Task WaitForCompletedAsync(int count)
        {
            return WaitAsync(
                () =>
                {
                    lock (gate)
                    {
                        return completed >= count;
                    }
                },
                count + " commit(s) to finish");
        }

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
/// The panel over the real committer, the real store and a real repository: ticking commits and shows the short id, a
/// failing hook shows the failure with the retry, and the retry commits once the cause is fixed.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TodoStepCommitEndToEndTests
{
    [Fact]
    public async Task Ticking_a_row_commits_the_worktree_with_the_step_fields_and_the_row_shows_the_short_sha()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Add feature", new TodoStepFields("feat: add feature", "Adds the feature.", "dotnet test"));
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.AttachAsync();

        await rig.TickAsync(item.Id);
        var stored = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitId is not null);

        var head = await rig.Repo.HeadAsync();
        Assert.Equal(head, stored.CommitId);
        Assert.Equal("feat: add feature\n\nAdds the feature.", await rig.Repo.LastMessageAsync());
        Assert.Equal(string.Empty, await rig.Repo.StatusAsync());
        await rig.RefreshAsync();
        var shown = await rig.Ui(() => (rig.Panel.Items.Single().HasCommitId, rig.Panel.Items.Single().CommitShort, rig.Panel.Items.Single().HasCommitError));
        Assert.True(shown.HasCommitId);
        Assert.Equal(head[..7], shown.CommitShort);
        Assert.False(shown.HasCommitError);
    }

    [Fact]
    public async Task A_failing_hook_shows_on_the_row_and_the_retry_commits_after_the_cause_is_fixed()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        rig.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        var before = await rig.Repo.CommitCountAsync();
        await rig.AttachAsync();

        await rig.TickAsync(item.Id);
        var failed = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitError is not null);

        Assert.Equal(TodoStatus.Done, failed.Status);
        Assert.Contains("hook rejected: lint failed", failed.CommitError, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        await rig.RefreshAsync();
        var failedRow = await rig.Ui(() => (rig.Panel.Items.Single().HasCommitError, Retry: rig.Panel.Items.Single().RetryCommitCommand.CanExecute(null), rig.Panel.Items.Single().HasCommitId));
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
        var fixedRow = await rig.Ui(() => (rig.Panel.Items.Single().HasCommitError, rig.Panel.Items.Single().CommitShort));
        Assert.False(fixedRow.HasCommitError);
        Assert.Equal(head[..7], fixedRow.CommitShort);
    }

    [Fact]
    public async Task Reopening_a_committed_step_clears_its_sha_and_finishing_it_again_commits_the_new_changes()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.AttachAsync();
        await rig.TickAsync(item.Id);
        var first = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitId is not null);
        await rig.Committer.WaitForCompletedAsync(1);

        await rig.TickAsync(item.Id, done: false);
        var reopened = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.Status == TodoStatus.Pending);
        await rig.RefreshAsync();
        var reopenedRow = await rig.Ui(() => (rig.Panel.Items.Single().HasCommitId, rig.Panel.Items.Single().HasCommitError));

        Assert.Null(reopened.CommitId);
        Assert.Null(reopened.CommitError);
        Assert.Equal((false, false), reopenedRow);

        rig.Repo.WriteFile("more.txt", "more\n");
        await rig.TickAsync(item.Id);
        var second = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitId is not null);
        await rig.Committer.WaitForCompletedAsync(2);

        Assert.NotEqual(first.CommitId, second.CommitId);
        Assert.Equal(await rig.Repo.HeadAsync(), second.CommitId);
        Assert.Equal(["A\tmore.txt"], await rig.Repo.LastCommitChangesAsync());
    }

    [Fact]
    public async Task Finishing_a_reopened_step_with_nothing_new_to_commit_records_no_commit()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.AttachAsync();
        await rig.TickAsync(item.Id);
        await rig.WaitForStoredAsync(item.Id, static candidate => candidate.CommitId is not null);
        await rig.Committer.WaitForCompletedAsync(1);
        var count = await rig.Repo.CommitCountAsync();

        await rig.TickAsync(item.Id, done: false);
        await rig.WaitForStoredAsync(item.Id, static candidate => candidate.Status == TodoStatus.Pending);
        await rig.TickAsync(item.Id);
        await rig.Committer.WaitForCompletedAsync(2);
        var stored = await rig.WaitForStoredAsync(item.Id, static candidate => candidate.Status == TodoStatus.Done);

        Assert.Equal(count, await rig.Repo.CommitCountAsync());
        Assert.Null(stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task The_commit_takes_everything_uncommitted_in_the_worktree_not_only_the_agents_files()
    {
        await using var rig = await EndToEndRig.CreateAsync();
        var item = await rig.AddAsync("Manual step");
        rig.Repo.WriteFile("seed.txt", "edited by hand\n");
        rig.Repo.WriteFile("new/dir/file.txt", "new\n");
        await rig.AttachAsync();

        await rig.TickAsync(item.Id);
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

        public Task<TodoItem> AddAsync(string title, TodoStepFields? step = null)
        {
            return AddCoreAsync();

            async Task<TodoItem> AddCoreAsync()
            {
                var added = await Harness.Store.AddAsync(StepCommitHarness.SessionId, title, null, null, CancellationToken.None);
                if (step is null)
                {
                    return added;
                }

                return await Harness.Store.UpdateAsync(added.Id, null, null, null, step, CancellationToken.None)
                    ?? throw new InvalidOperationException("the item vanished");
            }
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

        /// <summary>Sets the row's check box after refreshing the panel, so the row mirrors the store (see the other rig).</summary>
        public Task TickAsync(string id, bool done = true) =>
            Ui(async () =>
            {
                await Panel.RefreshAsync(CancellationToken.None);
                Panel.Items.Single(entry => entry.Id == id).IsDone = done;
            });

        /// <summary>Runs the row's retry command to its end (the committer wrapper tells when the commit itself finished).</summary>
        public async Task RetryAsync(string id)
        {
            var calls = Committer.Started;
            await Ui(
                async () =>
                {
                    var command = Panel.Items.Single(entry => entry.Id == id).RetryCommitCommand;
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

    /// <summary>Forwards to the real committer and counts started and finished calls.</summary>
    private sealed class ObservingCommitter(IStepCommitter inner) : IStepCommitter
    {
        private int started;
        private int completed;

        public int Started => Volatile.Read(ref started);

        public async Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            Interlocked.Increment(ref started);
            try
            {
                return await inner.CommitStepAsync(item, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref completed);
            }
        }

        public async Task WaitForCompletedAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (Volatile.Read(ref completed) < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("timed out waiting for " + count + " commit(s) to finish");
                }

                await Task.Delay(15);
            }
        }
    }
}
