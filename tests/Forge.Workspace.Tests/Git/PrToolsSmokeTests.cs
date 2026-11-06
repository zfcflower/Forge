using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>An in-memory pull request platform that records every call and lets a test change what the next read sees.</summary>
internal sealed class PrLifecycleFakePlatform : IPrPlatform
{
    public string Name => "FakeHub";

    public PrPlatformAvailability Availability { get; set; } = new(PrPlatformState.Ready, "logged in");

    /// <summary>The pull request every read returns; null means "no pull request".</summary>
    public PullRequestInfo? Current { get; set; }

    public int ViewCount { get; private set; }

    public List<string?> ViewSelectors { get; } = [];

    /// <summary>Called with the 1-based number of the read, before that read returns; mutate <see cref="Current"/> to simulate a concurrent change.</summary>
    public Action<int>? BeforeView { get; set; }

    /// <summary>When set, an accepted edit is not applied (the platform "lost" it).</summary>
    public bool DropEdits { get; set; }

    public List<(int Number, string? Title, string? Body)> Edits { get; } = [];

    public List<(string Title, string Body, string Base, string Head, bool Draft)> Creates { get; } = [];

    public PrResult<PrReference>? CreateFailure { get; set; }

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Task.FromResult(Availability);

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default)
    {
        ViewCount++;
        ViewSelectors.Add(selector);
        BeforeView?.Invoke(ViewCount);
        return Task.FromResult(Current is { } current
            ? PrResult<PullRequestInfo>.Ok(current)
            : PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other,
                "no pull requests found",
                reason: PrFailureReason.PullRequestNotFound));
    }

    public Task<PrResult<PrReference>> CreatePullRequestAsync(
        string workingDirectory,
        string title,
        string body,
        string baseBranch,
        string headBranch,
        bool draft = false,
        CancellationToken ct = default)
    {
        Creates.Add((title, body, baseBranch, headBranch, draft));
        if (CreateFailure is { } failure)
        {
            return Task.FromResult(failure);
        }

        const string Url = "https://example.test/acme/forge/pull/7";
        Current = new PullRequestInfo(
            7, Url, PrState.Open, title, body, headBranch, baseBranch, "0123456789abcdef", draft,
            PrMergeStateStatus.Clean, PrReviewDecision.None);
        return Task.FromResult(PrResult<PrReference>.Ok(new PrReference(7, Url)));
    }

    public Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default)
    {
        Edits.Add((number, title, body));
        if (!DropEdits && Current is { } current)
        {
            Current = current with { Title = title ?? current.Title, Body = body ?? current.Body };
        }

        return Task.FromResult(PrResult<PrReference>.Ok(new PrReference(number, Current?.Url ?? string.Empty)));
    }

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>A todo store that only knows a plan summary and a list of steps.</summary>
internal sealed class PrLifecycleFakeTodos : ITodoStore
{
    public event Action<string>? Changed
    {
        add { }
        remove { }
    }

    public PlanSummary? Summary { get; set; }

    public List<TodoItem> Items { get; } = [];

    public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodoItem>>(Items.ToList());

    /// <summary>Runs when the plan summary is read, which <c>pr_create</c> does after the gates and before its final re-check.</summary>
    public Action? OnPlanSummary { get; set; }

    public Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct)
    {
        OnPlanSummary?.Invoke();
        return Task.FromResult(Summary);
    }

    public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => throw new NotSupportedException();
}

internal static class PrLifecycleTestHelpers
{
    public const string Session = "session-1";

    public static PullRequestInfo Pr(
        string body = "## Summary\nOld summary.\n\n## Testing\nOld testing.\n",
        string title = "Add feature",
        PrState state = PrState.Open,
        string baseBranch = "main",
        PrMergeStateStatus merge = PrMergeStateStatus.Clean,
        PrReviewDecision review = PrReviewDecision.None) =>
        new(5, "https://example.test/acme/forge/pull/5", state, title, body, "forge/feature", baseBranch,
            "0123456789abcdef0123456789abcdef01234567", false, merge, review);

