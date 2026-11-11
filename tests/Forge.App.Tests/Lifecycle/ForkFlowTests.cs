using Forge.Abstractions;
using Forge.App.Lifecycle;
using Forge.App.Localization;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Lifecycle;

/// <summary>
/// The parallel fork (TASK-session-worktree-lifecycle 6.5) against a real <c>forge.db</c> and a scripted git: the start
/// point is the live HEAD (never the stored branch) and is read after a "commit first" commit, a detached / unborn /
/// non-repository source refuses, a busy source is never committed under, the three context modes land as specified
/// with the fork relation and the lineage root (never <c>parent_session_id</c>), the sparse configuration follows, and
/// every failure after the worktree exists takes the worktree - and a half-made session row - back again.
/// </summary>
public sealed class ForkFlowTests : IAsyncLifetime
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string Committed = "89abcdef0123456789abcdef0123456789abcdef";

    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-fork-" + Guid.NewGuid().ToString("N"));
    private SqliteSessionStore store = null!;

    private string Repository => Path.Combine(root, "repo");

    private string ForkDirectory => Path.Combine(root, "fork");

    private ProjectInfo Project => new("proj-1", "repo", Repository, ProjectKind.Repository, 0, DateTimeOffset.UtcNow);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(ForkDirectory);
        store = new SqliteSessionStore(root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_fork_starts_at_the_live_head_and_records_the_live_branch_not_the_stored_one()
    {
        // The session row still says "stored"; the workspace has since moved to "live".
        var source = await SourceSessionAsync(branch: "stored");
        var git = new FakeGit(ForkDirectory) { LiveHead = new GitHead(GitHeadKind.Branch, "live") };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Blank), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        var fork = await GetAsync(attempt.Session!.Id);
        Assert.Equal("live", fork.ForkOfBranch);
        Assert.Equal(Head, fork.ForkBaseSha);
        Assert.Equal("forge/fork-a", fork.Branch);
        Assert.Equal(ForkDirectory, fork.WorktreePath);
        Assert.Equal(ForkDirectory, fork.WorkspacePath);
        Assert.Equal(Project.Id, fork.ProjectId);
        Assert.Equal(SessionStatus.Active, fork.Status);
        Assert.Null(fork.ParentSessionId);
        var created = Assert.Single(git.Created);
        Assert.Equal(Repository, created.Repository);
        Assert.Equal("forge/fork-a", created.Branch);
        Assert.Equal(Head, created.StartPoint);
        Assert.Null(created.Sparse);

        // The source session is untouched: still Active in its own workspace.
        Assert.Equal(SessionStatus.Active, (await GetAsync(source.Id)).Status);
    }

    [Fact]
    public async Task Commit_first_commits_in_the_source_sessions_name_and_forks_from_the_new_commit()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt", "b.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Committed, Committed, "ok"),
        };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Blank), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        Assert.Equal(Committed, attempt.CommitSha);
        Assert.Equal(HandoverFlow.CommitPrefix + source.Title, Assert.Single(git.CommitMessages));
        Assert.Equal(Committed, Assert.Single(git.Created).StartPoint);
        Assert.Equal(Committed, (await GetAsync(attempt.Session!.Id)).ForkBaseSha);
    }

    [Fact]
    public async Task Fork_anyway_commits_nothing_and_starts_at_the_last_commit()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory) { Changes = ["a.txt"] };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Blank), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        Assert.Null(attempt.CommitSha);
        Assert.Empty(git.CommitMessages);
        Assert.Equal(Head, Assert.Single(git.Created).StartPoint);
    }

    [Fact]
    public async Task A_source_that_became_busy_is_not_committed_under_and_nothing_is_forked()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory) { Changes = ["a.txt"] };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Blank), static () => false, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(ForkStrings.BusyNow(source.Title), attempt.Error);
        Assert.Empty(git.CommitMessages);
        Assert.Empty(git.Created);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task A_failed_commit_forks_nothing()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Failed, null, "hook rejected"),
        };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Blank), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(ForkStrings.CommitFailed("hook rejected"), attempt.Error);
        Assert.Null(attempt.CommitSha);
        Assert.Empty(git.Created);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Theory]
    [InlineData(GitHeadKind.Detached)]
    [InlineData(GitHeadKind.UnbornBranch)]
    [InlineData(GitHeadKind.NotRepository)]
    public async Task A_source_without_a_branch_with_commits_refuses(GitHeadKind kind)
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory) { LiveHead = new GitHead(kind, kind == GitHeadKind.UnbornBranch ? "main" : null) };
        var expected = kind switch
        {
            GitHeadKind.Detached => ForkStrings.Detached,
            GitHeadKind.UnbornBranch => ForkStrings.Unborn,
            _ => ForkStrings.NotRepository,
        };
        var flow = Flow(git);

        var preparation = await flow.PrepareAsync(Source(source), default);
        var attempt = await flow.ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Blank), Idle, default);

        Assert.False(preparation.CanFork);
        Assert.Equal(expected, preparation.Error);
        Assert.False(attempt.Succeeded);
        Assert.Equal(expected, attempt.Error);
        Assert.Empty(git.Created);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task Preparation_reports_the_live_branch_the_head_the_changes_and_the_sparse_configuration()
    {
        var sparse = new SparseCheckout(["src"]);
        var git = new FakeGit(ForkDirectory)
        {
            LiveHead = new GitHead(GitHeadKind.Branch, "live"),
            Changes = ["a.txt", "b.txt"],
            Sparse = sparse,
        };

        var preparation = await Flow(git).PrepareAsync(new ForkSource(Project, Repository, null), default);

        Assert.True(preparation.CanFork);
        Assert.Equal("live", preparation.Branch);
        Assert.Equal(Head, preparation.StartCommit);
        Assert.Equal(2, preparation.Uncommitted.Count);
        Assert.Same(sparse, preparation.Sparse);
    }

    [Fact]
    public async Task A_failed_read_of_the_source_is_reported_and_forks_nothing()
    {
        var git = new FakeGit(ForkDirectory) { ReadFailure = new IOException("locked") };

        var preparation = await Flow(git).PrepareAsync(new ForkSource(Project, Repository, null), default);

        Assert.False(preparation.CanFork);
        Assert.Equal(ForkStrings.ReadFailed("locked"), preparation.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad..name")]
    public async Task An_invalid_branch_name_is_refused_before_anything_happens(string name)
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory) { Changes = ["a.txt"], InvalidBranches = { "bad..name" } };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice(name, true, HandoverContextMode.Blank), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(ForkStrings.BranchRequired, attempt.Error);
        Assert.Empty(git.CommitMessages);
        Assert.Empty(git.Created);
    }

    [Fact]
    public async Task The_sparse_configuration_of_the_source_is_used_for_the_new_worktree()
    {
        var source = await SourceSessionAsync();
        var sparse = new SparseCheckout(["src", "docs"]);
        var git = new FakeGit(ForkDirectory) { Sparse = sparse };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Blank), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        Assert.Same(sparse, Assert.Single(git.Created).Sparse);
    }

    [Fact]
    public async Task Full_mode_inherits_the_source_by_reference_and_appends_the_fork_note()
    {
        var source = await SourceSessionAsync();
        await store.AppendAsync(source.Id, ChatMessage.User("first"), null, default);
        var last = await store.AppendAsync(source.Id, ChatMessage.User("second"), null, default);
        var git = new FakeGit(ForkDirectory);

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Full), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        var fork = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Full, fork.Origin);
        Assert.Equal(source.Id, fork.InheritsFromSessionId);
        Assert.Equal(last, fork.InheritsUpToSeq);
        Assert.Equal(source.Id, fork.LineageRootId);
        Assert.Null(fork.ParentSessionId);
        Assert.Equal(InheritanceNote.BuildFork("main", "forge/fork-a", Head, null), Assert.Single(await OwnMessagesAsync(fork.Id)));

        // The source's own log is not copied or changed.
        Assert.Equal(last, await store.LastSeqAsync(source.Id, default));
    }

    [Fact]
    public async Task A_fork_of_a_fork_keeps_the_original_lineage_root()
    {
        var first = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory);
        var flow = Flow(git);
        var child = await flow.ExecuteAsync(Source(first), new ForkChoice("forge/fork-a", false, HandoverContextMode.Full), Idle, default);
        Assert.True(child.Succeeded, child.Error);
        var second = Path.Combine(root, "fork-2");
        Directory.CreateDirectory(second);
        git.ForkPath = second;

        var grandchild = await flow.ExecuteAsync(
            new ForkSource(Project, ForkDirectory, child.Session),
            new ForkChoice("forge/fork-b", false, HandoverContextMode.Blank),
            Idle,
            default);

        Assert.True(grandchild.Succeeded, grandchild.Error);
        Assert.Equal(first.Id, (await GetAsync(grandchild.Session!.Id)).LineageRootId);
    }

    [Fact]
    public async Task Summary_mode_starts_with_one_summary_message_generated_from_the_source_history()
    {
        var source = await SourceSessionAsync();
        await store.AppendAsync(source.Id, ChatMessage.User("first"), null, default);
        IReadOnlyList<ChatMessage>? seen = null;
        string? described = null;
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Committed, Committed, "ok"),
        };
        var flow = Flow(git, (history, description, _) =>
        {
            seen = history;
            described = description;
            return Task.FromResult("the summary");
        });

        var attempt = await flow.ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Summary), Idle, default);

        Assert.True(attempt.Succeeded, attempt.Error);
        var fork = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Summary, fork.Origin);
        Assert.Null(fork.InheritsFromSessionId);
        Assert.Equal(source.Id, fork.LineageRootId);
        Assert.Equal("the summary", Assert.Single(await OwnMessagesAsync(fork.Id)));
        Assert.Single(seen!);
        Assert.Equal(ForkFlow.DescribeFork("main", "forge/fork-a", Committed, Committed), described);
    }

    [Fact]
    public async Task A_failed_summary_creates_no_worktree_and_names_the_commit_that_landed()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Committed, Committed, "ok"),
        };
        var flow = Flow(git, static (_, _, _) => throw new HandoverSummaryException("model down"));

        var attempt = await flow.ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Summary), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(Committed, attempt.CommitSha);
        Assert.Contains("model down", attempt.Error, StringComparison.Ordinal);
        Assert.Contains(HandoverStrings.CommitLanded(Committed), attempt.Error, StringComparison.Ordinal);
        Assert.Empty(git.Created);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task A_cancelled_summary_is_reported_and_creates_nothing()
    {
        var source = await SourceSessionAsync();
        using var cts = new CancellationTokenSource();
        var git = new FakeGit(ForkDirectory);
        var flow = Flow(git, async (_, _, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
            return "never";
        });

        var attempt = await flow.ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Summary), Idle, cts.Token);

        Assert.False(attempt.Succeeded);
        Assert.Equal(HandoverStrings.SummaryCancelled, attempt.Error);
        Assert.Empty(git.Created);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task A_workspace_without_a_session_forks_blank_whatever_mode_was_asked()
    {
        var git = new FakeGit(ForkDirectory) { Changes = ["a.txt"] };

        var attempt = await Flow(git).ExecuteAsync(
            new ForkSource(Project, Repository, null),
            new ForkChoice("forge/fork-a", true, HandoverContextMode.Full),
            Idle,
            default);

        Assert.True(attempt.Succeeded, attempt.Error);
        var fork = await GetAsync(attempt.Session!.Id);
        Assert.Equal(SessionOrigin.Blank, fork.Origin);
        Assert.Null(fork.InheritsFromSessionId);
        // No source session: the fork starts its own lineage (the store makes it its own root).
        Assert.Equal(fork.Id, fork.LineageRootId);
        Assert.Equal("main", fork.ForkOfBranch);
        Assert.Empty(await OwnMessagesAsync(fork.Id));

        // No session to commit in the name of: nothing is committed.
        Assert.Empty(git.CommitMessages);
    }

    [Fact]
    public async Task A_worktree_that_cannot_be_created_is_reported_with_the_commit_that_landed()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Committed, Committed, "ok"),
            CreateFailure = new GitException("branch exists", [], 1, string.Empty),
        };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Blank), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(Committed, attempt.CommitSha);
        Assert.StartsWith(ForkStrings.CreateFailed("branch exists"), attempt.Error, StringComparison.Ordinal);
        Assert.Contains(HandoverStrings.CommitLanded(Committed), attempt.Error, StringComparison.Ordinal);
        Assert.Empty(git.Removed);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Fact]
    public async Task A_cancel_while_the_worktree_is_created_names_the_commit_and_leaves_no_session()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory)
        {
            Changes = ["a.txt"],
            Commit = new StepCommitResult(StepCommitOutcome.Committed, Committed, "ok"),
            CreateFailure = new OperationCanceledException(),
        };

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Blank), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(Committed, attempt.CommitSha);
        Assert.StartsWith(ForkStrings.Cancelled, attempt.Error, StringComparison.Ordinal);
        Assert.Contains(HandoverStrings.CommitLanded(Committed), attempt.Error, StringComparison.Ordinal);
        Assert.Empty(git.Removed);
        Assert.Single(await store.ListSessionsAsync(null, default));
    }

    [Theory]
    [InlineData(SessionStatus.Archived)]
    [InlineData(SessionStatus.Deleted)]
    public async Task A_source_session_that_is_no_longer_active_is_refused_before_anything_is_read_or_created(SessionStatus status)
    {
        var source = await SourceSessionAsync();
        await store.SetStatusAsync(source.Id, status, default);
        var git = new FakeGit(ForkDirectory) { Changes = ["a.txt"] };

        var preparation = await Flow(git).PrepareAsync(Source(source), default);
        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", true, HandoverContextMode.Full), Idle, default);

        Assert.Equal(ForkStrings.SourceChanged(source.Title), preparation.Error);
        Assert.False(attempt.Succeeded);
        Assert.Equal(ForkStrings.SourceChanged(source.Title), attempt.Error);
        Assert.Empty(git.CommitMessages);
        Assert.Empty(git.Created);
    }

    [Fact]
    public async Task A_session_row_that_cannot_be_written_takes_the_new_worktree_back()
    {
        var source = await SourceSessionAsync();

        // The directory git hands back already has an active session: the conditional insert refuses (I1).
        var holder = await ((ISessionCatalog)store).CreateAsync(new SessionCreate(ForkDirectory, "holder") { WorktreePath = ForkDirectory }, default);
        var git = new FakeGit(ForkDirectory);

        var attempt = await Flow(git).ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Blank), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.NotNull(attempt.Error);
        Assert.Equal(ForkDirectory, Assert.Single(git.Removed).Path);
        Assert.Equal(2, (await store.ListSessionsAsync(null, default)).Count);
        Assert.Equal(SessionStatus.Active, (await GetAsync(holder.Id)).Status);
    }

    [Fact]
    public async Task A_first_message_that_cannot_be_written_takes_the_session_row_and_the_worktree_back()
    {
        var source = await SourceSessionAsync();
        var git = new FakeGit(ForkDirectory);
        var flow = new ForkFlow(store, new FailingAppendStore(store), git, static (_, _, _) => Task.FromResult("summary"));

        var attempt = await flow.ExecuteAsync(Source(source), new ForkChoice("forge/fork-a", false, HandoverContextMode.Full), Idle, default);

        Assert.False(attempt.Succeeded);
        Assert.Equal(ForkStrings.SessionFailed("disk full"), attempt.Error);
        Assert.Equal(ForkDirectory, Assert.Single(git.Removed).Path);

        // The half-made row is purged, not left Active or in history.
        Assert.Equal(source.Id, Assert.Single(await store.ListSessionsAsync(null, default)).Id);
        Assert.Empty(await ((ISessionCatalog)store).ListHistoryAsync(new SessionQuery(), default));
    }

    [Fact]
    public void The_default_branch_name_carries_the_worktree_prefix()
    {
        var name = ForkFlow.DefaultBranchName("forge/");

        Assert.StartsWith("forge/fork-", name, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', name);
    }

    [Fact]
    public void A_busy_source_only_offers_fork_anyway()
    {
        var model = new ForkDialogModel("main", Head, "source", 3, sourceBusy: true, isSparse: false, "forge/fork-a", HandoverContextMode.Summary, NeverRun);

        Assert.True(model.HasUncommitted);
        Assert.False(model.OffersCommitFirst);
        Assert.False(model.CommitFirst);

        model.CommitFirst = true;

        Assert.False(model.CommitFirst);
        Assert.True(model.OffersContextMode);
    }

    [Fact]
    public void An_idle_source_with_changes_defaults_to_commit_first()
    {
        var model = new ForkDialogModel("main", Head, "source", 3, sourceBusy: false, isSparse: true, "forge/fork-a", HandoverContextMode.Full, NeverRun);

        Assert.True(model.OffersCommitFirst);
        Assert.True(model.CommitFirst);
        Assert.Equal(HandoverContextMode.Full, model.Mode);
        Assert.True(model.IsSparse);
    }

    [Fact]
    public void A_workspace_without_a_session_offers_neither_commit_first_nor_a_context_mode()
    {
        var model = new ForkDialogModel("main", Head, null, 3, sourceBusy: false, isSparse: false, "forge/fork-a", HandoverContextMode.Summary, NeverRun);

        Assert.False(model.OffersCommitFirst);
        Assert.False(model.CommitFirst);
        Assert.False(model.OffersContextMode);
        Assert.Equal(HandoverContextMode.Blank, model.Mode);

        model.Mode = HandoverContextMode.Full;

        Assert.Equal(HandoverContextMode.Blank, model.Mode);
    }

    [Fact]
    public async Task The_dialog_model_passes_the_choice_keeps_the_error_and_allows_a_retry()
    {
        var choices = new List<ForkChoice>();
        var model = new ForkDialogModel("main", Head, "source", 1, sourceBusy: false, isSparse: false, "forge/fork-a", HandoverContextMode.Summary, (choice, _) =>
        {
            choices.Add(choice);
            return Task.FromResult(choices.Count == 1 ? "boom" : null);
        });
        model.BranchName = "forge/renamed";
        model.CommitFirst = false;
        model.Mode = HandoverContextMode.Full;

        Assert.False(await model.ExecuteAsync());
        Assert.Equal("boom", model.Error);
        Assert.False(model.Succeeded);

        Assert.True(await model.ExecuteAsync());
        Assert.Null(model.Error);
        Assert.True(model.Succeeded);
        Assert.Equal(2, choices.Count);
        Assert.Equal(new ForkChoice("forge/renamed", false, HandoverContextMode.Full), choices[0]);
    }

    private static bool Idle() => true;

    private static Task<string?> NeverRun(ForkChoice choice, CancellationToken ct) =>
        throw new Xunit.Sdk.XunitException("the fork must not run here");

    private ForkFlow Flow(FakeGit git, Func<IReadOnlyList<ChatMessage>, string?, CancellationToken, Task<string>>? summarize = null) =>
        new(store, store, git, summarize ?? (static (_, _, _) => Task.FromResult("summary")));

    private ForkSource Source(SessionInfo session) => new(Project, Repository, session);

    private Task<SessionInfo> SourceSessionAsync(string branch = "main") =>
        ((ISessionCatalog)store).CreateAsync(new SessionCreate(Repository, "source") { ProjectId = Project.Id, Branch = branch }, default);

    private async Task<SessionInfo> GetAsync(string id) =>
        await store.GetSessionAsync(id, default) ?? throw new Xunit.Sdk.XunitException($"session {id} is missing");

    /// <summary>The messages stored in the session's own log (inherited history is a reference, not a copy).</summary>
    private async Task<List<string>> OwnMessagesAsync(string id)
    {
        var texts = new List<string>();
        await foreach (var message in store.ReadAsync(id, 0, null, default))
        {
            texts.Add(string.Concat(message.Message.Content.OfType<TextBlock>().Select(static block => block.Text)));
        }

        return texts;
    }

    private sealed record CreatedWorktree(string Repository, string Title, string Branch, string StartPoint, SparseCheckout? Sparse);

    private sealed class FakeGit(string forkPath) : IForkGit
    {
        private bool committed;

        public string ForkPath { get; set; } = forkPath;

        public GitHead LiveHead { get; init; } = new(GitHeadKind.Branch, "main");

        public IReadOnlyList<string>? Changes { get; init; } = [];

        public StepCommitResult Commit { get; init; } = new(StepCommitOutcome.NoChanges, null, string.Empty);

        public SparseCheckout? Sparse { get; init; }

        public Exception? ReadFailure { get; init; }

        public Exception? CreateFailure { get; init; }

        public HashSet<string> InvalidBranches { get; } = new(StringComparer.Ordinal);

        public List<string> CommitMessages { get; } = [];

        public List<CreatedWorktree> Created { get; } = [];

        public List<WorktreeSession> Removed { get; } = [];

        public Task<IReadOnlyList<string>?> UncommittedAsync(string directory, CancellationToken ct) =>
            Task.FromResult(committed ? [] : Changes);

        /// <summary>The commit HEAD is at: the scripted commit once it landed, so "after the commit" is observable.</summary>
        public Task<string?> HeadAsync(string directory, CancellationToken ct) =>
            Task.FromResult<string?>(LiveHead.Kind == GitHeadKind.Branch ? (committed ? Commit.CommitId : Head) : null);

        public Task<StepCommitResult> CommitAsync(string directory, string message, CancellationToken ct)
        {
            CommitMessages.Add(message);
            committed = Commit.Outcome == StepCommitOutcome.Committed;
            return Task.FromResult(Commit);
        }

        public Task<GitHead> ReadHeadAsync(string directory, CancellationToken ct) =>
            ReadFailure is { } failure ? Task.FromException<GitHead>(failure) : Task.FromResult(LiveHead);

        public Task<SparseCheckout?> SparseAsync(string directory, CancellationToken ct) => Task.FromResult(Sparse);

        public Task<bool> IsValidBranchNameAsync(string repositoryRoot, string name, CancellationToken ct) =>
            Task.FromResult(!InvalidBranches.Contains(name));

        public Task<WorktreeSession> CreateWorktreeAsync(
            string repositoryRoot,
            string title,
            string branchName,
            string startPoint,
            SparseCheckout? sparse,
            CancellationToken ct)
        {
            if (CreateFailure is { } failure)
            {
                return Task.FromException<WorktreeSession>(failure);
            }

            Created.Add(new CreatedWorktree(repositoryRoot, title, branchName, startPoint, sparse));
            return Task.FromResult(new WorktreeSession(repositoryRoot, ForkPath, branchName, "slug", startPoint));
        }

        public Task RemoveCreatedAsync(WorktreeSession worktree, CancellationToken ct)
        {
            Removed.Add(worktree);
            return Task.CompletedTask;
        }
    }

    /// <summary>The real store, except that the first message of a new session cannot be written.</summary>
    private sealed class FailingAppendStore(ISessionStore inner) : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            inner.CreateSessionAsync(workspacePath, title, ct);

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => inner.GetSessionAsync(sessionId, ct);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            inner.ListSessionsAsync(workspacePath, ct);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromException<long>(new IOException("disk full"));

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            inner.ReadAsync(sessionId, fromSeq, toSeq, ct);

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => inner.LastSeqAsync(sessionId, ct);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => inner.PutSummaryAsync(summary, ct);

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            inner.GetSummariesAsync(sessionId, ct);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            inner.SearchAsync(sessionId, query, limit, ct);
    }
}
