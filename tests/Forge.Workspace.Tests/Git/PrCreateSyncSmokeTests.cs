using System.Buffers;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// A bare origin with <c>main</c>, a seed clone that moves <c>main</c> and a work clone on <c>forge/feature</c> with one
/// commit, plus the fake platform and the real <see cref="PrTools"/> wired to a real <see cref="GitCli"/>.
/// </summary>
internal sealed class PrLifecycleFixture : IDisposable
{
    public const string Branch = "forge/feature";

    private readonly RemoteSandbox sandbox = new();

    public PrLifecycleFixture()
    {
        Seed = sandbox.Clone("seed");
        sandbox.CommitFile(Seed, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(Seed, "push", "-q", "-u", "origin", "main");

        Work = sandbox.Clone("work");
        IsolatedGit.RunChecked(Work, "checkout", "-q", "-b", Branch);
        sandbox.CommitFile(Work, "feature.txt", "feature\n", "add feature");

        Tools = PrLifecycleTestHelpers.Tools(Platform, Todos, Ledger);
    }

    public string Seed { get; }

    public string Work { get; }

    public string Origin => sandbox.Origin;

    public PrLifecycleFakePlatform Platform { get; } = new();

    public PrLifecycleFakeTodos Todos { get; } = new();

    public GitReviewLedger Ledger { get; } = new();

    public PrTools Tools { get; }

    public string Head => IsolatedGit.Head(Work);

    /// <summary>A commit on the work branch.</summary>
    public string Commit(string file, string content, string message) => sandbox.CommitFile(Work, file, content, message);

    /// <summary>A commit on <c>main</c>, pushed to origin (the work clone does not know about it until it fetches).</summary>
    public string AdvanceMain(string file, string content, string message)
    {
        var sha = sandbox.CommitFile(Seed, file, content, message);
        IsolatedGit.RunChecked(Seed, "push", "-q", "origin", "main");
        return sha;
    }

    /// <summary>A commit on a new <c>develop</c> branch, pushed to origin.</summary>
    public string AdvanceDevelop(string file, string content, string message)
    {
        IsolatedGit.RunChecked(Seed, "checkout", "-q", "-b", "develop");
        var sha = sandbox.CommitFile(Seed, file, content, message);
        IsolatedGit.RunChecked(Seed, "push", "-q", "origin", "develop");
        IsolatedGit.RunChecked(Seed, "checkout", "-q", "main");
        return sha;
    }

    /// <summary>Publishes the work branch the way a user would, with the upstream set.</summary>
    public void PushBranch() => IsolatedGit.RunChecked(Work, "push", "-q", "-u", "origin", Branch);

    /// <summary>The tip of a branch on origin, or null when the branch is not there.</summary>
    public string? OriginTip(string branch)
    {
        var (exitCode, output, _) = IsolatedGit.Run(Origin, "rev-parse", "--verify", "-q", "refs/heads/" + branch);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Another developer pushes a commit to the work branch's name on origin (on top of it when it exists there).</summary>
    public string PushFromElsewhere(string file)
    {
        var other = sandbox.Clone("other");
        if (OriginTip(Branch) is null)
        {
            IsolatedGit.RunChecked(other, "checkout", "-q", "-b", Branch);
        }
        else
        {
            IsolatedGit.RunChecked(other, "checkout", "-q", "-b", Branch, "--track", "origin/" + Branch);
        }

        sandbox.CommitFile(other, file, "other\n", "other work");
        IsolatedGit.RunChecked(other, "push", "-q", "origin", Branch);
        return IsolatedGit.Head(other);
    }

    /// <summary>Makes origin refuse every push, like a server-side policy would.</summary>
    public void RejectPushes()
    {
        var hook = Path.Combine(Origin, "hooks", "pre-receive");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, "#!/bin/sh\necho 'rejected by policy' >&2\nexit 1\n", new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Records that the full diff was read at the current HEAD, the way <c>git_diff</c> does.</summary>
    public void MarkReviewed() => Ledger.RecordReviewed(PrLifecycleTestHelpers.Session, Work, Head);

    public Task<ToolResult> CreateAsync(string json, CancellationToken ct = default) => InvokeAsync(Tools.Create, json, ct);

    public Task<ToolResult> SyncAsync(CancellationToken ct = default) => InvokeAsync(Tools.Sync, "{}", ct);

    private Task<ToolResult> InvokeAsync(ITool tool, string json, CancellationToken ct) =>
        tool.InvokeAsync(
            new ToolCall("call-1", tool.Definition.Name, json),
            new ToolContext(PrLifecycleTestHelpers.Session, Work),
            ct).AsTask();

    public void Dispose() => sandbox.Dispose();
}

/// <summary>
/// <c>pr_create</c> and <c>pr_sync</c> against real repositories (a bare origin on disk) and the fake platform: every gate
/// refuses without side effects, the lease and the unseen-commit guard protect the remote, conflicts stop the sync.
/// </summary>
public sealed class PrCreateSyncSmokeTests
{
    private const string Branch = PrLifecycleFixture.Branch;

    private const string GoodBody =
        "## Summary\nAdds a feature.\n\n## Testing\nRan `dotnet test tests/Forge.Workspace.Tests`: 412 passed, 0 failed.\n";

    private static string Args(string title = "Add feature", string? body = GoodBody, string? baseName = null, bool? draft = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("title", title);
            if (body is not null)
            {
                writer.WriteString("body", body);
            }

            if (baseName is not null)
            {
                writer.WriteString("base", baseName);
            }

            if (draft is { } isDraft)
            {
                writer.WriteBoolean("draft", isDraft);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Short(string sha) => sha[..7];

    private static string RunGit(PrLifecycleFixture fixture, params string[] arguments) =>
        IsolatedGit.RunChecked(fixture.Work, arguments).Trim();

    private static void AssertNothingPublished(PrLifecycleFixture fixture)
    {
        Assert.Empty(fixture.Platform.Creates);
        Assert.Null(fixture.OriginTip(Branch));
    }

    // ----------------------------------------------------------------------------------------------- pr_create: success

    [Fact]
    public async Task Create_pushes_the_branch_with_upstream_and_a_lease_and_opens_the_pull_request()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Created pull request #7: https://example.test/acme/forge/pull/7", result.Content, StringComparison.Ordinal);
        Assert.Contains("(ready for review)", result.Content, StringComparison.Ordinal);
        Assert.Contains("new branch", result.Content, StringComparison.Ordinal);
        Assert.Contains("pr_workflow", result.Content, StringComparison.Ordinal);
        var created = Assert.Single(fixture.Platform.Creates);
        Assert.Equal("Add feature", created.Title);
        Assert.Equal(GoodBody, created.Body);
        Assert.Equal("main", created.Base);
        Assert.Equal(Branch, created.Head);
        Assert.False(created.Draft);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
        Assert.Equal("origin/" + Branch, IsolatedGit.UpstreamOf(fixture.Work, Branch));
        Assert.Contains(Branch, fixture.Platform.ViewSelectors);
    }

    [Fact]
    public async Task Create_defaults_the_body_to_the_plan_summary_and_steps_and_strips_the_remote_prefix_from_base()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Todos.Summary = new PlanSummary(
            PrLifecycleTestHelpers.Session,
            "## Summary\nAdds a feature.\n\n## Testing\nRan `dotnet test`: 87 passed.",
            DateTimeOffset.UnixEpoch);
        fixture.Todos.Items.Add(new TodoItem(
            "id-1", PrLifecycleTestHelpers.Session, "Add the model", TodoStatus.Done, null, 1, DateTimeOffset.UnixEpoch, null,
            "Adds the model.", "dotnet test: 12 passed", "abcdef1234567"));

        var result = await fixture.CreateAsync(Args(body: null, baseName: "origin/main", draft: true));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("(draft)", result.Content, StringComparison.Ordinal);
        Assert.Contains("the plan summary and 1 step(s)", result.Content, StringComparison.Ordinal);
        var created = Assert.Single(fixture.Platform.Creates);
        Assert.True(created.Draft);
        Assert.Equal("main", created.Base);
        Assert.Contains("1. **Add the model** (`abcdef1`)", created.Body, StringComparison.Ordinal);
        Assert.Equal("Ran `dotnet test`: 87 passed.", PrBodyRenderer.TryGetSection(created.Body, "Testing"));
        Assert.NotNull(PrBodyRenderer.TryGetSection(created.Body, "Plan"));
    }

    [Fact]
    public async Task Create_accepts_the_review_that_the_real_git_diff_tool_recorded_and_refuses_partial_reads()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        var diff = new GitTools(new GitCli(), new GitToolSettings(), fixture.Ledger).Diff;
        var context = new ToolContext(PrLifecycleTestHelpers.Session, fixture.Work);

        var statOnly = await diff.InvokeAsync(new ToolCall("c1", "git_diff", """{"stat_only":true}"""), context, CancellationToken.None);
        var onePath = await diff.InvokeAsync(new ToolCall("c2", "git_diff", """{"paths":["feature.txt"]}"""), context, CancellationToken.None);
        Assert.False(statOnly.IsError, statOnly.Content);
        Assert.False(onePath.IsError, onePath.Content);

        var refused = await fixture.CreateAsync(Args());
        Assert.True(refused.IsError);
        Assert.Contains("gate 3", refused.Content, StringComparison.Ordinal);

        var full = await diff.InvokeAsync(new ToolCall("c3", "git_diff", "{}"), context, CancellationToken.None);
        Assert.False(full.IsError, full.Content);
        var created = await fixture.CreateAsync(Args());

        Assert.False(created.IsError, created.Content);
        Assert.Single(fixture.Platform.Creates);
    }

    // ------------------------------------------------------------------------------------------- pr_create: the gates

    [Fact]
    public async Task Gate_1_refuses_untracked_files_and_changes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        File.WriteAllText(Path.Combine(fixture.Work, "scratch.txt"), "debug\n");
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("pr_create refused (gate 1: clean working tree)", result.Content, StringComparison.Ordinal);
        Assert.Contains("scratch.txt (untracked)", result.Content, StringComparison.Ordinal);
        Assert.Contains("Next:", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_1_refuses_uncommitted_changes_to_tracked_files()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        File.WriteAllText(Path.Combine(fixture.Work, "base.txt"), "edited\n");
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("base.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("(untracked)", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_2_refuses_when_the_base_moved_and_fetches_fresh_to_find_out()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        var newMain = fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        var staleRef = RunGit(fixture, "rev-parse", "origin/main");
        Assert.NotEqual(newMain, staleRef);
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 2: up to date with the base", result.Content, StringComparison.Ordinal);
        Assert.Contains("origin/main has 1 commit(s) that HEAD does not contain", result.Content, StringComparison.Ordinal);
        Assert.Contains("pr_sync", result.Content, StringComparison.Ordinal);
        Assert.Equal(newMain, RunGit(fixture, "rev-parse", "origin/main"));
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_2_refuses_a_branch_with_nothing_beyond_the_base()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        RunGit(fixture, "reset", "-q", "--hard", "origin/main");
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("no commits beyond origin/main", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_2_names_a_base_that_does_not_exist_on_the_remote()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();

        var result = await fixture.CreateAsync(Args(baseName: "no-such-branch"));

        Assert.True(result.IsError);
        Assert.Contains("gate 2", result.Content, StringComparison.Ordinal);
        Assert.Contains("origin/no-such-branch does not exist", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_3_refuses_when_the_diff_was_never_read()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 3: self-review of the full diff", result.Content, StringComparison.Ordinal);
        Assert.Contains("has not been read in this session", result.Content, StringComparison.Ordinal);
        Assert.Contains("only enforce that the diff was re-read", result.Content, StringComparison.Ordinal);
        Assert.Contains("git_diff", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_3_refuses_a_review_of_an_older_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        var reviewedAt = fixture.Head;
        fixture.MarkReviewed();
        var newHead = fixture.Commit("more.txt", "more\n", "more work");

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 3", result.Content, StringComparison.Ordinal);
        Assert.Contains($"last read at {Short(reviewedAt)}", result.Content, StringComparison.Ordinal);
        Assert.Contains($"HEAD is now {Short(newHead)}", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_3_does_not_accept_a_review_recorded_by_another_session()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.Ledger.RecordReviewed("another-session", fixture.Work, fixture.Head);

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate 3", result.Content, StringComparison.Ordinal);
        Assert.Contains("has not been read in this session", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Theory]
    [InlineData("## Summary\nAdds a feature.\n\n## Testing\nAll tests pass.\n", "only says that testing happened")]
    [InlineData("## Summary\nAdds a feature.\n\n## Testing\nn/a\n", "only says that testing happened")]
    [InlineData("## Summary\nAdds a feature.\n\n## Testing\n测试通过\n", "only says that testing happened")]
    [InlineData("## Summary\nAdds a feature.\n", "no '## Testing' section")]
    [InlineData("just a sentence without any sections", "no '## Testing' section")]
    [InlineData("## Testing\n\n## Notes\nSomething else.\n", "empty")]
    public async Task Gate_4_refuses_a_missing_empty_or_platitude_testing_section(string body, string expectedReason)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args(body: body));

        Assert.True(result.IsError);
        Assert.Contains("pr_create refused (gate 4: testing evidence)", result.Content, StringComparison.Ordinal);
        Assert.Contains(expectedReason, result.Content, StringComparison.Ordinal);
        Assert.Contains("the body argument", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gate_4_judges_the_generated_body_when_no_body_is_passed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args(body: null));

        Assert.True(result.IsError);
        Assert.Contains("gate 4", result.Content, StringComparison.Ordinal);
        Assert.Contains("the plan summary and 0 step(s)", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Gates_are_reported_in_order_one_two_three_four()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        File.WriteAllText(Path.Combine(fixture.Work, "scratch.txt"), "debug\n");
        const string Platitude = "## Testing\nAll tests pass.\n";

        var first = await fixture.CreateAsync(Args(body: Platitude));
        File.Delete(Path.Combine(fixture.Work, "scratch.txt"));
        var second = await fixture.CreateAsync(Args(body: Platitude));
        IsolatedGit.RunChecked(fixture.Work, "rebase", "-q", "origin/main");
        var third = await fixture.CreateAsync(Args(body: Platitude));
        fixture.MarkReviewed();
        var fourth = await fixture.CreateAsync(Args(body: Platitude));

        Assert.Contains("gate 1", first.Content, StringComparison.Ordinal);
        Assert.Contains("gate 2", second.Content, StringComparison.Ordinal);
        Assert.Contains("gate 3", third.Content, StringComparison.Ordinal);
        Assert.Contains("gate 4", fourth.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_refuses_a_body_longer_than_the_platform_accepts()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        var body = GoodBody + "\n## Notes\n" + new string('x', PrBodyEditor.MaxBodyLength);

        var result = await fixture.CreateAsync(Args(body: body));

        Assert.True(result.IsError);
        Assert.Contains("at most 65536", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_refuses_to_open_a_pull_request_from_the_base_branch_or_a_detached_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        RunGit(fixture, "checkout", "-q", "--detach");
        var detached = await fixture.CreateAsync(Args());
        RunGit(fixture, "checkout", "-q", "main");
        var onBase = await fixture.CreateAsync(Args());

        Assert.True(detached.IsError);
        Assert.Contains("detached", detached.Content, StringComparison.Ordinal);
        Assert.True(onBase.IsError);
        Assert.Contains("is the base branch itself", onBase.Content, StringComparison.Ordinal);
        Assert.Empty(fixture.Platform.Creates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("first line\nsecond line")]
    public async Task Create_refuses_a_blank_or_multi_line_title(string title)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args(title));

        Assert.True(result.IsError);
        Assert.Contains("title", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    // ----------------------------------------------------------------------------- pr_create: re-check, platform, remote

    [Fact]
    public async Task Create_rechecks_head_after_the_gates_and_stops_when_it_moved()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        var before = fixture.Head;
        fixture.Todos.Summary = new PlanSummary(PrLifecycleTestHelpers.Session, GoodBody, DateTimeOffset.UnixEpoch);
        fixture.Todos.OnPlanSummary = () => fixture.Commit("late.txt", "late\n", "late commit");

        var result = await fixture.CreateAsync(Args(body: null));

        Assert.True(result.IsError);
        Assert.Contains("state changed while checking", result.Content, StringComparison.Ordinal);
        Assert.Contains($"HEAD moved from {Short(before)} to {Short(fixture.Head)}", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_refuses_a_commit_that_lands_between_the_last_gate_and_the_push()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();

        // The platform read is the last step before the push; a commit landing there is exactly the gate-to-push race.
        fixture.Platform.BeforeView = _ => fixture.Commit("late.txt", "late\n", "late commit");

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("gate-to-push race", result.Content);
        Assert.Contains("was never reviewed", result.Content);
        Assert.Contains("git_diff", result.Content);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_rechecks_the_tree_after_the_gates_and_stops_when_a_file_appeared()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Todos.Summary = new PlanSummary(PrLifecycleTestHelpers.Session, GoodBody, DateTimeOffset.UnixEpoch);
        fixture.Todos.OnPlanSummary = () => File.WriteAllText(Path.Combine(fixture.Work, "late.txt"), "late\n");

        var result = await fixture.CreateAsync(Args(body: null));

        Assert.True(result.IsError);
        Assert.Contains("gate 1", result.Content, StringComparison.Ordinal);
        Assert.Contains("late.txt (untracked)", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_reports_an_existing_open_pull_request_instead_of_opening_another()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("a pull request already exists", result.Content, StringComparison.Ordinal);
        Assert.Contains("#5", result.Content, StringComparison.Ordinal);
        Assert.Contains("https://example.test/acme/forge/pull/5", result.Content, StringComparison.Ordinal);
        Assert.Contains("pr_edit", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_is_not_blocked_by_a_closed_pull_request_of_the_same_branch_name()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr(state: PrState.Closed);

        var result = await fixture.CreateAsync(Args());

        Assert.False(result.IsError, result.Content);
        Assert.Single(fixture.Platform.Creates);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Create_returns_a_failed_pull_request_lookup_instead_of_guessing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        var broken = new BrokenViewPlatform(fixture.Platform);
        var tools = new PrTools(new GitCli(), broken, new GitToolSettings(), fixture.Ledger, fixture.Todos);

        var result = await tools.Create.InvokeAsync(
            new ToolCall("c", "pr_create", Args()),
            new ToolContext(PrLifecycleTestHelpers.Session, fixture.Work),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("AuthenticationFailed", result.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Theory]
    [InlineData(PrPlatformState.NotInstalled, "not available")]
    [InlineData(PrPlatformState.NotAuthenticated, "not logged in")]
    public async Task Create_says_so_when_the_platform_cli_is_not_usable_before_touching_anything(PrPlatformState state, string expected)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Platform.Availability = new PrPlatformAvailability(state, "gh: " + state);

        var create = await fixture.CreateAsync(Args());

        Assert.True(create.IsError);
        Assert.Contains(expected, create.Content, StringComparison.Ordinal);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_never_overwrites_a_diverged_remote_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        var theirs = fixture.PushFromElsewhere("theirs.txt");
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("NonFastForward", result.Content, StringComparison.Ordinal);
        Assert.Contains("never overwrites an existing remote branch", result.Content, StringComparison.Ordinal);
        Assert.Equal(theirs, fixture.OriginTip(Branch));
        Assert.Empty(fixture.Platform.Creates);
    }

    [Fact]
    public async Task Create_fast_forwards_an_older_remote_branch_with_a_lease_on_the_observed_tip()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var published = fixture.Head;
        fixture.Commit("more.txt", "more\n", "more work");
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.False(result.IsError, result.Content);
        Assert.Contains($"lease {Short(published)}", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("new branch", result.Content, StringComparison.Ordinal);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
        Assert.Single(fixture.Platform.Creates);
    }

    [Fact]
    public async Task Create_sets_the_upstream_of_a_branch_that_is_already_on_the_remote_at_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        RunGit(fixture, "push", "-q", "origin", Branch);
        Assert.Null(IsolatedGit.UpstreamOf(fixture.Work, Branch));
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.False(result.IsError, result.Content);
        Assert.Equal("origin/" + Branch, IsolatedGit.UpstreamOf(fixture.Work, Branch));
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
        Assert.Single(fixture.Platform.Creates);
    }

    [Fact]
    public async Task Create_pushes_nothing_when_the_branch_is_already_published_and_tracked_at_head()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.False(result.IsError, result.Content);
        Assert.Contains("nothing to push", result.Content, StringComparison.Ordinal);
        Assert.Single(fixture.Platform.Creates);
    }

    [Fact]
    public async Task Create_keeps_the_pushed_branch_when_the_platform_fails_and_a_retry_succeeds()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        fixture.Platform.CreateFailure = PrResult<PrReference>.Fail(GitRemoteFailureKind.AuthenticationFailed, "bad credentials");

        var failed = await fixture.CreateAsync(Args());

        Assert.True(failed.IsError);
        Assert.Contains("AuthenticationFailed", failed.Content, StringComparison.Ordinal);
        Assert.Contains("already pushed", failed.Content, StringComparison.Ordinal);
        Assert.Contains("only the pull request is missing", failed.Content, StringComparison.Ordinal);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));

        fixture.Platform.CreateFailure = null;
        var retried = await fixture.CreateAsync(Args());

        Assert.False(retried.IsError, retried.Content);
        Assert.Contains("nothing to push", retried.Content, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Platform.Creates.Count);
    }

    [Fact]
    public async Task Create_reports_a_rejected_push_and_does_not_open_a_pull_request()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.RejectPushes();
        fixture.MarkReviewed();

        var result = await fixture.CreateAsync(Args());

        Assert.True(result.IsError);
        Assert.Contains("rejected", result.Content, StringComparison.OrdinalIgnoreCase);
        AssertNothingPublished(fixture);
    }

    [Fact]
    public async Task Create_and_sync_propagate_cancellation()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.MarkReviewed();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateAsync(Args(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.SyncAsync(cancelled.Token));
        AssertNothingPublished(fixture);
    }

    // -------------------------------------------------------------------------------------------------------- pr_sync

    [Fact]
    public async Task Sync_rebases_a_branch_without_merge_commits_and_pushes_with_a_fresh_lease()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var oldHead = fixture.Head;
        var newMain = fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        var newHead = fixture.Head;
        Assert.NotEqual(oldHead, newHead);
        Assert.Contains("Base: origin/main (base of PR #5)", result.Content, StringComparison.Ordinal);
        Assert.Contains("Rebased forge/feature onto origin/main", result.Content, StringComparison.Ordinal);
        Assert.Contains("1 commit(s) behind", result.Content, StringComparison.Ordinal);
        Assert.Contains("so it was rebased", result.Content, StringComparison.Ordinal);
        Assert.Contains($"lease {Short(oldHead)}", result.Content, StringComparison.Ordinal);
        Assert.Contains($"{Short(oldHead)} -> {Short(newHead)}", result.Content, StringComparison.Ordinal);
        Assert.Contains("git_diff", result.Content, StringComparison.Ordinal);
        Assert.Equal(newMain, RunGit(fixture, "rev-parse", "HEAD~1"));
        Assert.Equal("0", RunGit(fixture, "rev-list", "--merges", "--count", "origin/main..HEAD"));
        Assert.Equal(newHead, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_merges_when_the_branch_already_has_merge_commits_and_pushes_without_force()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.AdvanceMain("a.txt", "a\n", "main a");
        RunGit(fixture, "fetch", "-q", "origin");
        RunGit(fixture, "merge", "-q", "--no-ff", "-m", "merge main", "origin/main");
        fixture.PushBranch();
        var oldHead = fixture.Head;
        var newMain = fixture.AdvanceMain("b.txt", "b\n", "main b");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Merged origin/main into forge/feature", result.Content, StringComparison.Ordinal);
        Assert.Contains("already had merge commits, so the base was merged in", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("lease", result.Content, StringComparison.Ordinal);
        Assert.Equal(oldHead, RunGit(fixture, "rev-parse", "HEAD^1"));
        Assert.Equal(newMain, RunGit(fixture, "rev-parse", "HEAD^2"));
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_does_nothing_to_history_when_the_base_is_already_contained()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var head = fixture.Head;
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("already contained in HEAD; nothing to integrate", result.Content, StringComparison.Ordinal);
        Assert.Contains("nothing to push", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("->", result.Content, StringComparison.Ordinal);
        Assert.Equal(head, fixture.Head);
        Assert.Equal(head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_pushes_a_branch_the_agent_already_rebased_by_hand()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var oldHead = fixture.Head;
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        RunGit(fixture, "fetch", "-q", "origin");
        RunGit(fixture, "rebase", "-q", "origin/main");
        var rebased = fixture.Head;
        Assert.NotEqual(oldHead, rebased);
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("already contained in HEAD", result.Content, StringComparison.Ordinal);
        Assert.Contains($"lease {Short(oldHead)}", result.Content, StringComparison.Ordinal);
        Assert.Equal(rebased, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_takes_the_base_from_the_open_pull_request()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        RunGit(fixture, "push", "-q", "-u", "origin", Branch);
        var develop = fixture.AdvanceDevelop("dev.txt", "d\n", "develop work");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr(baseBranch: "develop");

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Base: origin/develop (base of PR #5)", result.Content, StringComparison.Ordinal);
        Assert.Equal(develop, RunGit(fixture, "rev-parse", "HEAD~1"));
        Assert.Equal(0, IsolatedGit.Run(fixture.Work, "merge-base", "--is-ancestor", "origin/develop", "HEAD").ExitCode);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_falls_back_to_the_default_branch_and_says_so_when_the_platform_is_unavailable()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        fixture.Platform.Availability = new PrPlatformAvailability(PrPlatformState.NotInstalled, "gh: command not found");

        var result = await fixture.SyncAsync();

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Base: origin/main (default branch, no open pull request for forge/feature", result.Content, StringComparison.Ordinal);
        Assert.Contains("is not available", result.Content, StringComparison.Ordinal);
        Assert.Equal(fixture.Head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_stops_on_conflicts_lists_the_files_and_blocks_a_second_call_until_resolved()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.Commit("base.txt", "feature version\n", "feature edits base");
        fixture.PushBranch();
        var published = fixture.OriginTip(Branch);
        fixture.AdvanceMain("base.txt", "main version\n", "main edits base");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.True(result.IsError);
        Assert.Contains("Conflict:", result.Content, StringComparison.Ordinal);
        Assert.Contains("base.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("git log -p forge/feature..origin/main -- <file>", result.Content, StringComparison.Ordinal);
        Assert.Contains("git log -p origin/main..forge/feature -- <file>", result.Content, StringComparison.Ordinal);
        Assert.Contains("git_sync continue", result.Content, StringComparison.Ordinal);
        Assert.Contains("never take one side wholesale", result.Content, StringComparison.Ordinal);
        Assert.True(
            Directory.Exists(Path.Combine(fixture.Work, ".git", "rebase-merge"))
            || Directory.Exists(Path.Combine(fixture.Work, ".git", "rebase-apply")),
            "the rebase is left in progress for the agent to resolve");
        Assert.Equal(published, fixture.OriginTip(Branch));

        var again = await fixture.SyncAsync();
        fixture.MarkReviewed();
        var create = await fixture.CreateAsync(Args());

        Assert.True(again.IsError);
        Assert.Contains("pr_sync refused (an operation is in progress)", again.Content, StringComparison.Ordinal);
        Assert.Contains("rebase", again.Content, StringComparison.Ordinal);
        Assert.Contains("git_sync continue", again.Content, StringComparison.Ordinal);
        Assert.True(create.IsError);
        Assert.Contains("gate 1", create.Content, StringComparison.Ordinal);
        Assert.Contains("rebase is in progress", create.Content, StringComparison.Ordinal);
        Assert.Empty(fixture.Platform.Creates);
        Assert.Equal(published, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_refuses_uncommitted_tracked_changes_but_ignores_untracked_files()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        var head = fixture.Head;
        File.WriteAllText(Path.Combine(fixture.Work, "base.txt"), "edited\n");

        var dirty = await fixture.SyncAsync();

        Assert.True(dirty.IsError);
        Assert.Contains("pr_sync refused (uncommitted changes)", dirty.Content, StringComparison.Ordinal);
        Assert.Contains("base.txt", dirty.Content, StringComparison.Ordinal);
        Assert.Equal(head, fixture.Head);

        RunGit(fixture, "checkout", "-q", "--", "base.txt");
        File.WriteAllText(Path.Combine(fixture.Work, "scratch.txt"), "untracked\n");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var untrackedOnly = await fixture.SyncAsync();

        Assert.False(untrackedOnly.IsError, untrackedOnly.Content);
        Assert.NotEqual(head, fixture.Head);
        Assert.True(File.Exists(Path.Combine(fixture.Work, "scratch.txt")));
    }

    [Fact]
    public async Task Sync_refuses_to_force_over_commits_somebody_else_pushed()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var theirs = fixture.PushFromElsewhere("theirs.txt");
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.True(result.IsError);
        Assert.Contains("NonFastForward", result.Content, StringComparison.Ordinal);
        Assert.Contains("someone else pushed", result.Content, StringComparison.Ordinal);
        Assert.Contains("other work", result.Content, StringComparison.Ordinal);
        Assert.Contains("The local branch is already updated", result.Content, StringComparison.Ordinal);
        Assert.Contains("so it was rebased", result.Content, StringComparison.Ordinal);
        Assert.Equal(theirs, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_keeps_the_local_update_and_says_so_when_the_push_is_rejected()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var published = fixture.Head;
        fixture.AdvanceMain("main-only.txt", "m\n", "main moves");
        fixture.RejectPushes();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await fixture.SyncAsync();

        Assert.True(result.IsError);
        Assert.Contains("rejected", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The local branch is already updated", result.Content, StringComparison.Ordinal);
        Assert.NotEqual(published, fixture.Head);
        Assert.Equal(published, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_refuses_a_branch_without_a_pull_request_and_points_at_pr_create()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        fixture.PushBranch();
        var head = fixture.Head;

        var result = await fixture.SyncAsync();

        Assert.True(result.IsError);
        Assert.Contains("pr_sync refused (no pull request for this branch)", result.Content);
        Assert.Contains("pr_create", result.Content);
        Assert.Equal(head, fixture.Head);
        Assert.Equal(head, fixture.OriginTip(Branch));
    }

    [Fact]
    public async Task Sync_refuses_a_detached_head_and_the_base_branch_itself()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();

        RunGit(fixture, "checkout", "-q", "--detach");
        var detached = await fixture.SyncAsync();
        RunGit(fixture, "checkout", "-q", "main");
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var onBase = await fixture.SyncAsync();

        Assert.True(detached.IsError);
        Assert.Contains("detached", detached.Content, StringComparison.Ordinal);
        Assert.True(onBase.IsError);
        Assert.Contains("is the base branch itself", onBase.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"unexpected":1}""")]
    [InlineData("[]")]
    public async Task Sync_and_create_reject_unknown_or_malformed_arguments(string json)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new PrLifecycleFixture();
        var context = new ToolContext(PrLifecycleTestHelpers.Session, fixture.Work);

        var sync = await fixture.Tools.Sync.InvokeAsync(new ToolCall("c1", "pr_sync", json), context, CancellationToken.None);
        var create = await fixture.Tools.Create.InvokeAsync(new ToolCall("c2", "pr_create", json), context, CancellationToken.None);

        Assert.True(sync.IsError);
        Assert.True(create.IsError);
        AssertNothingPublished(fixture);
    }
}

/// <summary>A platform whose pull request lookup fails with something other than "not found".</summary>
internal sealed class BrokenViewPlatform(IPrPlatform inner) : IPrPlatform
{
    public string Name => inner.Name;

    public Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        inner.GetAvailabilityAsync(workingDirectory, ct);

    public Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
        Task.FromResult(PrResult<PullRequestInfo>.Fail(GitRemoteFailureKind.AuthenticationFailed, "token expired"));

    public Task<PrResult<PrReference>> CreatePullRequestAsync(string workingDirectory, string title, string body, string baseBranch, string headBranch, bool draft = false, CancellationToken ct = default) =>
        inner.CreatePullRequestAsync(workingDirectory, title, body, baseBranch, headBranch, draft, ct);

    public Task<PrResult<PrReference>> EditPullRequestAsync(string workingDirectory, int number, string? title = null, string? body = null, CancellationToken ct = default) =>
        inner.EditPullRequestAsync(workingDirectory, number, title, body, ct);

    public Task<PrResult<PrChecksSnapshot>> GetChecksAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        inner.GetChecksAsync(workingDirectory, number, ct);

    public Task<PrResult<PrMergeState>> GetMergeStateAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        inner.GetMergeStateAsync(workingDirectory, number, ct);

    public Task<PrResult<PrText>> GetFailedJobLogAsync(string workingDirectory, long? runId, long jobId, CancellationToken ct = default) =>
        inner.GetFailedJobLogAsync(workingDirectory, runId, jobId, ct);

    public Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        inner.GetReviewThreadsAsync(workingDirectory, number, ct);

    public Task<PrResult<PrText>> GetPullRequestDiffAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        inner.GetPullRequestDiffAsync(workingDirectory, number, ct);

    public Task<PrResult<PrReviewComment>> ReplyToReviewThreadAsync(string workingDirectory, string threadId, string body, CancellationToken ct = default) =>
        inner.ReplyToReviewThreadAsync(workingDirectory, threadId, body, ct);

    public Task<PrResult<PrThreadResolution>> ResolveReviewThreadAsync(string workingDirectory, string threadId, CancellationToken ct = default) =>
        inner.ResolveReviewThreadAsync(workingDirectory, threadId, ct);
}
