using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Pure tests for the grouped plan rendering of <see cref="PrBodyRenderer"/>: group order, per-group
/// numbering, heading fallbacks, empty-group elision and the loose queue. The section helpers live in
/// <see cref="PrBodyRendererSmokeTests"/>.</summary>
public sealed class PrBodyRendererGroupTests
{
    private static TodoGroup Group(string id, string title, int order) =>
        new(id, "session-1", title, order, DateTimeOffset.UnixEpoch);

    private static TodoItem Step(string id, string title, int order, string? groupId, TodoStatus status = TodoStatus.Done, string? summary = null, string? verification = null, string? commitId = null) =>
        new(id, "session-1", title, status, null, order, DateTimeOffset.UnixEpoch, null, summary, verification, commitId, GroupId: groupId);

    [Fact]
    public void Groups_render_in_group_order_with_steps_renumbered_from_one_each()
    {
        var groups = new[]
        {
            Group("g-b", "Second", 1),
            Group("g-a", "First", 0),
        };
        var steps = new[]
        {
            Step("b2", "B two", 1, "g-b"),
            Step("a2", "A two", 1, "g-a", summary: "second"),
            Step("b1", "B one", 0, "g-b"),
            Step("a1", "A one", 0, "g-a"),
        };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.Equal(
            "### First\n\n1. **A one**\n\n2. **A two**\n   - second\n\n### Second\n\n1. **B one**\n\n2. **B two**",
            plan);
    }

    [Fact]
    public void Group_title_line_breaks_are_flattened_to_single_line_headings()
    {
        var groups = new[] { Group("g-1", "Line one\r\nLine two\nLine three", 0) };
        var steps = new[] { Step("s-1", "Step", 0, "g-1") };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.StartsWith("### Line one Line two Line three\n\n1. **Step**", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", plan, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_group_titles_fall_back_to_the_group_id(string title)
    {
        var groups = new[] { Group("g-fallback", title, 0) };
        var steps = new[] { Step("s-1", "Step", 0, "g-fallback") };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.StartsWith("### g-fallback\n\n1. **Step**", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_groups_emit_no_section_while_loose_steps_still_render()
    {
        var groups = new[]
        {
            Group("g-empty", "Empty", 0),
            Group("g-full", "Full", 1),
        };
        var steps = new[]
        {
            Step("s-1", "Only step", 0, "g-full"),
            Step("l-1", "Loose", 0, null),
        };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.DoesNotContain("### Empty", plan, StringComparison.Ordinal);
        Assert.Equal(
            "### Full\n\n1. **Only step**\n\n### Other steps\n\n1. **Loose**",
            plan);
    }

    [Fact]
    public void Groups_without_any_steps_add_no_plan_section()
    {
        var summary = new PlanSummary("s", "## Summary\nDoes a thing.", DateTimeOffset.UnixEpoch);
        var groups = new[] { Group("g-empty", "Empty", 0) };

        Assert.Equal(string.Empty, PrBodyRenderer.Render(null, [], [Group("g-empty", "Empty", 0)]));
        var body = PrBodyRenderer.Render(summary, [], groups);

        Assert.Equal("Does a thing.", PrBodyRenderer.TryGetSection(body, "Summary"));
        Assert.Null(PrBodyRenderer.TryGetSection(body, "Plan"));
    }

    [Fact]
    public void Paused_steps_carry_the_paused_marker_while_done_steps_carry_none()
    {
        var groups = new[] { Group("g-1", "Plan", 0) };
        var steps = new[]
        {
            Step("s-paused", "Waiting", 0, "g-1", TodoStatus.Paused),
            Step("s-done", "Finished", 1, "g-1", TodoStatus.Done, commitId: "abcdef0"),
            Step("s-pending", "Later", 0, null, TodoStatus.Pending),
            Step("s-running", "Now", 1, null, TodoStatus.InProgress),
        };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.Contains("1. **Waiting** _(paused)_", plan, StringComparison.Ordinal);
        Assert.Contains("2. **Finished** (`abcdef0`)", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Finished** _(", plan, StringComparison.Ordinal);
        Assert.Contains("1. **Later** _(pending)_", plan, StringComparison.Ordinal);
        Assert.Contains("2. **Now** _(in progress)_", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_whose_group_is_missing_fall_back_to_other_steps_after_the_groups()
    {
        var groups = new[] { Group("g-1", "Plan", 0) };
        var steps = new[]
        {
            Step("s-1", "Grouped", 0, "g-1"),
            Step("s-orphan", "Orphan", 5, "missing-group", summary: "kept"),
            Step("l-1", "Loose", 0, null),
        };

        var plan = PrBodyRenderer.TryGetSection(PrBodyRenderer.Render(null, steps, groups), "Plan")!;

        Assert.Equal(
            "### Plan\n\n1. **Grouped**\n\n### Other steps\n\n1. **Loose**\n\n2. **Orphan**\n   - kept",
            plan);
    }
}