    public static async Task<ToolResult> InvokeAsync(ITool tool, string json, string workingDirectory, string session = Session) =>
        await tool.InvokeAsync(
            new ToolCall("call-1", tool.Definition.Name, json),
            new ToolContext(session, workingDirectory),
            CancellationToken.None);

    public static PrTools Tools(IPrPlatform platform, ITodoStore? todos = null, GitReviewLedger? ledger = null) =>
        new(new GitCli(), platform, new GitToolSettings(), ledger ?? new GitReviewLedger(), todos ?? new PrLifecycleFakeTodos());
}

/// <summary>Tool definitions, <c>pr_view</c>, <c>pr_workflow</c>, <c>pr_edit</c> and <see cref="PrBodyEditor"/> against the fake platform.</summary>
public sealed class PrToolsSmokeTests
{
    private static readonly string[] AllNames = ["pr_view", "pr_create", "pr_sync", "pr_edit", "pr_workflow"];

    [Fact]
    public void Tools_have_stable_names_fixed_side_effects_and_point_to_pr_workflow()
    {
        var tools = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform());

        Assert.Equal(AllNames, tools.All.Select(static tool => tool.Definition.Name).ToArray());
        Assert.Equal(ToolSideEffect.ReadOnly, tools.View.SideEffect);
        Assert.Equal(ToolSideEffect.ReadOnly, tools.Workflow.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Create.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Sync.SideEffect);
        Assert.Equal(ToolSideEffect.Write, tools.Edit.SideEffect);

        foreach (var tool in tools.All.Where(static tool => tool.Definition.Name != PrTools.WorkflowName))
        {
            Assert.Contains("pr_workflow", tool.Definition.Description, StringComparison.Ordinal);
            Assert.Contains("phase", tool.Definition.Description, StringComparison.Ordinal);
        }

