using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Forge.Core.Tests.Agents;
using Forge.Core.Tests.Storage;
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

    /// <summary>
    /// The store <c>request_review</c> records its conclusions into: keeps every row, and can be told to fail so the
    /// "a store that throws never loses the review" rule can be pinned.
    /// </summary>
    private sealed class FakeConclusionStore : IReviewConclusionStore
    {
        private readonly Lock gate = new();
        private readonly List<ReviewConclusion> recorded = [];

        /// <summary>When set, the next <see cref="AddAsync"/> throws this instead of recording.</summary>
        public Func<Exception?>? Failure { get; set; }

        public IReadOnlyList<ReviewConclusion> Recorded
        {
            get
            {
                lock (gate)
                {
                    return [.. recorded];
                }
            }
        }

        public Task<ReviewConclusion> AddAsync(NewReviewConclusion conclusion, CancellationToken ct = default)
        {
            if (Failure?.Invoke() is { } failure)
            {
                return Task.FromException<ReviewConclusion>(failure);
            }

            lock (gate)
            {
                var stored = new ReviewConclusion(
                    recorded.Count + 1,
                    conclusion.WorkspacePath,
                    conclusion.SessionId,
                    conclusion.StepId,
                    conclusion.Base,
                    conclusion.HeadCommit,
                    conclusion.Outcome,
                    conclusion.Text,
                    DateTimeOffset.UnixEpoch.AddSeconds(recorded.Count));
                recorded.Add(stored);
                return Task.FromResult(stored);
            }
        }

        public Task<IReadOnlyList<ReviewConclusion>> ListAsync(string sessionId, CancellationToken ct = default)
        {
            lock (gate)
            {
                return Task.FromResult<IReadOnlyList<ReviewConclusion>>(
                    [.. recorded.Where(row => string.Equals(row.SessionId, sessionId, StringComparison.Ordinal))]);
            }
        }
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

    private static RequestReviewTool CreateTool(FakeReviewChangeSetSource source, ITodoStore? todos = null, StepReviewLedger? ledger = null) =>
        new(CreateScheduler(), source, todos, ledger: ledger);

    private static AgentHost CreateHost(
        IChatProvider provider,
        FakeReviewChangeSetSource? source,
        ITodoStore? todos = null,
        AgentOptions? options = null,
        ITool? read = null,
        Func<ReviewPackageLimits>? limits = null,
        IReviewConclusionStore? conclusions = null,
        IStepChangeProbe? stepChanges = null,
        Func<bool>? stepGatesEnabled = null) =>
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
            ReviewConclusions = conclusions,
            Todos = todos,
            StepChanges = stepChanges,
            StepGatesEnabled = stepGatesEnabled,
            ApprovalHandler = new AllowAll(),
            Options = options ?? new AgentOptions(),
        });

    /// <summary>Approves every ask, so a write tool call in a scripted turn reaches the tool instead of being denied.</summary>
    private sealed class AllowAll : IApprovalHandler
    {
        public ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct) =>
            ValueTask.FromResult(ApprovalDecision.Allow);
    }

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

    // ---------------------------------------------------------------- recording the verdict for the step gate

    [Fact]
    public async Task A_branch_review_records_no_verdict_so_it_does_not_unlock_the_step_gate()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var provider = new FakeProvider((request, index) =>
            Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal)
                ? Turn.Say("No blocking findings.\n\nverdict: approve")
                : index == 0
                    ? Turn.Call(("call-1", RequestReviewTool.Name, """{"intent":"add the request_review tool"}"""))
                    : index == 2
                        ? Turn.Call(("call-2", "todo", $$"""{"operation":"update","id":"{{item.Id}}","status":"done","verification":"dotnet test: 12 passed"}"""))
                        : Turn.Say("parent done"));
        var host = CreateHost(provider, new FakeReviewChangeSetSource(), todos: store, stepChanges: new DirtyProbe());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        // The branch review approves, but only a step review (step_id) earns a verdict: the dirty step stays refused.
        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.False(results[0].IsError, results[0].Content);
        Assert.StartsWith("verdict: approve", results[0].Content, StringComparison.Ordinal);
        Assert.True(results[1].IsError);
        Assert.Contains("cannot be marked done yet", results[1].Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Pending, Assert.Single(await store.ListAsync(Session, default)).Status);
    }

    [Fact]
    public async Task A_step_review_that_never_reaches_a_verdict_records_nothing()
    {
        var ledger = new StepReviewLedger();

        // An empty change set starts no reviewer at all.
        var empty = CreateTool(
            new FakeReviewChangeSetSource { Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(0, diff: null))) },
            new FakeTodoStore(Step()),
            ledger);
        var emptyResult = await InAgentScopeAsync(empty, """{"step_id":"a1"}""");
        Assert.False(emptyResult.IsError);
        Assert.Null(ledger.Find(Session, "a1"));

        // A review the scheduler refuses to start has no verdict either.
        var refused = CreateTool(new FakeReviewChangeSetSource(), new FakeTodoStore(Step()), ledger);
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "root", slot))
            {
                var result = await refused.InvokeAsync(
                    new ToolCall("call-1", RequestReviewTool.Name, """{"step_id":"a1"}"""),
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

        Assert.Null(ledger.Find(Session, "a1"));
    }

    [Fact]
    public async Task A_stopped_step_review_records_no_verdict_even_though_its_partial_text_has_one()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHost(
            PrematureVerdictStepScript(),
            new FakeReviewChangeSetSource(),
            todos: new FakeTodoStore(Step()),
            read: BlockingRead(entered),
            stepChanges: new DirtyProbe());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var run = host.RunToCompletionAsync(root);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop("Reviewer-1", "user stopped it"));
        await run;

        // The stopped review is an error, its partial text does carry "verdict: approve", and the todo call that follows
        // is still refused by the gate: the stop recorded no approval for the step. (Had it recorded one, the stored
        // verification of the step would satisfy the gate and the update would reach the fake store instead.)
        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.Contains("The review was stopped before it finished", results[0].Content, StringComparison.Ordinal);
        Assert.True(results[^1].IsError);
        Assert.Contains("cannot be marked done yet", results[^1].Content, StringComparison.Ordinal);
    }

    /// <summary>The gate's git evidence: always a dirty worktree, the answer that needs a review verdict.</summary>
    private sealed class DirtyProbe : IStepChangeProbe
    {
        public Task<StepChangeProbe> ProbeAsync(string sessionId, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(new StepChangeProbe(StepChangeState.Dirty));
    }

    /// <summary>
    /// The parent asks for a step review; the Reviewer's first answer already carries a verdict line next to a read it
    /// never finishes, which is what a stop in the middle of a review leaves behind. After the stopped review the parent
    /// tries to mark the step done.
    /// </summary>
    private static FakeProvider PrematureVerdictStepScript() =>
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
                    ? Turn.Call(("call-1", RequestReviewTool.Name, """{"step_id":"a1"}"""))
                    : index == 2
                        ? Turn.Call(("call-2", "todo", """{"operation":"update","id":"a1","status":"done"}"""))
                        : Turn.Say("parent done"));

    // ---------------------------------------------------------------- the host wiring: one ledger for both tools

    [Fact]
    public async Task The_gate_and_request_review_share_one_ledger_so_an_approval_lets_only_that_step_be_done()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var reviewed = await store.AddAsync(Session, "Wire the gate", null, null, default);
        var other = await store.AddAsync(Session, "Second step", null, null, default);
        var host = CreateHost(
            ReviewThenTwoDonesScript(reviewed.Id, other.Id),
            new FakeReviewChangeSetSource(),
            todos: store,
            stepChanges: new DirtyProbe());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        // The review approves one step, and only because both tools hold the host's one ledger does that step's done pass
        // the gate - while the other step, which was never reviewed, is still refused.
        var results = root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()).ToArray();
        Assert.False(results[0].IsError, results[0].Content);
        Assert.StartsWith("verdict: approve", results[0].Content, StringComparison.Ordinal);
        Assert.False(results[1].IsError, results[1].Content);
        Assert.Contains($"[x] #{reviewed.Id} Wire the gate", results[1].Content, StringComparison.Ordinal);
        Assert.True(results[2].IsError);
        Assert.Contains("cannot be marked done yet", results[2].Content, StringComparison.Ordinal);
        var stored = await store.ListAsync(Session, default);
        Assert.Equal(TodoStatus.Done, Assert.Single(stored, item => item.Id == reviewed.Id).Status);
        Assert.Equal(TodoStatus.Pending, Assert.Single(stored, item => item.Id == other.Id).Status);
    }

    [Fact]
    public async Task A_host_without_reviews_installs_no_probe_so_a_changed_step_still_finishes()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var provider = new FakeProvider((_, index) => index == 0
            ? Turn.Call(("call-1", "todo", $$"""{"operation":"update","id":"{{item.Id}}","status":"done","verification":"dotnet test: 12 passed"}"""))
            : Turn.Say("parent done"));
        var host = CreateHost(provider, source: null, todos: store, stepChanges: new DirtyProbe());
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        // No review tool means no verdict can be earned, so the host installs no probe: a dirty step is still let through.
        Assert.True(host.Tools.TryGet(TodoTool.Name, out _));
        Assert.False(host.Tools.TryGet(RequestReviewTool.Name, out _));
        var result = Assert.Single(root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()));
        Assert.False(result.IsError, result.Content);
        Assert.Contains($"[x] #{item.Id} Step", result.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync(Session, default)).Status);
    }

    [Fact]
    public async Task A_host_with_the_gates_preference_off_lets_a_changed_step_finish_without_a_review()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteTodoStore(directory.Path);
        var item = await store.AddAsync(Session, "Step", null, null, default);
        var provider = new FakeProvider((_, index) => index == 0
            ? Turn.Call(("call-1", "todo", $$"""{"operation":"update","id":"{{item.Id}}","status":"done","verification":"dotnet test: 12 passed"}"""))
            : Turn.Say("parent done"));
        var host = CreateHost(provider, new FakeReviewChangeSetSource(), todos: store, stepChanges: new DirtyProbe(), stepGatesEnabled: () => false);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        // The gate has a probe and the step is dirty and unreviewed, but the preference switched it off: done still goes
        // through. Had the host dropped the StepGatesEnabled func, the same update would be refused for lack of a verdict.
        var result = Assert.Single(root.History.SelectMany(message => message.Content.OfType<ToolResultBlock>()));
        Assert.False(result.IsError, result.Content);
        Assert.Contains($"[x] #{item.Id} Step", result.Content, StringComparison.Ordinal);
        Assert.Equal(TodoStatus.Done, Assert.Single(await store.ListAsync(Session, default)).Status);
    }

    /// <summary>The parent asks for one step review, then marks that step done, then tries a second, unreviewed step.</summary>

    private static FakeProvider ReviewThenTwoDonesScript(string stepId, string otherId) =>
        new((request, index) =>
            Turn.LastUserText(request).StartsWith("This is a review request:", StringComparison.Ordinal)
                ? Turn.Say("No blocking findings.\n\nverdict: approve")
                : index == 0
                    ? Turn.Call(("call-1", RequestReviewTool.Name, $$"""{"step_id":"{{stepId}}"}"""))
                    : index == 2
                        ? Turn.Call(("call-2", "todo", $$"""{"operation":"update","id":"{{stepId}}","status":"done","verification":"dotnet test: 12 passed"}"""))
                        : index == 3
                            ? Turn.Call(("call-3", "todo", $$"""{"operation":"update","id":"{{otherId}}","status":"done","verification":"dotnet test: 12 passed"}"""))
                            : Turn.Say("parent done"));

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

    // ------------------------------------------------------------ conclusions (wave W17b)

    [Fact]
    public async Task An_approved_step_review_is_recorded_with_its_verdict_and_step()
    {
        var store = new FakeConclusionStore();
        var host = CreateHost(
            ReviewScript("No blocking findings.\n\nverdict: approve", """{"step_id":"a1"}"""),
            new FakeReviewChangeSetSource(),
            new FakeTodoStore(Step()),
            conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.Approve, row.Outcome);
        Assert.Equal(Session, row.SessionId);
        Assert.Equal("a1", row.StepId);
        Assert.Equal("1234567890abcdef", row.HeadCommit);
        Assert.Equal("0987654321fedcba", row.Base);
        Assert.Equal(root.WorkingDirectory, row.WorkspacePath);
        Assert.Contains("verdict: approve", row.Text, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.UnixEpoch, row.CreatedAt);
        Assert.Single(await store.ListAsync(Session));
    }

    [Fact]
    public async Task A_request_changes_review_is_recorded_as_such()
    {
        var store = new FakeConclusionStore();
        var host = CreateHost(
            ReviewScript("blocking: the cap is off by one\n\nverdict: request changes"),
            new FakeReviewChangeSetSource(),
            conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.RequestChanges, row.Outcome);
        Assert.Null(row.StepId);
        Assert.Contains("blocking: the cap is off by one", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_without_a_verdict_line_is_recorded_as_unstructured()
    {
        var store = new FakeConclusionStore();
        var host = CreateHost(ReviewScript("Two findings, and I forgot the format."), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        // The stored state is what a reader of the history has to trust: unstructured is never an approval.
        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.Unstructured, row.Outcome);
        Assert.Contains("forgot the format", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_review_is_recorded_as_stopped_with_its_partial_text()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeConclusionStore();
        var host = CreateHost(PrematureVerdictScript(), new FakeReviewChangeSetSource(), read: BlockingRead(entered), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var run = host.RunToCompletionAsync(root);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop("Reviewer-1", "user stopped it"));
        await run;

        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.Stopped, row.Outcome);
        // The premature verdict line is in the text the reviewer wrote, so a reader must not read it as the verdict.
        Assert.Contains("verdict: approve", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_that_could_not_be_started_is_recorded_as_failed()
    {
        var store = new FakeConclusionStore();
        var tool = new RequestReviewTool(CreateScheduler(), new FakeReviewChangeSetSource(), null, null, store);
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "root", slot))
            {
                var result = await tool.InvokeAsync(
                    new ToolCall("call-1", RequestReviewTool.Name, """{"intent":"add the request_review tool"}"""),
                    new ToolContext(Session, "work/tree", AgentDepth: 3),
                    CancellationToken.None);

                Assert.True(result.IsError);
            }
        }
        finally
        {
            slot.Dispose();
        }

        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.Failed, row.Outcome);
        Assert.Contains("could not be started", row.Text, StringComparison.Ordinal);
        Assert.Equal("work/tree", row.WorkspacePath);
    }

    [Fact]
    public async Task A_reviewer_that_returned_no_text_is_recorded_as_failed()
    {
        var store = new FakeConclusionStore();
        var host = CreateHost(ReviewScript("   "), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var row = Assert.Single(store.Recorded);
        Assert.Equal(ReviewConclusionOutcome.Failed, row.Outcome);
        Assert.Contains("returned no text", row.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_set_that_cannot_be_read_leaves_no_row_because_no_review_happened()
    {
        var store = new FakeConclusionStore();
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Failed("boom")),
        };
        var tool = new RequestReviewTool(CreateScheduler(), source, null, null, store);

        var result = await InAgentScopeAsync(tool, """{"intent":"add the request_review tool"}""");

        Assert.True(result.IsError);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task An_empty_change_set_leaves_no_row_because_no_reviewer_ran()
    {
        var store = new FakeConclusionStore();
        var source = new FakeReviewChangeSetSource
        {
            Handler = (_, _) => Task.FromResult(ReviewChangeSetResult.Ok(ChangeSet(0, diff: null))),
        };
        var host = CreateHost(ReviewScript("verdict: approve"), source, conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task Without_a_store_the_tool_result_is_exactly_the_same()
    {
        var withoutStore = CreateHost(ReviewScript("No blocking findings.\n\nverdict: approve"), new FakeReviewChangeSetSource());
        var rootWithout = withoutStore.CreateRoot(Session, Path.GetTempPath());
        await withoutStore.RunToCompletionAsync(rootWithout);

        var store = new FakeConclusionStore();
        var withStore = CreateHost(ReviewScript("No blocking findings.\n\nverdict: approve"), new FakeReviewChangeSetSource(), conclusions: store);
        var rootWith = withStore.CreateRoot(Session, Path.GetTempPath());
        await withStore.RunToCompletionAsync(rootWith);

        var without = Assert.Single(rootWithout.History[2].Content.OfType<ToolResultBlock>());
        var with = Assert.Single(rootWith.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(without.Content, with.Content);
        Assert.Equal(without.IsError, with.IsError);
        Assert.Single(store.Recorded);
    }

    [Fact]
    public async Task A_store_that_throws_never_turns_a_finished_review_into_an_error()
    {
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var host = CreateHost(ReviewScript("No blocking findings.\n\nverdict: approve"), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.StartsWith("verdict: approve", result.Content, StringComparison.Ordinal);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public void The_stored_outcomes_keep_their_numbering_because_reviews_db_stores_them_as_integers()
    {
        // A row written today is read by a later version by number, so a renumbering would silently reinterpret history.
        Assert.Equal(0, (int)ReviewConclusionOutcome.Unstructured);
        Assert.Equal(1, (int)ReviewConclusionOutcome.Approve);
        Assert.Equal(2, (int)ReviewConclusionOutcome.RequestChanges);
        Assert.Equal(3, (int)ReviewConclusionOutcome.Stopped);
        Assert.Equal(4, (int)ReviewConclusionOutcome.Failed);
    }

    // ------------------------------------------------------------ a failing store never changes the result (blind spots)

    [Fact]
    public async Task A_store_that_throws_leaves_a_request_changes_review_unchanged()
    {
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var host = CreateHost(ReviewScript("blocking: the cap is off by one\n\nverdict: request changes"), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.StartsWith("verdict: request changes", result.Content, StringComparison.Ordinal);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task A_store_that_throws_leaves_an_unstructured_review_unchanged()
    {
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var host = CreateHost(ReviewScript("Two findings, and I forgot the format."), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.False(result.IsError);
        Assert.StartsWith("verdict: unstructured", result.Content, StringComparison.Ordinal);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task A_store_that_throws_leaves_a_reviewer_that_returned_no_text_unchanged()
    {
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var host = CreateHost(ReviewScript("   "), new FakeReviewChangeSetSource(), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Contains("returned no text", result.Content, StringComparison.Ordinal);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task A_store_that_throws_leaves_a_stopped_review_unchanged()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var host = CreateHost(PrematureVerdictScript(), new FakeReviewChangeSetSource(), read: BlockingRead(entered), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        var run = host.RunToCompletionAsync(root);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SubAgentControlResult.Accepted, host.SubAgentControl.Stop("Reviewer-1", "user stopped it"));
        await run;

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.StartsWith("The review was stopped before it finished", result.Content, StringComparison.Ordinal);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task A_store_that_throws_leaves_a_review_that_could_not_start_unchanged()
    {
        var store = new FakeConclusionStore { Failure = () => new InvalidOperationException("the database is gone") };
        var tool = new RequestReviewTool(CreateScheduler(), new FakeReviewChangeSetSource(), null, null, store);
        var slot = Assert.IsType<AgentSlot>(new AgentSlotPool(1).TryAcquire());
        try
        {
            using (AgentScope.Enter(new ListSink(), "root", slot))
            {
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

        Assert.Empty(store.Recorded);
    }

    // ------------------------------------------------------------ the paths that leave no row (blind spots)

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"intent":"review it","verbosity":"loud"}""")]
    [InlineData("""{"step_id":5}""")]
    [InlineData("{}")]
    [InlineData("""{"intent":"   "}""")]
    [InlineData("""{"step_id":"a1","base":"main"}""")]
    [InlineData("""{"step_id":"zzz"}""")]
    public async Task An_argument_error_leaves_no_row_because_no_review_happened(string json)
    {
        var store = new FakeConclusionStore();
        var source = new FakeReviewChangeSetSource();
        var tool = new RequestReviewTool(CreateScheduler(), source, new FakeTodoStore(Step()), null, store);

        var result = await InAgentScopeAsync(tool, json);

        Assert.True(result.IsError);
        Assert.Empty(store.Recorded);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task A_review_without_a_working_directory_leaves_no_row()
    {
        var store = new FakeConclusionStore();
        var tool = new RequestReviewTool(CreateScheduler(), new FakeReviewChangeSetSource(), null, null, store);

        var result = await InAgentScopeAsync(() => InvokeAsync(tool, """{"intent":"review it"}""", " ").AsTask());

        Assert.True(result.IsError);
        Assert.Empty(store.Recorded);
    }

    [Fact]
    public async Task Two_reviews_in_one_session_record_both_conclusions_in_the_session()
    {
        var store = new FakeConclusionStore();
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
        var host = CreateHost(provider, source, options: new AgentOptions { MaxParallelSubAgents = 2 }, conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());

        await host.RunToCompletionAsync(root);

        Assert.Equal(2, store.Recorded.Count);
        Assert.All(store.Recorded, static row => Assert.Equal(Session, row.SessionId));
        Assert.Equal(
            [ReviewConclusionOutcome.Approve, ReviewConclusionOutcome.RequestChanges],
            store.Recorded.Select(static row => row.Outcome).Order().ToArray());
        Assert.Equal(2, (await store.ListAsync(Session)).Count);
    }

    [Fact]
    public async Task Cancelling_the_parent_mid_review_records_no_conclusion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeConclusionStore();
        var host = CreateHost(PrematureVerdictScript(), new FakeReviewChangeSetSource(), read: BlockingRead(entered, cancelled), conclusions: store);
        var root = host.CreateRoot(Session, Path.GetTempPath());
        using var cts = new CancellationTokenSource();

        var run = AgentTestHost.CollectAsync(host.RunAsync(root, "go", cts.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        // The cancellation reached the reviewer before a verdict existed, so nothing was recorded.
        Assert.Empty(store.Recorded);
    }
}
