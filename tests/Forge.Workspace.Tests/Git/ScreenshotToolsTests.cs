using Forge.Abstractions;
using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <c>pr_screenshots</c> end to end against a real repository and the fake platform: the tool's side effect is
/// <see cref="ToolSideEffect.Execute"/> (it runs a command from the repository), the preconditions (declaration, open pull
/// request, argument shape) are refused before anything runs, a good run pairs the images and writes them into the
/// <c>## Screenshots</c> section, and every failure path pushes nothing and leaves no temporary worktree behind.
/// </summary>
public sealed class ScreenshotToolsTests
{
    [Fact]
    public void The_tool_is_one_pr_screenshots_call_with_the_execute_side_effect()
    {
        using var fixture = new ScreenshotFixture();
        var tools = NewTools(fixture);

        var tool = Assert.Single(tools.All);
        Assert.Equal("pr_screenshots", tool.Definition.Name);
        Assert.Equal(ToolSideEffect.Execute, tool.SideEffect);
        Assert.Same(tool, tools.Screenshots);
    }

    [Fact]
    public async Task It_captures_before_and_after_and_writes_the_section_into_the_description()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "screenshots");
        var bodyBefore = fixture.Platform.Current!.Body;

        var result = await PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Pushed 3 screen(s)", result.Content, StringComparison.Ordinal);
        Assert.Contains("home (before and after)", result.Content, StringComparison.Ordinal);
        Assert.Contains("removed (before only)", result.Content, StringComparison.Ordinal);
        Assert.Contains("extra (after only)", result.Content, StringComparison.Ordinal);
        Assert.Contains("https://example.test/acme/forge/pull/5", result.Content, StringComparison.Ordinal);

        // The images are on the assets branch under their paired names, and the section links to that commit.
        var assets = Assert.IsType<string>(fixture.OriginTip("assets/forge-feature"));
        Assert.Equal(
            ["extra-after.png", "home-after.png", "home-before.png", "removed-before.png"],
            IsolatedGit.RunChecked(fixture.Origin, "ls-tree", "--name-only", assets)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var body = fixture.Platform.Current!.Body;
        Assert.Contains("## Screenshots", body, StringComparison.Ordinal);
        Assert.Contains("## Summary", body, StringComparison.Ordinal);
        Assert.Contains(bodyBefore.Split('\n')[0], body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/acme/forge/blob/{assets}/home-before.png?raw=true", body, StringComparison.Ordinal);
        Assert.Contains($"https://github.com/acme/forge/blob/{assets}/extra-after.png?raw=true", body, StringComparison.Ordinal);
        Assert.Contains("_no image after the change_", body, StringComparison.Ordinal);

        // Nothing temporary is left: no worktree of the base, no capture directory.
        fixture.AssertNoTrace();
        Assert.Equal(string.Empty, fixture.Status());
    }

    [Fact]
    public async Task Without_a_declaration_or_a_command_it_says_it_cannot_capture_and_runs_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        var bodyBefore = fixture.Platform.Current!.Body;

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "pr_screenshots refused",
            ".forge/screenshots.json",
            "never fabricate an image");
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
        Assert.Equal(bodyBefore, fixture.Platform.Current!.Body);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_broken_declaration_is_named_and_nothing_runs()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.Write(".forge/screenshots.json", "{\"commnd\": \"call .\\\\capture.cmd\", \"output\": \"screenshots\"}");
        fixture.CommitAll("a declaration with a misspelt key");

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "commnd",
            "Fix .forge/screenshots.json");
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_pull_request_that_is_not_open_is_refused()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr(state: PrState.Merged);
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "screenshots");

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "is Merged",
            "Nothing was changed");
        Assert.Empty(fixture.Platform.Edits);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Without_a_pull_request_the_platform_failure_is_reported()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "screenshots");

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "PullRequestNotFound",
            "pr_create");
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_side_that_fails_reports_its_exit_code_and_output_and_publishes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WriteConfig(ScreenshotTestData.FailOn("before", 7, "the base cannot be captured"), "screenshots");
        var bodyBefore = fixture.Platform.Current!.Body;

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "exit code 7",
            "the base cannot be captured",
            "nothing was pushed and the description is unchanged");
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
        Assert.Equal(bodyBefore, fixture.Platform.Current!.Body);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_command_that_writes_no_png_file_reports_that_and_publishes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WriteConfig(ScreenshotTestData.Pick("exit /b 0", "true"), "screenshots");

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "wrote no PNG files");
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task It_publishes_existing_files_instead_of_capturing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WritePng("shots/home-before.png", "manual-before");
        fixture.WritePng("shots/home-after.png", "manual-after");
        fixture.WritePng("shots/extra.png", "manual-extra");

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            NewTools(fixture).Screenshots,
            """{"files":["shots/home-before.png","shots/home-after.png","shots/extra.png"]}""",
            fixture.Work);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("home (before and after)", result.Content, StringComparison.Ordinal);
        Assert.Contains("extra (after only)", result.Content, StringComparison.Ordinal);
        var body = fixture.Platform.Current!.Body;
        var assets = Assert.IsType<string>(fixture.OriginTip("assets/forge-feature"));
        Assert.Contains($"blob/{assets}/home-after.png?raw=true", body, StringComparison.Ordinal);
        Assert.Contains("_no image on the base_", body, StringComparison.Ordinal);

        // Publishing files captures nothing: no temporary worktree was ever created.
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task Files_are_refused_and_nothing_is_pushed_when_one_cannot_be_used()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        var bodyBefore = fixture.Platform.Current!.Body;

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            NewTools(fixture).Screenshots,
            """{"files":["shots/missing.png"]}""",
            fixture.Work);

        Assert.True(result.IsError);
        Assert.Contains("not an existing file", result.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing was pushed", result.Content, StringComparison.Ordinal);
        Assert.Null(fixture.OriginTip("assets/forge-feature"));
        Assert.Equal(bodyBefore, fixture.Platform.Current!.Body);
    }

    [Fact]
    public async Task It_removes_the_temporary_paths_once_the_description_holds_the_images()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        // The declaration is committed; the one-off script it names is not, so only the base worktree needs the overlay.
        fixture.WriteConfig(ScreenshotTestData.Pick("call .\\one-off.cmd", "sh one-off.sh"), "screenshots");
        fixture.WriteOneOffScript();

        var result = await PrLifecycleTestHelpers.InvokeAsync(
            NewTools(fixture).Screenshots,
            """{"cleanup_paths":["one-off.cmd","one-off.sh"]}""",
            fixture.Work);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("Cleanup: removed", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Work, "one-off.cmd")));
        Assert.False(File.Exists(Path.Combine(fixture.Work, "one-off.sh")));
        Assert.NotNull(fixture.OriginTip("assets/forge-feature"));
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task The_command_arguments_are_validated_before_anything_runs()
    {
        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        var tools = NewTools(fixture);

        var blank = await PrLifecycleTestHelpers.InvokeAsync(tools.Screenshots, """{"command":"   "}""", fixture.Work);
        var multiline = await PrLifecycleTestHelpers.InvokeAsync(tools.Screenshots, """{"command":"a\nb"}""", fixture.Work);
        var mixed = await PrLifecycleTestHelpers.InvokeAsync(
            tools.Screenshots,
            """{"files":["a.png"],"command":"x"}""",
            fixture.Work);
        var unknown = await PrLifecycleTestHelpers.InvokeAsync(tools.Screenshots, """{"nope":1}""", fixture.Work);

        Assert.True(blank.IsError);
        Assert.Contains("must not be blank", blank.Content, StringComparison.Ordinal);
        Assert.True(multiline.IsError);
        Assert.Contains("single line", multiline.Content, StringComparison.Ordinal);
        Assert.True(mixed.IsError);
        Assert.Contains("do not combine it", mixed.Content, StringComparison.Ordinal);
        Assert.True(unknown.IsError);
        Assert.Contains("nope", unknown.Content, StringComparison.Ordinal);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_rejected_push_reports_the_remote_failure_and_leaves_the_description_alone()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "screenshots");
        fixture.RejectPushes();
        var bodyBefore = fixture.Platform.Current!.Body;

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "rejected by policy");
        Assert.Equal(bodyBefore, fixture.Platform.Current!.Body);
        fixture.AssertNoTrace();
    }

    [Fact]
    public async Task A_description_the_platform_did_not_keep_is_reported_with_the_markdown_to_paste()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        OpenPullRequest(fixture);
        fixture.WriteConfig(ScreenshotTestData.CaptureCommand, "screenshots");
        fixture.Platform.DropEdits = true;

        var result = await ToolResultAssertions.RefusedAsync(
            PrLifecycleTestHelpers.InvokeAsync(NewTools(fixture).Screenshots, "{}", fixture.Work),
            "the images are on the remote already",
            "pr_edit (section Screenshots)",
            "| Screen | Before | After |");
        Assert.NotNull(fixture.OriginTip("assets/forge-feature"));
        fixture.AssertNoTrace();
    }

    private static ScreenshotTools NewTools(ScreenshotFixture fixture) =>
        new(fixture.Git, fixture.Platform, new GitToolSettings(), fixture.Options());

    private static void OpenPullRequest(ScreenshotFixture fixture) =>
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
}
