using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>Pure tests of the body renderer, the section helpers and the Testing-evidence check.</summary>
public sealed class PrBodyRendererSmokeTests
{
    private static TodoItem Step(
        string title,
        int order,
        TodoStatus status = TodoStatus.Done,
        string? summary = null,
        string? verification = null,
        string? commitId = null) =>
        new($"id-{order}", "session-1", title, status, null, order, DateTimeOffset.UnixEpoch, null, summary, verification, commitId);

    [Fact]
    public void Render_without_summary_and_steps_is_empty()
    {
        Assert.Equal(string.Empty, PrBodyRenderer.Render(null, []));
        Assert.Equal(string.Empty, PrBodyRenderer.Render(new PlanSummary("s", "   \n", DateTimeOffset.UnixEpoch), []));
    }

    [Fact]
    public void Render_with_summary_only_keeps_the_text_and_adds_no_plan()
    {
        var summary = new PlanSummary("s", "\n## Summary\r\nDoes a thing.\r\n\r\n## Testing\r\nRan it.\r\n\r\n", DateTimeOffset.UnixEpoch);

        var body = PrBodyRenderer.Render(summary, []);

        Assert.Equal("## Summary\nDoes a thing.\n\n## Testing\nRan it.\n", body);
        Assert.Null(PrBodyRenderer.TryGetSection(body, "Plan"));
    }