        Assert.Contains("'creating'", tools.Create.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("'syncing'", tools.Sync.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("'body'", tools.Edit.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("'checks'", tools.View.Definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_schemas_are_valid_json_objects()
    {
        var tools = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform());
        foreach (var tool in tools.All)
        {
            using var document = System.Text.Json.JsonDocument.Parse(tool.Definition.InputSchemaJson);
            Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
            Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public void Create_description_says_honestly_that_code_enforces_only_the_re_read()
    {
        var description = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform()).Create.Definition.Description;

        Assert.Contains("only enforce that the diff was re-read", description, StringComparison.Ordinal);
        Assert.Contains("your own judgement", description, StringComparison.Ordinal);
        Assert.Contains("nothing here checks it", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var platform = new PrLifecycleFakePlatform();
        var todos = new PrLifecycleFakeTodos();
        var settings = new GitToolSettings();
        var ledger = new GitReviewLedger();

        Assert.Throws<ArgumentNullException>(() => new PrTools(null!, platform, settings, ledger, todos));
        Assert.Throws<ArgumentNullException>(() => new PrTools(new GitCli(), null!, settings, ledger, todos));
        Assert.Throws<ArgumentNullException>(() => new PrTools(new GitCli(), platform, null!, ledger, todos));
        Assert.Throws<ArgumentNullException>(() => new PrTools(new GitCli(), platform, settings, null!, todos));
        Assert.Throws<ArgumentNullException>(() => new PrTools(new GitCli(), platform, settings, ledger, null!));
    }

    [Theory]
    [InlineData("creating", "git_diff")]
    [InlineData("syncing", "git log -p")]
    [InlineData("body", "section")]
    [InlineData("checks", "BEHIND")]
    [InlineData("review-comments", "push_back")]
    [InlineData("screenshots", ".forge/screenshots.json")]
    public async Task Workflow_returns_the_text_of_each_phase(string phase, string expectedFragment)
    {
        var tools = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform());

        var result = await PrLifecycleTestHelpers.InvokeAsync(tools.Workflow, $$"""{"phase":"{{phase}}"}""", Path.GetTempPath());

        Assert.False(result.IsError);
        Assert.Contains(expectedFragment, result.Content, StringComparison.Ordinal);
        Assert.True(PrWorkflowText.TryGet(phase, out var text));
        Assert.Equal(text, result.Content);
    }

    [Fact]
    public void Workflow_texts_cover_the_required_points()
    {
        Assert.True(PrWorkflowText.TryGet("checks", out var checks));
        Assert.Contains("snapshot", checks, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wait", checks, StringComparison.OrdinalIgnoreCase);
        foreach (var state in new[] { "CLEAN", "HAS_HOOKS", "BEHIND", "DIRTY", "BLOCKED", "UNSTABLE", "DRAFT", "UNKNOWN" })
        {
            Assert.Contains(state, checks, StringComparison.Ordinal);
        }

        Assert.Contains("Do not sync by reflex", checks, StringComparison.Ordinal);

        Assert.True(PrWorkflowText.TryGet("review-comments", out var review));
        Assert.Contains("Judge before changing", review, StringComparison.Ordinal);
        foreach (var fragment in new[] { "apply", "push_back", "clarify", "commit sha", "pr_comments", "review_comments", "Resolve a thread only after" })
        {
            Assert.Contains(fragment, review, StringComparison.Ordinal);
        }

        Assert.True(PrWorkflowText.TryGet("screenshots", out var screenshots));
        foreach (var fragment in new[] { ".forge/screenshots.json", "command", "output", "timeoutSeconds", "pr_screenshots", "cannot screenshot" })
        {
            Assert.Contains(fragment, screenshots, StringComparison.Ordinal);
        }

        Assert.Equal(6, PrWorkflowText.Phases.Count);
        foreach (var phase in PrWorkflowText.Phases)
        {
            Assert.True(PrWorkflowText.TryGet(phase, out var text));
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public async Task Workflow_reports_an_unknown_phase_with_the_valid_ones()
    {
        var tools = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform());

        var unknown = await PrLifecycleTestHelpers.InvokeAsync(tools.Workflow, """{"phase":"merging"}""", Path.GetTempPath());
        var missing = await PrLifecycleTestHelpers.InvokeAsync(tools.Workflow, "{}", Path.GetTempPath());

        Assert.True(unknown.IsError);
        Assert.Contains("merging", unknown.Content, StringComparison.Ordinal);
        Assert.Contains("review-comments", unknown.Content, StringComparison.Ordinal);
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task View_formats_the_pull_request_and_passes_the_selector()
    {
        var platform = new PrLifecycleFakePlatform
        {
            Current = PrLifecycleTestHelpers.Pr(merge: PrMergeStateStatus.Behind, review: PrReviewDecision.ChangesRequested),
        };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var byNumber = await PrLifecycleTestHelpers.InvokeAsync(tools.View, """{"pr":12}""", Path.GetTempPath());
        var byText = await PrLifecycleTestHelpers.InvokeAsync(tools.View, """{"pr":"forge/other"}""", Path.GetTempPath());
        var current = await PrLifecycleTestHelpers.InvokeAsync(tools.View, "{}", Path.GetTempPath());

        Assert.False(byNumber.IsError);
        Assert.Contains("PR #5 (open)", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("https://example.test/acme/forge/pull/5", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("Title: Add feature", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("forge/feature -> main", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("Merge state: BEHIND", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("Review decision: CHANGES_REQUESTED", byNumber.Content, StringComparison.Ordinal);
        Assert.Contains("Old summary.", byNumber.Content, StringComparison.Ordinal);
        Assert.False(byText.IsError);
        Assert.False(current.IsError);
        Assert.Equal(new string?[] { "12", "forge/other", null }, platform.ViewSelectors.ToArray());
    }

    [Fact]
    public async Task View_without_a_pull_request_says_so_and_without_gh_is_an_error()
    {
        var platform = new PrLifecycleFakePlatform();
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var none = await PrLifecycleTestHelpers.InvokeAsync(tools.View, "{}", Path.GetTempPath());
        platform.Availability = new PrPlatformAvailability(PrPlatformState.NotAuthenticated, "not logged in to github.com");
        var notReady = await PrLifecycleTestHelpers.InvokeAsync(tools.View, "{}", Path.GetTempPath());
        platform.Availability = new PrPlatformAvailability(PrPlatformState.NotInstalled, "gh not found");
        var missing = await PrLifecycleTestHelpers.InvokeAsync(tools.View, "{}", Path.GetTempPath());

        Assert.False(none.IsError);
        Assert.Contains("no pull request", none.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pr_create", none.Content, StringComparison.Ordinal);
        Assert.True(notReady.IsError);
        Assert.Contains("gh auth login", notReady.Content, StringComparison.Ordinal);
        Assert.True(missing.IsError);
        Assert.Contains("Install the GitHub CLI", missing.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"pr":true}""")]
    [InlineData("""{"pr":-3}""")]
    [InlineData("""{"pr":"--json"}""")]
    [InlineData("""{"number":3}""")]
    public async Task View_rejects_bad_arguments(string json)
    {
        var tools = PrLifecycleTestHelpers.Tools(new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() });

        var result = await PrLifecycleTestHelpers.InvokeAsync(tools.View, json, Path.GetTempPath());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Edit_replaces_one_section_and_verifies_by_reading_back()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Edit,
            """{"section":{"heading":"Testing","content":"Ran dotnet test: 412 passed."}}""",
            Path.GetTempPath());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("verified", result.Content, StringComparison.Ordinal);
        Assert.Contains("section 'Testing'", result.Content, StringComparison.Ordinal);
        var edit = Assert.Single(platform.Edits);
        Assert.Null(edit.Title);
        Assert.Equal(5, edit.Number);
        Assert.Equal("Ran dotnet test: 412 passed.", PrBodyRenderer.TryGetSection(edit.Body, "Testing"));
        Assert.Equal("Old summary.", PrBodyRenderer.TryGetSection(edit.Body, "Summary"));
        Assert.Equal(3, platform.ViewCount); // first read, read right before the write, read back
    }

    [Fact]
    public async Task Edit_applies_a_section_edit_onto_a_body_that_changed_meanwhile()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        platform.BeforeView = count =>
        {
            if (count == 2)
            {
                platform.Current = platform.Current! with
                {
                    Body = platform.Current.Body + "\n## Notes\nAdded by a reviewer meanwhile.\n",
                };
            }
        };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Edit,
            """{"section":{"heading":"Testing","content":"New testing evidence."}}""",
            Path.GetTempPath());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("applied onto the newest body", result.Content, StringComparison.Ordinal);
        var written = Assert.Single(platform.Edits).Body;
        Assert.Equal("Added by a reviewer meanwhile.", PrBodyRenderer.TryGetSection(written, "Notes"));
        Assert.Equal("New testing evidence.", PrBodyRenderer.TryGetSection(written, "Testing"));
        Assert.Equal("Old summary.", PrBodyRenderer.TryGetSection(platform.Current!.Body, "Summary"));
    }

    [Fact]
    public async Task Edit_refuses_a_full_replacement_when_the_body_changed_meanwhile_and_shows_both()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        platform.BeforeView = count =>
        {
            if (count == 2)
            {
                platform.Current = platform.Current! with { Body = "Somebody else rewrote this." };
            }
        };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Edit,
            """{"body":"## Summary\nMy rewrite.\n"}""",
            Path.GetTempPath());

        Assert.True(result.IsError);
        Assert.Contains("Somebody else rewrote this.", result.Content, StringComparison.Ordinal);
        Assert.Contains("My rewrite.", result.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", result.Content, StringComparison.Ordinal);
        Assert.Empty(platform.Edits);
    }

    [Fact]
    public async Task Edit_reports_a_mismatch_when_the_read_back_differs()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr(), DropEdits = true };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Edit,
            """{"section":{"heading":"Testing","content":"Something new."}}""",
            Path.GetTempPath());

        Assert.True(result.IsError);
        Assert.Contains("Verification failed", result.Content, StringComparison.Ordinal);
        Assert.Single(platform.Edits);
    }

    [Fact]
    public async Task Edit_title_only_sends_no_body_and_a_no_op_writes_nothing()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var renamed = await PrLifecycleTestHelpers.InvokeAsync(tools.Edit, """{"title":"Add the feature"}""", Path.GetTempPath());
        var unchanged = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Edit,
            """{"section":{"heading":"Summary","content":"Old summary."}}""",
            Path.GetTempPath());

        Assert.False(renamed.IsError, renamed.Content);
        Assert.Contains("title", renamed.Content, StringComparison.Ordinal);
        var edit = Assert.Single(platform.Edits);
        Assert.Equal("Add the feature", edit.Title);
        Assert.Null(edit.Body);
        Assert.False(unchanged.IsError, unchanged.Content);
        Assert.Contains("nothing was written", unchanged.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"body":"x","section":{"heading":"Testing","content":"y"}}""")]
    [InlineData("""{"body":"   "}""")]
    [InlineData("""{"title":"  "}""")]
    [InlineData("""{"title":"two\nlines"}""")]
    [InlineData("""{"section":{"heading":"Testing"}}""")]
    [InlineData("""{"section":{"heading":"##","content":"x"}}""")]
    [InlineData("""{"section":"Testing"}""")]
    [InlineData("""{"summary":"x"}""")]
    public async Task Edit_rejects_bad_arguments_before_touching_the_platform(string json)
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var result = await PrLifecycleTestHelpers.InvokeAsync(tools.Edit, json, Path.GetTempPath());

        Assert.True(result.IsError);
        Assert.Equal(0, platform.ViewCount);
        Assert.Empty(platform.Edits);
    }

    [Fact]
    public async Task Edit_without_a_pull_request_or_without_gh_is_a_named_error_with_a_next_step()
    {
        var platform = new PrLifecycleFakePlatform();
        var tools = PrLifecycleTestHelpers.Tools(platform);

        var none = await PrLifecycleTestHelpers.InvokeAsync(tools.Edit, """{"title":"x"}""", Path.GetTempPath());
        platform.Availability = new PrPlatformAvailability(PrPlatformState.NotAuthenticated, "not logged in");
        var notReady = await PrLifecycleTestHelpers.InvokeAsync(tools.Edit, """{"title":"x"}""", Path.GetTempPath());

        Assert.True(none.IsError);
        Assert.StartsWith("PullRequestNotFound:", none.Content, StringComparison.Ordinal);
        Assert.Contains("Next:", none.Content, StringComparison.Ordinal);
        Assert.Contains("pr_create", none.Content, StringComparison.Ordinal);
        Assert.True(notReady.IsError);
        Assert.StartsWith("AuthenticationFailed:", notReady.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Body_editor_is_reusable_for_a_screenshots_section()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        var editor = new PrBodyEditor(platform);

        var result = await editor.ReplaceSectionAsync(Path.GetTempPath(), "Screenshots", "![after](https://example.test/a.png)");
        var replaced = await editor.ReplaceBodyAsync(Path.GetTempPath(), "## Summary\nAll new.\n");

        Assert.True(result.Success);
        Assert.Equal("![after](https://example.test/a.png)", PrBodyRenderer.TryGetSection(result.Value!.Body, "Screenshots"));
        Assert.Contains("verified", result.Status.Message, StringComparison.Ordinal);
        Assert.True(replaced.Success);
        Assert.Equal("## Summary\nAll new.\n", platform.Current!.Body);
        await Assert.ThrowsAsync<ArgumentException>(() => editor.EditAsync(Path.GetTempPath(), null, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.ReplaceSectionAsync(Path.GetTempPath(), " ", "x"));
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_a_tool_error()
    {
        var platform = new PrLifecycleFakePlatform { Current = PrLifecycleTestHelpers.Pr() };
        platform.BeforeView = _ => throw new OperationCanceledException();
        var tools = PrLifecycleTestHelpers.Tools(platform);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await PrLifecycleTestHelpers.InvokeAsync(tools.View, "{}", Path.GetTempPath()));
    }
}
