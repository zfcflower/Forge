using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Tests.Agents;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>
/// <c>request_review</c>: argument validation, the failure paths that must not reach the model, and the one end-to-end
/// path through a real host where the Reviewer's verdict comes back as the head of the tool result.
/// </summary>
public sealed class RequestReviewToolTests
{
    private const string Session = "session";

    /// <summary>The change set source the tool reads: one scripted answer, every request recorded.</summary>
    private sealed class FakeReviewChangeSetSource : IReviewChangeSetSource
    {
        private readonly Lock gate = new();
        private readonly List<ReviewChangeSetRequest> requests = [];

        public Func<ReviewChangeSetRequest, CancellationToken, Task<ReviewChangeSetResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(1)));

        public int Calls
        {
            get
            {
                lock (gate)
                {
                    return requests.Count;
                }
            }
        }

        public IReadOnlyList<ReviewChangeSetRequest> Requests
        {
            get
            {
                lock (gate)
                {
                    return [.. requests];
                }
            }
        }

        public Task<ReviewChangeSetResult> BuildAsync(ReviewChangeSetRequest request, CancellationToken ct)
        {
            lock (gate)
            {
                requests.Add(request);
            }

            return Handler(request, ct);
        }
    }

    /// <summary>The session checklist the tool resolves a step id against; only the read side is reachable.</summary>
    private sealed class FakeTodoStore : ITodoStore
    {
        private readonly List<TodoItem> items;

        public FakeTodoStore(params TodoItem[] steps) => items = [.. steps];

        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([.. items.Where(item => item.SessionId == sessionId)]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            throw new NotSupportedException("This fake only answers step lookups.");

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            throw new NotSupportedException("This fake only answers step lookups.");

        public Task<bool> RemoveAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException("This fake only answers step lookups.");

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) =>
            throw new NotSupportedException("This fake only answers step lookups.");
    }

    private sealed class ListSink : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public void Emit(AgentEvent value) => Events.Add(value);
    }

    private static ReviewChangeSet ChangeSet(int files, string? diff = "diff --git a/src/f000.cs b/src/f000.cs\n+one\n")
    {
        return new ReviewChangeSet
        {
            WorkingDirectory = "work/tree",
            Branch = "feature",
            HeadCommit = "1234567890abcdef",
            Base = "0987654321fedcba",
            Files = [.. Enumerable.Range(0, files).Select(index => new ReviewChangedFile($"src/f{index}.cs", "modified", false))],
            DiffText = diff ?? string.Empty,
        };
    }

    private static SubAgentScheduler CreateScheduler() =>
        new(new AgentCatalog(null), new PromptLayers("test system", null, null), new PromptComposer(), null, new AgentOptions());

    private static RequestReviewTool CreateTool(FakeReviewChangeSetSource source, ITodoStore? todos = null) =>
        new(CreateScheduler(), source, todos);

    private static AgentHost CreateHost(IChatProvider provider, FakeReviewChangeSetSource? source, ITodoStore? todos = null) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools =
            [
                FakeTool.Echo("read"),
                FakeTool.Echo("grep"),
                FakeTool.Echo("find"),
                FakeTool.Echo("ls"),
                FakeTool.Echo("git_diff"),
                FakeTool.Echo("edit"),
                FakeTool.Echo("write"),
                FakeTool.Echo("bash"),
            ],
            Layers = new PromptLayers("test system", null, null),
            Reviews = source,
            Todos = todos,
        });

    /// <summary>A parent turn that asks for one review (<paramref name="arguments"/>), the Reviewer answering
    /// <paramref name="reviewerReply"/>, then the parent finishing.</summary>
    private static FakeProvider ReviewScript(string reviewerReply, string? arguments = null) =>
        new((request, index) =>
            Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal)
                ? Turn.Say(reviewerReply)
                : index == 0
                    ? Turn.Call(("call-1", RequestReviewTool.Name, arguments ?? """{"intent":"add the request_review tool"}"""))
                    : Turn.Say("parent done"));

    private static ValueTask<ToolResult> InvokeAsync(
        RequestReviewTool tool,
        string json,
        string workingDirectory = "work/tree") =>
        tool.InvokeAsync(
            new ToolCall("call-1", RequestReviewTool.Name, json),
            new ToolContext(Session, workingDirectory),
            CancellationToken.None);

    /// <summary>Runs <paramref name="body"/> inside a live agent scope, the way the agent loop does.</summary>
    private static async Task<ToolResult> InAgentScopeAsync(Func<Task<ToolResult>> body)
    {
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "root", slot))
            {
                return await body();
            }
        }
        finally
        {
            slot.Dispose();
        }
    }

    private static async Task<ToolResult> InAgentScopeAsync(RequestReviewTool tool, string json) =>
        await InAgentScopeAsync(() => InvokeAsync(tool, json).AsTask());

    /// <summary>The one model request the Reviewer received, recognised by the review package it was handed.</summary>
    private static ChatRequest ReviewerRequest(FakeProvider provider) =>
        Assert.Single(
            provider.Requests,
            request => Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal));

    [Fact]
    public void Definition_is_the_read_only_request_review_tool()
    {
        var tool = CreateTool(new FakeReviewChangeSetSource());

        Assert.Equal("request_review", tool.Definition.Name);
        // Read-only is what lets a turn run two reviews in parallel and skips the approval prompt.
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }

    [Fact]
    public async Task A_call_outside_a_running_agent_is_an_error_naming_the_tool()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InvokeAsync(tool, """{"intent":"add the request_review tool"}""");

        Assert.True(result.IsError);
        Assert.Contains("request_review is only available inside a running agent", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Invalid_json_is_an_error_and_the_change_set_is_not_read()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, "not json");

        Assert.True(result.IsError);
        Assert.Contains("arguments are invalid", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task An_unknown_property_is_an_error_naming_it()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, """{"intent":"review it","verbosity":"loud"}""");

        Assert.True(result.IsError);
        Assert.Contains("unknown property 'verbosity'", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_numeric_step_id_is_an_error_instead_of_a_branch_review()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source, new FakeTodoStore(Step()));

        var result = await InAgentScopeAsync(tool, """{"step_id":5}""");

        Assert.True(result.IsError);
        Assert.Contains("'step_id' must be a string", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("""{"intent":"review it","paths":"src/f000.cs"}""")]
    [InlineData("""{"intent":"review it","paths":[1]}""")]
    [InlineData("""{"intent":"review it","paths":[["nested"]]}""")]
    public async Task Paths_that_are_not_an_array_of_strings_are_an_error(string json)
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, json);

        Assert.True(result.IsError);
        Assert.Contains("'paths' must be an array of strings", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"base":"main"}""")]
    [InlineData("""{"intent":"   ","base":"main"}""")]
    public async Task Neither_a_step_id_nor_an_intent_is_an_error(string json)
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, json);

        Assert.True(result.IsError);
        Assert.Contains("requires 'step_id'", result.Content, StringComparison.Ordinal);
        Assert.Contains("'intent'", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_step_id_combined_with_a_base_is_an_error()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source, new FakeTodoStore(Step()));

        var result = await InAgentScopeAsync(tool, """{"step_id":"a1","base":"main"}""");

        Assert.True(result.IsError);
        Assert.Contains("step_id and base cannot be combined", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_step_id_without_a_checklist_tells_the_caller_to_pass_intent_instead()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, """{"step_id":"a1"}""");

        Assert.True(result.IsError);
        Assert.Contains("pass 'intent' instead", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task An_unknown_step_id_is_an_error_naming_the_id()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source, new FakeTodoStore(Step()));

        var result = await InAgentScopeAsync(tool, """{"step_id":"zzz"}""");

        Assert.True(result.IsError);
        Assert.Contains("No checklist step 'zzz'", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_change_set_source_failure_is_an_error_result_carrying_the_reason()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Failed("boom")),
        };
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, """{"intent":"add the request_review tool"}""");

        Assert.True(result.IsError);
        Assert.Contains("The change set could not be read", result.Content, StringComparison.Ordinal);
        Assert.Contains("boom", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_source_that_returns_neither_a_change_set_nor_a_reason_is_an_error_result()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(new ReviewChangeSetResult(null, null)),
        };
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(tool, """{"intent":"add the request_review tool"}""");

        Assert.True(result.IsError);
        Assert.Contains("the change set source returned no change set", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_step_review_says_the_step_commit_changed_nothing_against_its_parent()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(new ReviewChangeSet
            {
                WorkingDirectory = "work/tree",
                HeadCommit = "abcdef1234567890",
            })),
        };
        var tool = CreateTool(source, new FakeTodoStore(Step()));

        var result = await InAgentScopeAsync(tool, """{"step_id":"a1"}""");

        // Not an error, and no reviewer ran: a spawn here would fail for the missing runner of this bare scheduler.
        Assert.False(result.IsError);
        Assert.Equal(
            "Nothing to review: commit abcdef1 changes nothing against its parent. No reviewer was started.",
            result.Content);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Cancelling_the_change_set_read_propagates_instead_of_becoming_a_result()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => throw new OperationCanceledException(),
        };
        var tool = CreateTool(source);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => InAgentScopeAsync(tool, """{"intent":"add the request_review tool"}"""));
    }

    [Fact]
    public async Task A_blank_working_directory_is_an_error()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);

        var result = await InAgentScopeAsync(() => InvokeAsync(tool, """{"intent":"review it"}""", " ").AsTask());

        Assert.True(result.IsError);
        Assert.Contains("no working directory", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, source.Calls);
    }

    private static TodoItem Step(
        string id = "a1",
        string commitId = "abcdef1234567890",
        string summary = "wired the tool and its source",
        string verification = "dotnet test --filter Review") =>
        new(id, Session, "Wire request_review", TodoStatus.InProgress, null, 1, DateTimeOffset.UnixEpoch,
            CommitSubject: "feat: review tool", Summary: summary, Verification: verification, CommitId: commitId);

    [Fact]
    public async Task The_result_opens_with_the_verdict_and_carries_the_review_separately()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(1))),
        };
        var provider = ReviewScript("No blocking findings.\n\nverdict: approve");
        var host = CreateHost(provider, source);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.Equal("verdict: approve", result.Content.Split('\n')[0]);
        Assert.Contains("reviewed: 1234567 · 1 file · base 0987654", result.Content, StringComparison.Ordinal);
        Assert.Contains("\n\n---\n\nNo blocking findings.\n\nverdict: approve", result.Content, StringComparison.Ordinal);
        Assert.Contains("[agent_id: Reviewer-1]", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_request_changes_review_is_reported_as_such_with_two_files()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(2))),
        };
        var provider = ReviewScript("blocking: the cap is off by one\n\nverdict: request changes");
        var host = CreateHost(provider, source);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.Equal("verdict: request changes", result.Content.Split('\n')[0]);
        Assert.Contains("reviewed: 1234567 · 2 files · base 0987654", result.Content, StringComparison.Ordinal);
        Assert.Contains("call request_review again at the new HEAD", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_that_forgot_its_verdict_line_is_unstructured_and_not_an_approval()
    {
        var provider = ReviewScript("Two findings, and I forgot the format.");
        var host = CreateHost(provider, new FakeReviewChangeSetSource());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.Equal("verdict: unstructured", result.Content.Split('\n')[0]);
        Assert.Contains("this is not an approval", result.Content, StringComparison.Ordinal);
        Assert.Contains("Two findings, and I forgot the format.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reviewer_sees_git_diff_and_no_write_tool_and_cannot_request_another_review()
    {
        var provider = ReviewScript("verdict: approve");
        var host = CreateHost(provider, new FakeReviewChangeSetSource());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var reviewerRequest = ReviewerRequest(provider);
        Assert.True(Turn.HasTool(reviewerRequest, "git_diff"));
        Assert.False(Turn.HasTool(reviewerRequest, "edit"));
        Assert.False(Turn.HasTool(reviewerRequest, "write"));
        Assert.False(Turn.HasTool(reviewerRequest, "bash"));
        Assert.False(Turn.HasTool(reviewerRequest, RequestReviewTool.Name));
        Assert.Contains("add the request_review tool", Turn.LastUserText(reviewerRequest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_change_set_says_there_is_nothing_to_review_and_starts_no_reviewer()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(0, diff: null))),
        };
        var provider = ReviewScript("verdict: approve");
        var host = CreateHost(provider, source);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.Equal(
            "Nothing to review: the branch and the uncommitted tree against 0987654 contain no change. No reviewer was started.",
            result.Content);
        Assert.Equal(1, source.Calls);
        Assert.Empty(host.SubAgents.Instances);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task A_branch_review_passes_the_base_and_the_paths_to_the_change_set_source()
    {
        var source = new FakeReviewChangeSetSource();
        var provider = ReviewScript("verdict: approve", """{"intent":"add the tool","base":"main","paths":["src/a.cs","src/b.cs"]}""");
        var host = CreateHost(provider, source);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var request = Assert.Single(source.Requests);
        Assert.Equal("main", request.Base);
        Assert.Null(request.Commit);
        Assert.Equal(["src/a.cs", "src/b.cs"], request.Paths);
    }

    [Fact]
    public async Task A_step_review_asks_for_the_steps_own_commit_and_passes_its_fields_to_the_reviewer()
    {
        var source = new FakeReviewChangeSetSource();
        var provider = ReviewScript("verdict: approve", """{"step_id":"a1"}""");
        var host = CreateHost(provider, source, new FakeTodoStore(Step()));
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var request = Assert.Single(source.Requests);
        Assert.Equal("abcdef1234567890", request.Commit);
        Assert.Null(request.Base);
        Assert.Equal(root.WorkingDirectory, request.WorkingDirectory);

        var package = Turn.LastUserText(ReviewerRequest(provider));
        Assert.Contains("Checklist step: Wire request_review", package, StringComparison.Ordinal);
        Assert.Contains("What the step changed: wired the tool and its source", package, StringComparison.Ordinal);
        Assert.Contains("How it was verified: dotnet test --filter Review", package, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_registers_request_review_only_when_a_change_set_source_is_given()
    {
        var without = CreateHost(ReviewScript("verdict: approve"), source: null);
        var with = CreateHost(ReviewScript("verdict: approve"), new FakeReviewChangeSetSource());

        Assert.False(without.Tools.TryGet(RequestReviewTool.Name, out _));
        Assert.DoesNotContain(without.AllTools.BuiltIns, tool => tool.Definition.Name == RequestReviewTool.Name);

        Assert.True(with.Tools.TryGet(RequestReviewTool.Name, out var registered));
        Assert.IsType<RequestReviewTool>(registered);
        Assert.Contains(with.AllTools.BuiltIns, tool => tool.Definition.Name == RequestReviewTool.Name);
    }

    [Fact]
    public async Task A_reviewer_holding_the_tool_cannot_ask_for_another_review()
    {
        // The built-in Reviewer does not list the tool; a user override that adds it must not turn a review into a tree of
        // reviews. The guard is by agent name, so it holds however the tool surface was assembled.
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "Reviewer-1", slot))
            {
                var result = await tool.InvokeAsync(
                    new ToolCall("call-1", RequestReviewTool.Name, """{"intent":"review the review"}"""),
                    new ToolContext(Session, "work/tree") { AgentName = "Reviewer" },
                    CancellationToken.None);

                Assert.True(result.IsError);
                Assert.Contains("cannot ask for another review", result.Content, StringComparison.Ordinal);
            }
        }
        finally
        {
            slot.Dispose();
        }

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_review_that_cannot_be_started_names_the_review_before_the_scheduler_reason()
    {
        var source = new FakeReviewChangeSetSource();
        var tool = CreateTool(source);
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "root", slot))
            {
                // Depth 3 is the scheduler's own limit, reached here without nesting three real agents.
                var result = await tool.InvokeAsync(
                    new ToolCall("call-1", RequestReviewTool.Name, """{"intent":"add the request_review tool"}"""),
                    new ToolContext(Session, "work/tree", AgentDepth: 3),
                    CancellationToken.None);

                Assert.True(result.IsError);
                Assert.StartsWith("The review could not be started: ", result.Content, StringComparison.Ordinal);
            }
        }
        finally
        {
            slot.Dispose();
        }
    }
}