    [Fact]
    public void Render_lists_steps_in_plan_order_with_short_sha_summary_and_verification()
    {
        var summary = new PlanSummary("s", "## Summary\nDoes a thing.\n\n## Testing\nRan it.", DateTimeOffset.UnixEpoch);
        var steps = new[]
        {
            Step("Second step", 2, summary: "Wires it up.\nSecond line.", verification: "dotnet test: 12 passed", commitId: "fedcba9876543210"),
            Step("First step", 1, summary: "Adds the model.", commitId: "abc"),
            Step("Open step", 3, TodoStatus.InProgress),
            Step("Later step", 4, TodoStatus.Pending),
        };

        var body = PrBodyRenderer.Render(summary, steps);

        var plan = PrBodyRenderer.TryGetSection(body, "Plan")!;
        var lines = plan.Split('\n');
        Assert.Equal(
            [
                "1. **First step** (`abc`)",
                "   - Adds the model.",
                string.Empty,
                "2. **Second step** (`fedcba9`)",
                "   - Wires it up.",
                "     Second line.",
                "   - Verification: dotnet test: 12 passed",
                string.Empty,
                "3. **Open step** _(in progress)_",
                string.Empty,
                "4. **Later step** _(pending)_",
            ],
            lines);
        Assert.Equal("Does a thing.", PrBodyRenderer.TryGetSection(body, "Summary"));
        Assert.Equal("Ran it.", PrBodyRenderer.TryGetSection(body, "Testing"));
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_replaces_a_plan_section_the_summary_already_has_and_works_without_a_summary()
    {
        var summary = new PlanSummary("s", "## Plan\nstale\n\n## Testing\nRan it.", DateTimeOffset.UnixEpoch);
        var steps = new[] { Step("Only step", 1, commitId: "1234567890") };

        var replaced = PrBodyRenderer.Render(summary, steps);
        var planOnly = PrBodyRenderer.Render(null, steps);

        Assert.Equal("1. **Only step** (`1234567`)", PrBodyRenderer.TryGetSection(replaced, "Plan"));
        Assert.DoesNotContain("stale", replaced, StringComparison.Ordinal);
        Assert.Equal("Ran it.", PrBodyRenderer.TryGetSection(replaced, "Testing"));
        Assert.Equal("## Plan\n\n1. **Only step** (`1234567`)\n", planOnly);
    }

    [Fact]
    public void TryGetSection_reads_content_without_the_heading_and_distinguishes_empty_from_missing()
    {
        const string Body = "Intro line\n\n## Summary\n\nFirst.\n\n### Detail\nStays inside.\n\n## Empty\n\n## Last\nTail\n";

        Assert.Equal("First.\n\n### Detail\nStays inside.", PrBodyRenderer.TryGetSection(Body, "Summary"));
        Assert.Equal(string.Empty, PrBodyRenderer.TryGetSection(Body, "Empty"));
        Assert.Equal("Tail", PrBodyRenderer.TryGetSection(Body, "last"));
        Assert.Equal("Tail", PrBodyRenderer.TryGetSection(Body, "## Last"));
        Assert.Null(PrBodyRenderer.TryGetSection(Body, "Missing"));
        Assert.Null(PrBodyRenderer.TryGetSection(Body, "Intro line"));
        Assert.Null(PrBodyRenderer.TryGetSection(null, "Summary"));
        Assert.Null(PrBodyRenderer.TryGetSection(string.Empty, "Summary"));
        Assert.Null(PrBodyRenderer.TryGetSection(Body, " "));
    }

    [Fact]
    public void TryGetSection_ignores_headings_inside_code_fences_and_indented_or_level_three_lookalikes()
    {
        const string Body = "## Real\nbefore\n```\n## Testing\nnot a heading\n```\nafter\n  ## Indented\n### Testing\nstill real\n";

        Assert.Null(PrBodyRenderer.TryGetSection(Body, "Testing"));
        Assert.Null(PrBodyRenderer.TryGetSection(Body, "Indented"));
        Assert.Equal(
            "before\n```\n## Testing\nnot a heading\n```\nafter\n  ## Indented\n### Testing\nstill real",
            PrBodyRenderer.TryGetSection(Body, "Real"));
    }

    [Fact]
    public void ReplaceSection_changes_only_the_named_section()
    {
        const string Body = "## Summary\n\nOld summary.\n\n## Testing\n\nOld testing.\n\n## Notes\nKeep me.\n";

        var replaced = PrBodyRenderer.ReplaceSection(Body, "Testing", "Ran `dotnet test`: 5 passed.\nSecond line.");

        Assert.Equal("## Summary\n\nOld summary.\n\n## Testing\n\nRan `dotnet test`: 5 passed.\nSecond line.\n\n## Notes\nKeep me.\n", replaced);
        Assert.Equal("Old summary.", PrBodyRenderer.TryGetSection(replaced, "Summary"));
        Assert.Equal("Keep me.", PrBodyRenderer.TryGetSection(replaced, "Notes"));
    }

    [Fact]
    public void ReplaceSection_with_the_same_content_returns_the_body_unchanged()
    {
        const string Body = "## Summary\nOld summary.\n\n## Testing\nOld testing.\n";

        Assert.Same(Body, PrBodyRenderer.ReplaceSection(Body, "Summary", "Old summary."));
        Assert.Equal(Body, PrBodyRenderer.ReplaceSection(Body, "Testing", "\nOld testing.\n\n"));
    }

    [Fact]
    public void ReplaceSection_appends_a_missing_section_and_handles_an_empty_body()
    {
        Assert.Equal("## Summary\nS\n\n## Screenshots\n\n![a](u)\n", PrBodyRenderer.ReplaceSection("## Summary\nS\n\n\n", "Screenshots", "![a](u)"));
        Assert.Equal("## Testing\n\nRan.\n", PrBodyRenderer.ReplaceSection(null, "Testing", "Ran."));
        Assert.Equal("## Testing\n", PrBodyRenderer.ReplaceSection(string.Empty, "## Testing", string.Empty));
    }

    [Fact]
    public void ReplaceSection_keeps_the_line_ending_style_and_clears_a_section()
    {
        const string Body = "## Summary\r\nS\r\n\r\n## Testing\r\nOld\r\n";

        var replaced = PrBodyRenderer.ReplaceSection(Body, "Testing", "New\nlines");
        var cleared = PrBodyRenderer.ReplaceSection(Body, "Summary", string.Empty);

        Assert.Equal("## Summary\r\nS\r\n\r\n## Testing\r\n\r\nNew\r\nlines\r\n", replaced);
        Assert.DoesNotContain("\n", replaced.Replace("\r\n", string.Empty), StringComparison.Ordinal);
        Assert.Equal(string.Empty, PrBodyRenderer.TryGetSection(cleared, "Summary"));
        Assert.Equal("Old", PrBodyRenderer.TryGetSection(cleared, "Testing"));
    }

    [Fact]
    public void ReplaceSection_rejects_a_blank_heading_and_does_not_edit_inside_code_fences()
    {
        Assert.Throws<ArgumentException>(() => PrBodyRenderer.ReplaceSection("x", "  ", "y"));
        Assert.Throws<ArgumentException>(() => PrBodyRenderer.ReplaceSection("x", "##", "y"));
        Assert.Throws<ArgumentNullException>(() => PrBodyRenderer.ReplaceSection("x", "Testing", null!));

        const string Body = "```\n## Testing\nfenced\n```\n";
        var appended = PrBodyRenderer.ReplaceSection(Body, "Testing", "real");

        Assert.StartsWith("```\n## Testing\nfenced\n```", appended, StringComparison.Ordinal);
        Assert.Equal("real", PrBodyRenderer.TryGetSection(appended, "Testing"));
    }

    [Theory]
    [InlineData("tests pass")]
    [InlineData("All tests pass.")]
    [InlineData("All tests passed!")]
    [InlineData("tested")]
    [InlineData("Tested locally")]
    [InlineData("n/a")]
    [InlineData("N/A")]
    [InlineData("none")]
    [InlineData("- [x] tested\n- tests pass")]
    [InlineData("测试通过")]
    [InlineData("所有测试通过。")]
    [InlineData("无")]
    [InlineData("ok")]
    [InlineData("LGTM")]
    public void Testing_evidence_refuses_platitudes(string testing)
    {
        var body = "## Summary\nDoes a thing.\n\n## Testing\n" + testing + "\n";

        var problem = PrTestingEvidence.Check(body);

        Assert.NotNull(problem);
        Assert.Contains("Testing", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ran `dotnet test tests/Forge.Workspace.Tests`: 412 passed, 0 failed.")]
    [InlineData("Opened the settings page on Windows, changed the timeout and restarted; the value was kept.")]
    [InlineData("手动在 Windows 上打开设置页并保存,重启后配置仍然保留。")]
    [InlineData("- `dotnet build`: 0 warnings\n- `dotnet test`: 87 passed")]
    public void Testing_evidence_accepts_concrete_statements(string testing)
    {
        var body = "## Summary\nDoes a thing.\n\n## Testing\n" + testing + "\n";

        Assert.Null(PrTestingEvidence.Check(body));
    }

    [Fact]
    public void Testing_evidence_names_a_missing_or_empty_section()
    {
        Assert.Contains("no '## Testing' section", PrTestingEvidence.Check("## Summary\nx\n"), StringComparison.Ordinal);
        Assert.Contains("no '## Testing' section", PrTestingEvidence.Check(null), StringComparison.Ordinal);
        Assert.Contains("empty", PrTestingEvidence.Check("## Testing\n\n## Notes\nx\n"), StringComparison.Ordinal);
        Assert.Contains("empty", PrTestingEvidence.Check("## Testing\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Testing_evidence_weight_counts_cjk_double_and_drops_banned_phrases()
    {
        Assert.Equal(0, PrTestingEvidence.Weigh("All tests pass"));
        Assert.Equal(0, PrTestingEvidence.Weigh("测试通过"));
        Assert.True(PrTestingEvidence.Weigh("dotnet test: 412 passed") >= PrTestingEvidence.MinimumEvidenceWeight);
        Assert.True(PrTestingEvidence.Weigh("打开设置页保存配置") >= 16);
        Assert.Throws<ArgumentNullException>(() => PrTestingEvidence.Weigh(null!));
    }
}
