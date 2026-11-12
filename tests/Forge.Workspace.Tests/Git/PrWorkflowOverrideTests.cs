using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>The <c>workflowText</c> hook of <see cref="PrTools"/>: a user override of one <c>pr_workflow</c> phase.</summary>
public sealed class PrWorkflowOverrideTests
{
    private static PrTools ToolsWith(Func<string, string, string?>? workflowText) =>
        new(
            new GitCli(),
            new PrLifecycleFakePlatform(),
            new GitToolSettings(),
            new GitReviewLedger(),
            new PrLifecycleFakeTodos(),
            workflowText);

    private static Task<ToolResult> Read(PrTools tools, string phase, string workingDirectory) =>
        PrLifecycleTestHelpers.InvokeAsync(tools.Workflow, $$"""{"phase":"{{phase}}"}""", workingDirectory);

    [Fact]
    public async Task An_override_is_returned_verbatim_instead_of_the_built_in_text()
    {
        var tools = ToolsWith((phase, _) => phase == "creating" ? "  MY CREATING TEXT\n" : null);

        var result = await Read(tools, "creating", Path.GetTempPath());

        Assert.False(result.IsError);
        Assert.Equal("  MY CREATING TEXT\n", result.Content);
    }

    [Fact]
    public async Task The_callback_gets_the_normalized_phase_and_the_calls_working_directory()
    {
        var calls = new List<(string Phase, string Directory)>();
        var tools = ToolsWith((phase, directory) =>
        {
            calls.Add((phase, directory));
            return "x";
        });
        var workspace = Path.Combine(Path.GetTempPath(), "forge-pr-workflow-ws");

        await Read(tools, " Review-Comments ", workspace);

        Assert.Equal([("review-comments", workspace)], calls);
    }

    [Fact]
    public async Task Only_the_overridden_phase_changes_and_other_phases_serve_the_built_in_text()
    {
        var tools = ToolsWith((phase, _) => phase == "body" ? "OVERRIDDEN BODY" : null);

        var body = await Read(tools, "body", Path.GetTempPath());
        var checks = await Read(tools, "checks", Path.GetTempPath());

        Assert.Equal("OVERRIDDEN BODY", body.Content);
        Assert.True(PrWorkflowText.TryGet("checks", out var builtIn));
        Assert.Equal(builtIn, checks.Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t\n")]
    public async Task A_null_or_blank_override_falls_back_to_the_built_in_text(string? blank)
    {
        var tools = ToolsWith((_, _) => blank);

        var result = await Read(tools, "syncing", Path.GetTempPath());

        Assert.False(result.IsError);
        Assert.True(PrWorkflowText.TryGet("syncing", out var builtIn));
        Assert.Equal(builtIn, result.Content);
    }

    [Fact]
    public async Task Without_a_hook_the_built_in_text_is_served()
    {
        var tools = ToolsWith(null);

        var result = await Read(tools, "screenshots", Path.GetTempPath());

        Assert.True(PrWorkflowText.TryGet("screenshots", out var builtIn));
        Assert.Equal(builtIn, result.Content);
    }

    [Fact]
    public async Task The_hook_is_asked_on_every_call_so_an_edit_applies_to_the_next_read()
    {
        var current = "first";
        var tools = ToolsWith((_, _) => current);

        var first = await Read(tools, "creating", Path.GetTempPath());
        current = "second";
        var second = await Read(tools, "creating", Path.GetTempPath());
        current = "";
        var third = await Read(tools, "creating", Path.GetTempPath());

        Assert.Equal("first", first.Content);
        Assert.Equal("second", second.Content);
        Assert.True(PrWorkflowText.TryGet("creating", out var builtIn));
        Assert.Equal(builtIn, third.Content);
    }

    [Fact]
    public async Task An_unknown_phase_is_still_an_error_and_never_reaches_the_hook()
    {
        var asked = 0;
        var tools = ToolsWith((_, _) =>
        {
            asked++;
            return "override";
        });

        var unknown = await Read(tools, "merging", Path.GetTempPath());
        var missing = await PrLifecycleTestHelpers.InvokeAsync(tools.Workflow, "{}", Path.GetTempPath());

        Assert.True(unknown.IsError);
        Assert.Contains("merging", unknown.Content, StringComparison.Ordinal);
        Assert.Contains("review-comments", unknown.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("override", unknown.Content, StringComparison.Ordinal);
        Assert.True(missing.IsError);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Every_phase_can_be_overridden()
    {
        var tools = ToolsWith((phase, _) => "override:" + phase);

        foreach (var phase in PrWorkflowText.Phases)
        {
            var result = await Read(tools, phase, Path.GetTempPath());
            Assert.False(result.IsError);
            Assert.Equal("override:" + phase, result.Content);
        }
    }
}
