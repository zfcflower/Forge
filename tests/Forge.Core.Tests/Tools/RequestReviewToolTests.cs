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

    private static AgentHost CreateHost(
        IChatProvider provider,
        FakeReviewChangeSetSource? source,
        ITodoStore? todos = null,
        AgentOptions? options = null,
        ITool? read = null,
        Func<ReviewPackageLimits>? limits = null) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools =
            [
                read ?? FakeTool.Echo("read"),
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
            ReviewLimits = limits,
            Todos = todos,
            Options = options ?? new AgentOptions(),
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
        string? commitId = "abcdef1234567890",
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
    public async Task A_review_runs_with_one_agent_slot_because_the_parent_hands_its_permit_back()
    {
        // With a single slot the child can only start if the parent detaches its permit for the wait
        // (AgentScope.Current.Permit.Detach); without it this run never finishes and the assertion below times out.
        var source = new FakeReviewChangeSetSource();
        var host = CreateHost(
            ReviewScript("verdict: approve"),
            source,
            options: new AgentOptions { MaxParallelSubAgents = 1 });
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var run = host.RunToCompletionAsync(root);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(run, finished);
        await run;
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task A_step_without_its_own_commit_reviews_the_branch_and_says_so_in_the_package()
    {
        // Step commits can be switched off, and a step with nothing to commit stores no commit id: the review then covers
        // the branch and the working tree, and the package must not read as if the step's own commit had been reviewed.
        var source = new FakeReviewChangeSetSource();
        var provider = ReviewScript("verdict: approve", """{"step_id":"a1"}""");
        var host = CreateHost(provider, source, new FakeTodoStore(Step(commitId: null)));
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var request = Assert.Single(source.Requests);
        Assert.Null(request.Commit);
        Assert.Null(request.Base);
        var package = Turn.LastUserText(ReviewerRequest(provider));
        Assert.Contains("Checklist step: Wire request_review", package, StringComparison.Ordinal);
        Assert.Contains("no commit of its own", package, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_step_review_of_a_commit_that_changed_nothing_says_so_and_starts_no_reviewer()
    {
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(new ReviewChangeSet
            {
                WorkingDirectory = "work/tree",
                HeadCommit = "abcdef1234567890",
                Base = "0123456789abcdef",
            })),
        };
        var tool = CreateTool(source, new FakeTodoStore(Step()));

        var result = await InAgentScopeAsync(tool, """{"step_id":"a1"}""");

        Assert.False(result.IsError);
        Assert.Contains("Nothing to review", result.Content, StringComparison.Ordinal);
        Assert.Contains("changes nothing against its parent", result.Content, StringComparison.Ordinal);
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

    /// <summary>A reviewer read that blocks until its token is cancelled, signalling when it was entered.</summary>
    private static FakeTool BlockingRead(TaskCompletionSource entered, TaskCompletionSource? cancelled = null) =>
        new("read", ToolSideEffect.ReadOnly, async (_, _, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled?.TrySetResult();
                throw;
            }

            return new ToolResult("unreachable");
        });

    /// <summary>
    /// The parent asks for one review; the Reviewer's first answer already carries a verdict line next to a read it never
    /// finishes, which is what a stop in the middle of a review leaves behind.
    /// </summary>
    private static FakeProvider PrematureVerdictScript() =>
        new((request, index) =>
            Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal)
                ?
                [
                    new MessageCompleted(
                        new ChatMessage(
                            MessageRole.Assistant,
                            [new TextBlock("verdict: approve"), new ToolCallBlock("r-1", "read", "{}")]),
                        new TokenUsage(10, 5),
                        StopReason.ToolUse),
                ]
                : index == 0
                    ? Turn.Call(("call-1", RequestReviewTool.Name, """{"intent":"add the request_review tool"}"""))
                    : Turn.Say("parent done"));

    [Fact]
    public async Task A_reviewer_stopped_mid_review_is_an_error_and_never_its_partial_verdict()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(PrematureVerdictScript(), new FakeReviewChangeSetSource(), read: BlockingRead(entered));
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var run = host.RunToCompletionAsync(root);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop("Reviewer-1", "user stopped it"));
        await run;

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.StartsWith("The review was stopped before it finished", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing blocking was found", result.Content, StringComparison.Ordinal);

        // The partial text does carry the premature verdict line, so this case is the one a verdict parse would misread.
        Assert.Contains("verdict: approve", result.Content, StringComparison.Ordinal);
        Assert.False(result.Content.StartsWith("verdict:", StringComparison.Ordinal));
        Assert.Contains("[agent_id: Reviewer-1]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_parent_during_a_review_cancels_the_reviewer_and_returns_no_verdict()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(PrematureVerdictScript(), new FakeReviewChangeSetSource(), read: BlockingRead(entered, cancelled));
        var root = host.CreateRoot(Session, Path.GetTempPath());
        using var cts = new CancellationTokenSource();

        var run = AgentTestHost.CollectAsync(host.RunAsync(root, "go", cts.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        // The cancellation reached the reviewer's own tool and no tool result (least of all an approval) reached the parent.
        Assert.DoesNotContain(
            root.History.SelectMany(static message => message.Content.OfType<ToolResultBlock>()),
            static block => block.Content.StartsWith("verdict:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_reviews_in_one_turn_run_side_by_side_and_each_returns_its_own_verdict()
    {
        var source = new FakeReviewChangeSetSource();
        var provider = new FakeProvider((request, index) =>
            Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal)
                ? Turn.Say(Turn.LastUserText(request).Contains("first change", StringComparison.Ordinal)
                    ? "verdict: approve"
                    : "blocking: broken\n\nverdict: request changes")
                : index == 0
                    ? Turn.Call(
                        ("call-1", RequestReviewTool.Name, """{"intent":"first change"}"""),
                        ("call-2", RequestReviewTool.Name, """{"intent":"second change"}"""))
                    : Turn.Say("parent done"));
        var host = CreateHost(provider, source, options: new AgentOptions { MaxParallelSubAgents = 2 });
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var results = root.History[2].Content.OfType<ToolResultBlock>().ToDictionary(static block => block.CallId);
        Assert.Equal(2, results.Count);
        Assert.StartsWith("verdict: approve", results["call-1"].Content, StringComparison.Ordinal);
        Assert.StartsWith("verdict: request changes", results["call-2"].Content, StringComparison.Ordinal);
        Assert.Equal(2, source.Calls);
        Assert.Equal(2, host.SubAgents.Instances.Select(static instance => instance.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_overlong_review_is_cut_in_the_result_but_its_verdict_is_read_from_the_whole_text()
    {
        var review = string.Concat(Enumerable.Repeat("minor: a long finding line\n", 400)) + "verdict: request changes";
        Assert.True(review.Length > RequestReviewVerdict.MaxReviewChars);
        var host = CreateHost(ReviewScript(review), new FakeReviewChangeSetSource());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.StartsWith("verdict: request changes", result.Content, StringComparison.Ordinal);
        Assert.Contains("[the review was cut", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < review.Length);
    }

    [Fact]
    public async Task A_cut_diff_is_flagged_in_the_result_and_the_reviewer_is_given_the_base_that_reproduces_it()
    {
        var diff = "diff --git a/src/f0.cs b/src/f0.cs\n" + string.Concat(Enumerable.Repeat("+a changed line\n", 4_000));
        Assert.True(diff.Length > ReviewPackageLimits.DefaultDiffChars);
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(1, diff))),
        };
        var provider = ReviewScript("verdict: approve");
        var host = CreateHost(provider, source);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Contains("note: the diff was cut", result.Content, StringComparison.Ordinal);
        var package = Turn.LastUserText(ReviewerRequest(provider));
        Assert.Contains("git_diff {\"base\": \"0987654321fedcba\"}", package, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_limits_the_host_hands_in_are_read_for_each_review_and_cap_the_package()
    {
        var diff = "diff --git a/src/f0.cs b/src/f0.cs\n" + string.Concat(Enumerable.Repeat("+a changed line\n", 400));
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(30, diff))),
        };
        var current = new ReviewPackageLimits { DiffChars = 500, FileLines = 5 };
        var provider = ReviewScript("verdict: approve");
        var host = CreateHost(provider, source, limits: () => current);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var package = Turn.LastUserText(ReviewerRequest(provider));
        Assert.Contains("[the diff was cut at ", package, StringComparison.Ordinal);
        Assert.Contains("... and 25 more files", package, StringComparison.Ordinal);

        // A later review in the same host sees the changed limits: nothing is captured at construction.
        current = new ReviewPackageLimits();
        var provider2 = ReviewScript("verdict: approve");
        var host2 = CreateHost(provider2, source, limits: () => current);
        await host2.RunToCompletionAsync(host2.CreateRoot(Session, Path.GetTempPath()));

        var wide = Turn.LastUserText(ReviewerRequest(provider2));
        Assert.DoesNotContain("[the diff was cut at ", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("more files", wide, StringComparison.Ordinal);
    }
}
