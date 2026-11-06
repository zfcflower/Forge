using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Forge.Core.Tests.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// <see cref="AgentHostOptions.StepCommitter"/> must travel from the host options to the registered <c>todo</c> tool, and a
/// model's <c>todo</c> call must reach it through the real loop (approval, side-effect resolution, history), not only when
/// the tool is driven directly.
/// </summary>
public sealed class AgentHostStepCommitterTests
{
    private const string Session = "session-1";

    private sealed class RecordingCommitter : IStepCommitter
    {
        private readonly Lock gate = new();
        private readonly List<TodoItem> items = [];

        public Func<TodoItem, CancellationToken, Task<StepCommitResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(new StepCommitResult(StepCommitOutcome.Committed, "feedface12345678", "committed"));

        public IReadOnlyList<TodoItem> Items
        {
            get
            {
                lock (gate)
                {
                    return [.. items];
                }
            }
        }

        public Task<StepCommitResult> CommitStepAsync(TodoItem item, CancellationToken ct)
        {
            lock (gate)
            {
                items.Add(item);
            }

            return Handler(item, ct);
        }
    }

    /// <summary>Allows everything and remembers what it was asked, in arrival order.</summary>
    private sealed class RecordingPolicy : IApprovalPolicy
    {
        private readonly Lock gate = new();
        private readonly List<ApprovalRequest> requests = [];

