using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// Steps 4-7 of the handover (TASK-session-worktree-lifecycle 6.2) against a real <c>forge.db</c> and a scripted git:
/// a failed commit never switches, a switch that fails after a commit names the commit, a failed or cancelled summary
/// keeps the commit (a retry does not commit again), the three context modes land as specified, todos follow only the
/// inheriting modes, and a failed review copy only warns.
/// </summary>
public sealed class HandoverFlowTests : IAsyncLifetime
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private readonly string root = TestTempFiles.NewDirectory("forge-handover-");
    private SqliteSessionStore store = null!;
    private SqliteTodoStore todos = null!;

    private string Worktree => Path.Combine(root, "wt");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Worktree);
        store = new SqliteSessionStore(root);
        todos = new SqliteTodoStore(root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await todos.DisposeAsync();
        await store.DisposeAsync();
        TestTempFiles.DeleteQuietly(root);
    }

    [Fact]
    public async Task A_failed_handover_commit_does_not_switch()
    {
        var old = await OldSessionAsync();
        var git = new FakeGit { Changes = ["a.txt"], Commit = new StepCommitResult(StepCommitOutcome.Failed, null, "hook rejected") };
        var flow = Flow(git);

        var attempt = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(HandoverStrings.CommitFailed("hook rejected"), attempt.Error);
        Assert.Null(attempt.CommitSha);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
        Assert.Single(await store.ListSessionsAsync(null, default));
        Assert.Equal(HandoverFlow.CommitPrefix + old.Title, Assert.Single(git.CommitMessages));
    }

    [Fact]
    public async Task A_skipped_commit_is_a_failure_too()
    {
        var old = await OldSessionAsync();
        var git = new FakeGit { Changes = ["a.txt"], Commit = new StepCommitResult(StepCommitOutcome.Skipped, null, "detached") };

        var attempt = await Flow(git).ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
    }

    [Fact]
    public async Task A_switch_that_fails_after_the_commit_names_the_commit()
    {
        var old = await OldSessionAsync();
        await ((ISessionCatalog)store).SetStatusAsync(old.Id, SessionStatus.Archived, default);
        var git = new FakeGit { Changes = ["a.txt"], Commit = new StepCommitResult(StepCommitOutcome.Committed, Sha, "ok") };

        var attempt = await Flow(git).ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(Sha, attempt.CommitSha);
        Assert.Contains(Sha[..10], attempt.Error, StringComparison.Ordinal);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task A_failed_summary_keeps_the_commit_and_a_retry_does_not_commit_again()
    {
        var old = await OldSessionAsync();
        var git = new FakeGit { Changes = ["a.txt"], Commit = new StepCommitResult(StepCommitOutcome.Committed, Sha, "ok") };
        var flow = Flow(git, summarize: static (_, _, _) => throw new HandoverSummaryException("model down"));

        var failed = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Summary), default);

        Assert.False(failed.Succeeded);
        Assert.Equal(Sha, failed.CommitSha);
        Assert.Contains("model down", failed.Error, StringComparison.Ordinal);
        Assert.Contains(HandoverStrings.CommitLanded(Sha), failed.Error, StringComparison.Ordinal);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
        Assert.Single(git.CommitMessages);

        git.Changes = []; // the commit landed: the tree is clean now
        var retried = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.True(retried.Succeeded);
        Assert.Single(git.CommitMessages);
        Assert.Equal(SessionStatus.Archived, (await GetAsync(old.Id)).Status);
        Assert.Equal(SessionStatus.Active, retried.Session!.Status);
    }

    [Fact]
    public async Task A_cancelled_summary_is_reported_and_changes_nothing()
    {
        var old = await OldSessionAsync();
        using var cts = new CancellationTokenSource();
        var flow = Flow(new FakeGit(), summarize: async (_, _, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return "never";
        });

        var attempt = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Summary), cts.Token);

        Assert.False(attempt.Succeeded);
        Assert.Equal(HandoverStrings.SummaryCancelled, attempt.Error);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task Summary_mode_starts_the_new_session_with_one_summary_message_and_copies_todos()
    {
        var old = await OldSessionAsync();
        await store.AppendAsync(old.Id, ChatMessage.User("first"), null, default);
        await store.AppendAsync(old.Id, ChatMessage.Assistant("reply"), null, default);
        await todos.AddAsync(new TodoAddRequest(old.Id, "step"), default);
        IReadOnlyList<ChatMessage>? seen = null;
        string? tree = null;
        var git = new FakeGit { Changes = ["a.txt", "b.txt"] };
        var flow = Flow(git, summarize: (history, description, _) =>
        {
            seen = history;
            tree = description;
            return Task.FromResult("the summary");
        });

        var attempt = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CarryToNew, HandoverContextMode.Summary), default);

        Assert.True(attempt.Succeeded);
        Assert.Empty(git.CommitMessages);
        Assert.Equal(["first", "reply"], seen!.Select(Text));
        Assert.Contains("- a.txt", tree, StringComparison.Ordinal);
        Assert.Contains("- b.txt", tree, StringComparison.Ordinal);
        var created = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Summary, created.Origin);
        Assert.Null(created.InheritsFromSessionId);
        Assert.Equal(["the summary"], await MessagesAsync(created.Id));
        Assert.Equal("step", Assert.Single(await todos.ListAsync(created.Id, default)).Title);
        Assert.Equal(SessionStatus.Archived, (await GetAsync(old.Id)).Status);
    }

    [Fact]
    public async Task Full_mode_inherits_the_old_history_with_one_note_and_copies_todos()
    {
        var old = await OldSessionAsync();
        await store.AppendAsync(old.Id, ChatMessage.User("first"), null, default);
        await store.AppendAsync(old.Id, ChatMessage.Assistant("reply"), null, default);
        await todos.AddAsync(new TodoAddRequest(old.Id, "step"), default);
        var git = new FakeGit { Changes = ["a.txt"], Commit = new StepCommitResult(StepCommitOutcome.Committed, Sha, "ok"), Head = Sha };

        var attempt = await Flow(git).ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Full), default);

        Assert.True(attempt.Succeeded);
        Assert.Equal(Sha, attempt.CommitSha);
        var created = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Full, created.Origin);
        Assert.Equal(old.Id, created.InheritsFromSessionId);
        Assert.Equal(2, created.InheritsUpToSeq);
        var note = Assert.Single(await MessagesAsync(created.Id));
        Assert.Equal(Text(InheritanceNote.Message(Sha, Sha, false)), note);
        Assert.Single(await todos.ListAsync(created.Id, default));
    }

    [Fact]
    public async Task Blank_mode_starts_empty_without_todos()
    {
        var old = await OldSessionAsync();
        await store.AppendAsync(old.Id, ChatMessage.User("first"), null, default);
        await todos.AddAsync(new TodoAddRequest(old.Id, "step"), default);

        var attempt = await Flow(new FakeGit()).ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.True(attempt.Succeeded);
        var created = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Blank, created.Origin);
        Assert.Null(created.InheritsFromSessionId);
        Assert.Empty(await MessagesAsync(created.Id));
        Assert.Empty(await todos.ListAsync(created.Id, default));
        Assert.Single(await todos.ListAsync(old.Id, default));
    }

    [Fact]
    public async Task A_failed_review_copy_only_warns()
    {
        var old = await OldSessionAsync();
        var flow = Flow(new FakeGit(), copyReviews: static (_, _, _, _) => throw new IOException("disk full"));

        var attempt = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.True(attempt.Succeeded);
        Assert.Equal(HandoverStrings.ReviewCopyFailed("disk full"), attempt.Warning);
        Assert.Equal(SessionStatus.Archived, (await GetAsync(old.Id)).Status);
    }

    [Fact]
    public async Task Reviews_are_copied_to_a_new_session_but_not_on_resume()
    {
        var old = await OldSessionAsync();
        var copies = new List<(string From, string To)>();
        var flow = Flow(new FakeGit(), copyReviews: (_, from, to, _) =>
        {
            copies.Add((from, to));
            return Task.CompletedTask;
        });

        var created = await flow.ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);
        Assert.Equal((old.Id, created.Session!.Id), Assert.Single(copies));

        var resumed = await flow.ExecuteAsync(created.Session, HandoverIntent.Resume(old.Id), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Summary), default);

        Assert.True(resumed.Succeeded);
        Assert.Equal(old.Id, resumed.Session!.Id);
        Assert.Single(copies);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
        Assert.Equal(SessionStatus.Archived, (await GetAsync(created.Session.Id)).Status);
    }

    [Fact]
    public async Task A_failed_status_read_does_not_switch()
    {
        var old = await OldSessionAsync();
        var git = new FakeGit { StatusFailure = new IOException("locked") };

        var attempt = await Flow(git).ExecuteAsync(old, NewIntent(), new HandoverChoice(UncommittedChoice.CommitAsOld, HandoverContextMode.Blank), default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(HandoverStrings.StatusFailed("locked"), attempt.Error);
        Assert.Equal(SessionStatus.Active, (await GetAsync(old.Id)).Status);
    }

    [Fact]
    public async Task The_dialog_model_keeps_the_error_and_allows_a_retry()
    {
        var calls = 0;
        var model = new HandoverDialogModel("old", 2, isRootCheckout: true, offersContextMode: true, (_, _) =>
            Task.FromResult(++calls == 1 ? "boom" : null));

        Assert.Equal(UncommittedChoice.CommitAsOld, model.Uncommitted);
        Assert.Equal(HandoverContextMode.Summary, model.Mode);
        Assert.True(model.HasUncommitted);

        Assert.False(await model.ExecuteAsync());
        Assert.Equal("boom", model.Error);
        Assert.False(model.Succeeded);

        Assert.True(await model.ExecuteAsync());
        Assert.Null(model.Error);
        Assert.True(model.Succeeded);
        Assert.Equal(2, calls);
    }

    private HandoverFlow Flow(
        FakeGit git,
        Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>>? summarize = null,
        Func<string, string, string, CancellationToken, Task>? copyReviews = null) =>
        new(store, store, git, summarize ?? (static (_, _, _) => Task.FromResult("summary")), copyReviews);

    private HandoverIntent NewIntent() => HandoverIntent.NewSession(new SessionCreate(Worktree, "new"));

    private Task<SessionInfo> OldSessionAsync() => ((ISessionCatalog)store).CreateAsync(new SessionCreate(Worktree, "old"), default);

    private async Task<SessionInfo> GetAsync(string id) =>
        await store.GetSessionAsync(id, default) ?? throw new Xunit.Sdk.XunitException($"session {id} is missing");

    private async Task<List<string>> MessagesAsync(string id)
    {
        var texts = new List<string>();
        await foreach (var message in store.ReadAsync(id, 0, null, default))
        {
            texts.Add(Text(message.Message));
        }

        return texts;
    }

    private static string Text(ChatMessage message) =>
        string.Concat(message.Content.OfType<TextBlock>().Select(static block => block.Text));

    private sealed class FakeGit : IHandoverGit
    {
        public IReadOnlyList<string>? Changes { get; set; } = [];

        public Exception? StatusFailure { get; init; }

        public StepCommitResult Commit { get; init; } = new(StepCommitOutcome.NoChanges, null, string.Empty);

        public string? Head { get; init; } = Sha;

        public List<string> CommitMessages { get; } = [];

        public Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct) =>
            StatusFailure is { } failure ? Task.FromException<IReadOnlyList<string>?>(failure) : Task.FromResult(Changes);

        public Task<string?> HeadAsync(string directory, CancellationToken ct) => Task.FromResult(Head);

        public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct)
        {
            CommitMessages.Add(message);
            return Task.FromResult(Commit);
        }
    }
}
