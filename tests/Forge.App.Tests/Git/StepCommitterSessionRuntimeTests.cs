using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Git;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The agent side of step commits, end to end: a real <see cref="SessionRuntime"/> over a scripted model, the real
/// <c>todo</c> tool, a real <see cref="Forge.Core.Storage.SqliteTodoStore"/> store and a real <see cref="StepCommitter"/> over a temporary git
/// repository. The model "calls" the tool, the tool commits, and the assertions read the tool result the model would see,
/// the commit in the repository and the item in the store. A runtime without a committer (the unit-test assembly) commits
/// nothing, which is the control that proves the wiring and not the tool does the work.
/// </summary>
[Collection("forge-app-ui")]
public sealed class StepCommitterSessionRuntimeTests
{
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task Marking_a_step_done_commits_it_with_the_subject_and_summary_and_records_the_sha()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        rig.Repo.WriteFile("docs/notes.md", "notes\n");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(
            new JsonObject
            {
                ["operation"] = "update",
                ["id"] = item.Id,
                ["status"] = "done",
                ["commit_subject"] = "feat: add feature",
                ["summary"] = "Adds the feature.\n\nSecond paragraph.",
                ["verification"] = "dotnet test: 12 passed",
            });

        var head = await rig.Repo.HeadAsync();
        Assert.False(result.IsError, result.Content);
        Assert.Contains("Step commit: committed " + head[..7], result.Content, StringComparison.Ordinal);
        Assert.Equal(before + 1, await rig.Repo.CommitCountAsync());
        var message = await rig.Repo.LastMessageAsync();
        Assert.Equal("feat: add feature\n\nAdds the feature.\n\nSecond paragraph.", message);
        Assert.DoesNotContain("dotnet test", message, StringComparison.Ordinal);
        Assert.Equal(["A\tdocs/notes.md", "A\tfeature.txt"], await rig.Repo.LastCommitChangesAsync());
        Assert.Equal(string.Empty, await rig.Repo.StatusAsync());