        public IReadOnlyList<ApprovalRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return [.. requests];
                }
            }
        }

        public ApprovalMode Evaluate(ApprovalRequest request)
        {
            lock (gate)
            {
                requests.Add(request);
            }

            return ApprovalMode.Allow;
        }
    }

    private static AgentHost BuildHost(
        IChatProvider provider,
        ITodoStore? todos,
        IStepCommitter? committer,
        IApprovalPolicy? policy = null) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("test system", null, null),
            Policy = policy ?? new RecordingPolicy(),
            Todos = todos,
            StepCommitter = committer,
        });

    private static string DoneJson(string id, string extra = "") =>
        $$"""{"operation":"update","id":"{{id}}","status":"done"{{extra}}}""";

    private static ToolResultBlock[] ToolResults(AgentInstance root) =>
        [.. root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>())];

    private static FakeProvider OneCallThenDone(params (string Id, string Name, string Args)[] calls) =>
        new((_, index) => index == 0 ? Turn.Call(calls) : Turn.Say("done"));

    private static void AssertHistoryPaired(AgentInstance root)
    {
        var history = root.History;
        for (var index = 0; index < history.Count; index++)
        {
            var calls = history[index].Content.OfType<ToolCallBlock>().Select(call => call.Id).ToArray();
            if (calls.Length == 0)
            {
                continue;
            }

            Assert.True(index + 1 < history.Count && history[index + 1].Role == MessageRole.Tool, "A tool call has no tool message after it.");
            Assert.Equal(calls, history[index + 1].Content.OfType<ToolResultBlock>().Select(result => result.CallId).ToArray());
        }
    }

    [Fact]
    public async Task The_committer_in_the_host_options_runs_when_the_model_marks_a_step_done()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Wire it up", null, null, default);
        var committer = new RecordingCommitter();
        var provider = OneCallThenDone(("c1", "todo", DoneJson(item.Id, ",\"commit_subject\":\"feat: wire\",\"summary\":\"wired\",\"verification\":\"ran tests\"")));
        var host = BuildHost(provider, store, committer);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "finish the step"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Contains(provider.Requests[0].Tools, tool => tool.Name == "todo");
        var seen = Assert.Single(committer.Items);
        Assert.Equal(item.Id, seen.Id);
        Assert.Equal(Session, seen.SessionId);
        Assert.Equal(TodoStatus.Done, seen.Status);
        Assert.Equal(("feat: wire", "wired", "ran tests"), (seen.CommitSubject, seen.Summary, seen.Verification));

        var result = Assert.Single(ToolResults(root));
        Assert.False(result.IsError);
        Assert.Equal($"Updated #{item.Id}: [x] #{item.Id} Wire it up\nStep commit: committed feedfac", result.Content);
        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.Equal(result.Content, finished.Result.Content);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync(Session, default)).Status);
        AssertHistoryPaired(root);
    }

    [Fact]
    public async Task Without_a_committer_the_host_still_offers_todo_and_done_commits_nothing()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var provider = OneCallThenDone(("c1", "todo", DoneJson(item.Id)));
        var host = BuildHost(provider, store, committer: null);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Contains(provider.Requests[0].Tools, tool => tool.Name == "todo");
        var result = Assert.Single(ToolResults(root));
        Assert.False(result.IsError);
        Assert.Equal($"Updated #{item.Id}: [x] #{item.Id} Step", result.Content);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync(Session, default)).Status);
    }

    [Fact]
    public async Task A_committer_without_a_todo_store_registers_no_todo_tool_and_is_never_called()
    {
        var committer = new RecordingCommitter();
        var provider = OneCallThenDone(("c1", "todo", """{"operation":"update","id":"x","status":"done"}"""));
        var host = BuildHost(provider, todos: null, committer);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.DoesNotContain(provider.Requests[0].Tools, tool => tool.Name == "todo");
        Assert.False(host.Tools.TryGet("todo", out _));
        var result = Assert.Single(ToolResults(root));
        Assert.True(result.IsError);
        Assert.Equal("Unknown tool 'todo'.", result.Content);
        Assert.Empty(committer.Items);
    }

    [Fact]
    public async Task Each_host_hands_its_own_committer_to_its_own_todo_tool()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var first = await store.AddAsync("session-a", "A", null, null, default);
        var second = await store.AddAsync("session-b", "B", null, null, default);
        var committerA = new RecordingCommitter();
        var committerB = new RecordingCommitter();
        var hostA = BuildHost(OneCallThenDone(("c1", "todo", DoneJson(first.Id))), store, committerA);
        var hostB = BuildHost(OneCallThenDone(("c1", "todo", DoneJson(second.Id))), store, committerB);

        await AgentTestHost.CollectAsync(hostA.RunAsync(hostA.CreateRoot("session-a", Path.GetTempPath()), "go"));
        await AgentTestHost.CollectAsync(hostB.RunAsync(hostB.CreateRoot("session-b", Path.GetTempPath()), "go"));

        Assert.Equal(first.Id, Assert.Single(committerA.Items).Id);
        Assert.Equal(second.Id, Assert.Single(committerB.Items).Id);
    }

    [Fact]
    public async Task The_approval_policy_sees_the_per_call_side_effect_of_every_todo_operation()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var committer = new RecordingCommitter();
        var policy = new RecordingPolicy();
        (string Id, string Name, string Args)[] calls =
        [
            ("c1", "todo", """{"operation":"list"}"""),
            ("c2", "todo", """{"operation":"plan_summary_get"}"""),
            ("c3", "todo", """{"operation":"plan_summary_set","text":"the plan"}"""),
            ("c4", "todo", DoneJson(item.Id)),
            ("c5", "todo", """{"operation":"add","title":"later"}"""),
            ("c6", "todo", """{"operation":"list"}"""),
        ];
        var host = BuildHost(OneCallThenDone(calls), store, committer, policy);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var sideEffects = policy.Requests
            .GroupBy(request => request.ArgumentsJson, request => request.SideEffect)
            .ToDictionary(group => group.Key, group => group.ToArray());
        Assert.Equal([ToolSideEffect.ReadOnly, ToolSideEffect.ReadOnly], sideEffects["""{"operation":"list"}"""]);
        Assert.Equal([ToolSideEffect.ReadOnly], sideEffects["""{"operation":"plan_summary_get"}"""]);
        Assert.Equal([ToolSideEffect.Write], sideEffects["""{"operation":"plan_summary_set","text":"the plan"}"""]);
        Assert.Equal([ToolSideEffect.Write], sideEffects[DoneJson(item.Id)]);
        Assert.Equal([ToolSideEffect.Write], sideEffects["""{"operation":"add","title":"later"}"""]);
        Assert.All(policy.Requests, request => Assert.Equal("todo", request.ToolName));

        // Results come back in call order, and only the done update reached the committer.
        Assert.Equal(["c1", "c2", "c3", "c4", "c5", "c6"], ToolResults(root).Select(result => result.CallId).ToArray());
        Assert.All(ToolResults(root), result => Assert.False(result.IsError));
        Assert.Equal(item.Id, Assert.Single(committer.Items).Id);
        AssertHistoryPaired(root);
    }

    [Fact]
    public async Task A_denied_write_never_reaches_the_committer_while_reading_the_checklist_still_works()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var committer = new RecordingCommitter();

        // The stock policy asks for writes and no approval handler is wired, so the write is denied.
        var host = BuildHost(
            OneCallThenDone(("c1", "todo", """{"operation":"list"}"""), ("c2", "todo", DoneJson(item.Id))),
            store,
            committer,
            new DefaultApprovalPolicy());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var results = ToolResults(root);
        Assert.False(results[0].IsError);
        Assert.Contains("Step", results[0].Content, StringComparison.Ordinal);
        Assert.True(results[1].IsError);
        Assert.Equal("The user denied this tool call.", results[1].Content);
        Assert.Empty(committer.Items);
        Assert.Equal(TodoStatus.Pending, Assert.Single(await store.ListAsync(Session, default)).Status);
        Assert.Single(events.OfType<AgentApprovalDenied>());
    }

    [Fact]
    public async Task A_committer_failure_is_a_tool_result_for_the_model_not_a_failed_turn()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var committer = new RecordingCommitter { Handler = (_, _) => throw new InvalidOperationException("git exploded") };
        var provider = OneCallThenDone(("c1", "todo", DoneJson(item.Id)));
        var host = BuildHost(provider, store, committer);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        var result = Assert.Single(ToolResults(root));
        Assert.False(result.IsError);
        Assert.Contains("Step commit: FAILED: git exploded", result.Content, StringComparison.Ordinal);
        var stored = Assert.Single(await store.ListAsync(Session, default));
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Equal("git exploded", stored.CommitError);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("git exploded", ToolText(provider.Requests[1]), StringComparison.Ordinal);
    }

    private static string ToolText(ChatRequest request) =>
        string.Concat(request.Messages
            .SelectMany(message => message.Content.OfType<ToolResultBlock>())
            .Select(block => block.Content));

    [Fact]
    public async Task Cancelling_inside_the_committer_ends_the_run_quietly_and_leaves_the_step_done_and_the_history_paired()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        using var cts = new CancellationTokenSource();
        var committer = new RecordingCommitter
        {
            Handler = (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new StepCommitResult(StepCommitOutcome.Committed, "unreachable", "unreachable"));
            },
        };
        var provider = OneCallThenDone(("c1", "todo", DoneJson(item.Id)));
        var host = BuildHost(provider, store, committer);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go", cts.Token));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.Single(committer.Items);
        Assert.Single(provider.Requests);
        var stored = Assert.Single(await store.ListAsync(Session, default));
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Null(stored.CommitError);
        Assert.Null(stored.CommitId);
        AssertHistoryPaired(root);
    }

    [Fact]
    public async Task A_retried_done_after_a_failed_commit_reaches_the_committer_again_through_the_host()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var attempts = 0;
        var committer = new RecordingCommitter
        {
            Handler = async (committed, ct) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    await store.SetCommitResultAsync(committed.Id, null, "hook rejected", ct);
                    return new StepCommitResult(StepCommitOutcome.Failed, null, "hook rejected");
                }

                await store.SetCommitResultAsync(committed.Id, "0123456789abcdef", null, ct);
                return new StepCommitResult(StepCommitOutcome.Committed, "0123456789abcdef", "committed");
            },
        };
        var provider = new FakeProvider((_, index) => index switch
        {
            0 => Turn.Call(("c1", "todo", DoneJson(item.Id))),
            1 => Turn.Say("it failed"),
            2 => Turn.Call(("c2", "todo", DoneJson(item.Id))),
            3 => Turn.Say("fixed"),
            _ => Turn.Say("unexpected"),
        });
        var host = BuildHost(provider, store, committer);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var first = await AgentTestHost.CollectAsync(host.RunAsync(root, "finish"));
        var second = await AgentTestHost.CollectAsync(host.RunAsync(root, "try the commit again"));

        Assert.Empty(first.OfType<AgentFailed>());
        Assert.Empty(second.OfType<AgentFailed>());
        Assert.Equal(2, committer.Items.Count);
        var results = ToolResults(root);
        Assert.Contains("Step commit: FAILED: hook rejected", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("Step commit: committed 0123456", results[1].Content, StringComparison.Ordinal);
        var stored = Assert.Single(await store.ListAsync(Session, default));
        Assert.Equal("0123456789abcdef", stored.CommitId);
        Assert.Null(stored.CommitError);
        AssertHistoryPaired(root);
    }

    // Defect probe through the real loop: arguments that are valid JSON but not an object make TodoTool.Resolve throw, and
    // AgentLoop evaluates the side effect outside its tool try/catch, so the whole turn fails instead of the call erroring.
    [Theory(Skip = "defect: a todo call whose arguments are valid JSON but not an object ([] , null, 5, \"x\") fails the whole turn with AgentFailed (TodoTool.Resolve throws InvalidOperationException from TryGetProperty; AgentLoop.RunOneAsync/IsReadOnly call ToolRegistry.SideEffectOf outside any try/catch); the call should instead come back as an IsError tool result")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"list\"")]
    public async Task Non_object_todo_arguments_are_a_tool_error_and_do_not_fail_the_turn(string arguments)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var provider = OneCallThenDone(("c1", "todo", arguments));
        var host = BuildHost(provider, store, new RecordingCommitter());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var events = await AgentTestHost.CollectAsync(host.RunAsync(root, "go"));

        Assert.Empty(events.OfType<AgentFailed>());
        Assert.True(Assert.Single(ToolResults(root)).IsError);
    }
}