        var stored = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Equal(head, stored.CommitId);
        Assert.Null(stored.CommitError);
        Assert.Equal("feat: add feature", stored.CommitSubject);
        Assert.Equal("Adds the feature.\n\nSecond paragraph.", stored.Summary);
        Assert.Equal("dotnet test: 12 passed", stored.Verification);
    }

    [Fact]
    public async Task Without_a_committer_marking_done_commits_nothing()
    {
        await using var rig = await Rig.CreateAsync(withCommitter: false);
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        Assert.Equal("?? feature.txt", await rig.Repo.StatusAsync());
        var stored = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task With_the_preference_off_the_step_is_done_but_not_committed()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Harness.Enabled = false;
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Step commit: skipped", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        Assert.Equal("?? feature.txt", await rig.Repo.StatusAsync());
        var stored = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task A_clean_worktree_reports_no_changes_and_records_no_commit()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Think about it");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Step commit: no changes", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        var stored = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    [Fact]
    public async Task A_session_without_a_worktree_is_skipped_and_the_repository_is_untouched()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Harness.NoWorktree = true;
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Step commit: skipped", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        Assert.Equal("?? feature.txt", await rig.Repo.StatusAsync());
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("pending")]
    public async Task Other_status_changes_never_commit(string status)
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        var before = await rig.Repo.CommitCountAsync();

        var result = await rig.TodoAsync(
            new JsonObject { ["operation"] = "update", ["id"] = item.Id, ["status"] = status });

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        Assert.Equal("?? feature.txt", await rig.Repo.StatusAsync());
    }

    [Fact]
    public async Task Editing_a_note_on_a_finished_step_does_not_commit_again()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.TodoAsync(Done(item.Id));
        var committed = await rig.Repo.CommitCountAsync();
        rig.Repo.WriteFile("later.txt", "later\n");

        var result = await rig.TodoAsync(
            new JsonObject { ["operation"] = "update", ["id"] = item.Id, ["note"] = "reviewed" });

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        Assert.Equal(committed, await rig.Repo.CommitCountAsync());
        Assert.Equal("?? later.txt", await rig.Repo.StatusAsync());
    }

    [Fact]
    public async Task Saying_done_again_about_a_committed_step_does_not_commit_again()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.TodoAsync(Done(item.Id));
        var firstCommit = await rig.Repo.HeadAsync();
        var committed = await rig.Repo.CommitCountAsync();
        rig.Repo.WriteFile("later.txt", "later\n");

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("Step commit", result.Content, StringComparison.Ordinal);
        Assert.Equal(committed, await rig.Repo.CommitCountAsync());
        Assert.Equal(firstCommit, await rig.Repo.HeadAsync());
        Assert.Equal("?? later.txt", await rig.Repo.StatusAsync());
        Assert.Equal(firstCommit, (await rig.Harness.StoredAsync(item.Id)).CommitId);
    }

    [Fact]
    public async Task Reopening_a_committed_step_clears_its_sha_and_finishing_it_again_commits_the_new_changes()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        await rig.TodoAsync(Done(item.Id));
        var firstCommit = await rig.Repo.HeadAsync();
        Assert.Equal(firstCommit, (await rig.Harness.StoredAsync(item.Id)).CommitId);
        var committed = await rig.Repo.CommitCountAsync();

        var reopened = await rig.TodoAsync(
            new JsonObject { ["operation"] = "update", ["id"] = item.Id, ["status"] = "in_progress" });

        Assert.False(reopened.IsError, reopened.Content);
        Assert.DoesNotContain("Step commit", reopened.Content, StringComparison.Ordinal);
        var afterReopen = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.InProgress, afterReopen.Status);
        Assert.Null(afterReopen.CommitId);
        Assert.Null(afterReopen.CommitError);
        Assert.Equal(committed, await rig.Repo.CommitCountAsync());

        rig.Repo.WriteFile("fix.txt", "fix\n");
        var finished = await rig.TodoAsync(Done(item.Id));

        var secondCommit = await rig.Repo.HeadAsync();
        Assert.NotEqual(firstCommit, secondCommit);
        Assert.Contains("Step commit: committed " + secondCommit[..7], finished.Content, StringComparison.Ordinal);
        Assert.Equal(committed + 1, await rig.Repo.CommitCountAsync());
        Assert.Equal(["A\tfix.txt"], await rig.Repo.LastCommitChangesAsync());
        var afterFinish = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, afterFinish.Status);
        Assert.Equal(secondCommit, afterFinish.CommitId);
        Assert.Null(afterFinish.CommitError);
    }

    [Fact]
    public async Task Reopening_a_step_whose_commit_failed_clears_the_failure_and_finishing_it_again_commits()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        rig.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        var before = await rig.Repo.CommitCountAsync();
        await rig.TodoAsync(Done(item.Id));
        Assert.NotNull((await rig.Harness.StoredAsync(item.Id)).CommitError);

        await rig.TodoAsync(
            new JsonObject { ["operation"] = "update", ["id"] = item.Id, ["status"] = "pending" });

        var reopened = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Pending, reopened.Status);
        Assert.Null(reopened.CommitError);
        Assert.Null(reopened.CommitId);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());

        rig.Repo.RemoveHook("pre-commit");
        var finished = await rig.TodoAsync(Done(item.Id));

        var head = await rig.Repo.HeadAsync();
        Assert.Contains("Step commit: committed " + head[..7], finished.Content, StringComparison.Ordinal);
        Assert.Equal(before + 1, await rig.Repo.CommitCountAsync());
        Assert.Equal(head, (await rig.Harness.StoredAsync(item.Id)).CommitId);
    }

    [Fact]
    public async Task Consecutive_steps_each_commit_only_their_own_changes()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await rig.AddAsync("Step one");
        var second = await rig.AddAsync("Step two");

        rig.Repo.WriteFile("one.txt", "1\n");
        var firstResult = await rig.TodoAsync(Done(first.Id));
        var firstSha = await rig.Repo.HeadAsync();

        rig.Repo.WriteFile("two.txt", "2\n");
        var secondResult = await rig.TodoAsync(Done(second.Id));
        var secondSha = await rig.Repo.HeadAsync();

        Assert.Contains("committed " + firstSha[..7] + "\n", firstResult.Content + "\n", StringComparison.Ordinal);
        Assert.Contains("committed " + secondSha[..7], secondResult.Content, StringComparison.Ordinal);
        Assert.NotEqual(firstSha, secondSha);
        Assert.Equal(["A\ttwo.txt"], await rig.Repo.LastCommitChangesAsync());
        Assert.Equal("Step two", await rig.Repo.LastMessageAsync());
        Assert.Equal(firstSha, (await rig.Harness.StoredAsync(first.Id)).CommitId);
        Assert.Equal(secondSha, (await rig.Harness.StoredAsync(second.Id)).CommitId);
    }

    [Fact]
    public async Task A_rejecting_hook_leaves_the_step_done_with_the_reason_and_saying_done_again_retries()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");
        rig.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        var before = await rig.Repo.CommitCountAsync();

        var failed = await rig.TodoAsync(Done(item.Id));

        Assert.False(failed.IsError, failed.Content);
        Assert.Contains("Step commit: FAILED", failed.Content, StringComparison.Ordinal);
        Assert.Contains("hook rejected: lint failed", failed.Content, StringComparison.Ordinal);
        Assert.Equal(before, await rig.Repo.CommitCountAsync());
        var afterFailure = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, afterFailure.Status);
        Assert.Null(afterFailure.CommitId);
        Assert.Contains("hook rejected: lint failed", afterFailure.CommitError, StringComparison.Ordinal);

        rig.Repo.RemoveHook("pre-commit");
        var retried = await rig.TodoAsync(Done(item.Id));

        var head = await rig.Repo.HeadAsync();
        Assert.Contains("Step commit: committed " + head[..7], retried.Content, StringComparison.Ordinal);
        Assert.Equal(before + 1, await rig.Repo.CommitCountAsync());
        var afterRetry = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(head, afterRetry.CommitId);
        Assert.Null(afterRetry.CommitError);
    }

    [Fact]
    public async Task A_failing_worktree_lookup_is_reported_to_the_model_and_recorded_on_the_item()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Harness.ResolveFailure = new IOException("catalog is down");
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");

        var result = await rig.TodoAsync(Done(item.Id));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Step commit: FAILED", result.Content, StringComparison.Ordinal);
        Assert.Contains("catalog is down", result.Content, StringComparison.Ordinal);
        var stored = await rig.Harness.StoredAsync(item.Id);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitId);
        Assert.Contains("catalog is down", stored.CommitError, StringComparison.Ordinal);
        Assert.Equal("?? feature.txt", await rig.Repo.StatusAsync());
    }

    [Fact]
    public async Task The_turn_itself_finishes_normally_when_the_commit_fails()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Repo.InstallHook("pre-commit", "exit 1\n");
        var item = await rig.AddAsync("Add feature");
        rig.Repo.WriteFile("feature.txt", "feature\n");

        await rig.TodoAsync(Done(item.Id));

        Assert.Empty(rig.Events.OfType<AgentFailed>());
        Assert.NotEmpty(rig.Events.OfType<AgentTurnCompleted>());
        Assert.Contains(rig.Events.OfType<AgentTextDelta>(), static delta => delta.Text == Rig.FinalAnswer);
    }

    // -------------------------------------------------------- the composition root's wiring

    /// <summary>
    /// <see cref="AppComposition.Create"/> hands the one committer it also gives the TODO panel to every session runtime,
    /// resolves a session's worktree from its catalog row and reads the live preference. Driven with a real composition
    /// over a throw-away data root and a worktree-backed session row.
    /// </summary>
    [Fact]
    public async Task The_composition_commits_into_the_session_worktree_and_follows_the_preference()
    {
        var session = SharedUi.Session;
        var root = StepCommitRepo.NewRoot();
        try
        {
            using var repo = await StepCommitRepo.CreateAsync(root, "worktree");
            var observed = await session.Dispatch(
                async () =>
                {
                    await using var app = CompositionHarness.Open(root);
                    var composition = app.Composition;
                    using var cts = new CancellationTokenSource(TurnTimeout);

                    var info = await composition.Catalog.CreateAsync(
                        new SessionCreate(repo.Path, "wired") { WorktreePath = repo.Path, Branch = "main" },
                        cts.Token);
                    var added = await composition.Todos.AddAsync(info.Id, "Wire it", null, null, cts.Token);
                    var done = await composition.Todos.UpdateAsync(added.Id, null, TodoStatus.Done, null, cts.Token);
                    repo.WriteFile("wired.txt", "wired\n");

                    var committed = await composition.TodoStepCommitter.CommitStepAsync(done!, cts.Token);
                    var head = await repo.HeadAsync();
                    var storedAfterCommit = (await composition.Todos.ListAsync(info.Id, cts.Token)).Single();

                    composition.Preferences.GitStepCommits = false;
                    var second = await composition.Todos.AddAsync(info.Id, "Switched off", null, null, cts.Token);
                    var secondDone = await composition.Todos.UpdateAsync(second.Id, null, TodoStatus.Done, null, cts.Token);
                    repo.WriteFile("off.txt", "off\n");
                    var skipped = await composition.TodoStepCommitter.CommitStepAsync(secondDone!, cts.Token);

                    var handedToRuntimes = HandedToRuntimes(composition);
                    return (committed, head, storedAfterCommit, skipped, handedToRuntimes, same: ReferenceEquals(handedToRuntimes, composition.TodoStepCommitter));
                },
                CancellationToken.None);

            Assert.Equal(StepCommitOutcome.Committed, observed.committed.Outcome);
            Assert.Equal(observed.head, observed.committed.CommitId);
            Assert.Equal(observed.head, observed.storedAfterCommit.CommitId);
            Assert.Equal("Wire it", await repo.LastMessageAsync());
            Assert.Equal(StepCommitOutcome.Skipped, observed.skipped.Outcome);
            Assert.Equal("?? off.txt", await repo.StatusAsync());
            Assert.NotNull(observed.handedToRuntimes);
            Assert.True(observed.same, "the session runtimes must share the committer the TODO panel uses");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            StepCommitRepo.DeleteTree(root);
        }
    }

    /// <summary>
    /// The runtime services are private to the runtime manager; this is the one reflection hop in the file, the committer
    /// they carry being the thing under test (the same instance the panel gets).
    /// </summary>
    private static IStepCommitter? HandedToRuntimes(AppComposition composition)
    {
        var field = typeof(AgentRuntimeManager).GetField("services", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AgentRuntimeManager.services is gone; update this test.");
        var services = (AgentRuntimeServices)(field.GetValue(composition.Runtimes) ?? throw new InvalidOperationException("no services"));
        return services.StepCommitter;
    }

    private static JsonObject Done(string id) => new() { ["operation"] = "update", ["id"] = id, ["status"] = "done" };

    // ------------------------------------------------------------ rig

    /// <summary>A session runtime, its scripted model and a step-commit harness, for one test.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public const string FinalAnswer = "All done.";

        private readonly ScriptedProvider provider = new();
        private int callCounter;

        private Rig(StepCommitHarness harness, bool withCommitter)
        {
            Harness = harness;
            var services = new AgentRuntimeServices(
                _ => provider,
                HostClient: null,
                Store: null!,
                Catalog: null!,
                Todos: harness.Store,
                Model: new ModelSettings("scripted-model"))
            {
                StepCommitter = withCommitter ? harness.Committer : null,
            };
            Runtime = new SessionRuntime(
                services,
                new SessionInfo(StepCommitHarness.SessionId, harness.Repo.Path, "step commits", DateTimeOffset.UtcNow)
                {
                    WorktreePath = harness.Repo.Path,
                });
        }

        public StepCommitHarness Harness { get; }

        public StepCommitRepo Repo => Harness.Repo;

        public SessionRuntime Runtime { get; }

        /// <summary>Every event of every turn this rig ran, in order.</summary>
        public List<AgentEvent> Events { get; } = [];

        public static async Task<Rig> CreateAsync(bool withCommitter = true) =>
            new(await StepCommitHarness.CreateAsync(), withCommitter);

        public Task<TodoItem> AddAsync(string title) =>
            Harness.Store.AddAsync(StepCommitHarness.SessionId, title, null, null, CancellationToken.None);

        /// <summary>
        /// Runs one turn in which the scripted model calls <c>todo</c> with <paramref name="arguments"/> and then answers,
        /// and returns what the tool answered to the model.
        /// </summary>
        public async Task<ToolResult> TodoAsync(JsonObject arguments)
        {
            var callId = "call-" + Interlocked.Increment(ref callCounter);
            provider.Enqueue(ScriptedProvider.Call(callId, TodoTool.Name, arguments.ToJsonString()));
            provider.Enqueue(ScriptedProvider.Say(FinalAnswer));

            using var cts = new CancellationTokenSource(TurnTimeout);
            var start = Events.Count;
            await Runtime.RunTurnAsync("go", Events.Add, cts.Token);

            var failure = Events.Skip(start).OfType<AgentFailed>().FirstOrDefault();
            Assert.True(failure is null, "the turn failed: " + failure?.Error);
            var finished = Events.Skip(start).OfType<AgentToolFinished>().Single(candidate => candidate.Call.Id == callId);
            return finished.Result;
        }

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>Plays queued responses in order; an unexpected extra model request fails the test loudly.</summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly Queue<IReadOnlyList<ChatStreamEvent>> responses = new();
        private readonly Lock gate = new();

        public string Id => "scripted";

        public void Enqueue(IReadOnlyList<ChatStreamEvent> response)
        {
            lock (gate)
            {
                responses.Enqueue(response);
            }
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            IReadOnlyList<ChatStreamEvent> response;
            lock (gate)
            {
                if (!responses.TryDequeue(out var next))
                {
                    throw new InvalidOperationException("the scripted model was asked for more turns than the test queued");
                }

                response = next;
            }

            foreach (var value in response)
            {
                await Task.Yield();
                yield return value;
            }
        }

        public static IReadOnlyList<ChatStreamEvent> Say(string text) =>
        [
            new TextDelta(text),
            new MessageCompleted(ChatMessage.Assistant(text), new TokenUsage(10, 5), StopReason.EndTurn),
        ];

        public static IReadOnlyList<ChatStreamEvent> Call(string id, string name, string argumentsJson) =>
        [
            new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock(id, name, argumentsJson)]),
                new TokenUsage(10, 5),
                StopReason.ToolUse),
        ];
    }

    /// <summary>A real composition root over a throw-away data root; the environment is restored on dispose.</summary>
    private sealed class CompositionHarness : IAsyncDisposable
    {
        private readonly (string? Data, string? General, string? Workspace) saved;

        private CompositionHarness((string? Data, string? General, string? Workspace) saved, AppComposition composition)
        {
            this.saved = saved;
            Composition = composition;
        }

        public AppComposition Composition { get; }

        public static CompositionHarness Open(string root)
        {
            var saved = (
                Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
                General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
                Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));
            try
            {
                return new CompositionHarness(saved, AppComposition.Create());
            }
            catch (Exception)
            {
                Restore(saved);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Composition.DisposeAsync();
            Restore(saved);
        }

        private static void Restore((string? Data, string? General, string? Workspace) saved)
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
        }
    }
}
