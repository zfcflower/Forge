using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The <c>skill</c> tool: the whole SKILL.md of one discovered skill by name (case-insensitive), the error a model gets
/// for an unknown or path-style name, and the cap on one skill's text. The name is resolved against the discovered
/// catalog only, so no argument can reach a file outside the skill roots.
/// </summary>
public sealed class SkillToolTests
{
    private const int MaxTextChars = SkillTool.MaxTextChars;

    /// <summary>One temp directory with two skill files; the catalog function reads them per call.</summary>
    private sealed class Catalog : IDisposable
    {
        private const string AlphaFileText =
            "---\nname: alpha\ndescription: the alpha skill\n---\n# Alpha\n\nFollow this.\n";

        private readonly string root = TestTempFiles.NewDirectory("forge-skill-tool-");

        public string AlphaText => AlphaFileText;

        public Catalog()
        {
            TestTempFiles.WriteFile(root, "alpha/SKILL.md", AlphaFileText);
            TestTempFiles.WriteFile(root, "beta/SKILL.md", "---\nname: beta\ndescription: the beta skill\n---\n# Beta\n");
        }

        public IReadOnlyList<SlashEntry> Entries()
        {
            var alpha = Path.Combine(root, "alpha", "SKILL.md");
            var beta = Path.Combine(root, "beta", "SKILL.md");
            return
            [
                new SlashEntry("alpha", "the alpha skill", IsSkill: true, alpha),
                new SlashEntry("beta", "the beta skill", IsSkill: true, beta),
            ];
        }

        /// <summary>One entry whose file is gone, for the unreadable-file path.</summary>
        public IReadOnlyList<SlashEntry> WithMissingFile() =>
            [new SlashEntry("alpha", "the alpha skill", IsSkill: true, Path.Combine(root, "alpha", "gone.md"))];

        public void Dispose() => TestTempFiles.DeleteTree(root);
    }

    private static ValueTask<ToolResult> InvokeAsync(SkillTool tool, string json) =>
        tool.InvokeAsync(new ToolCall("call-1", SkillTool.Name, json), new ToolContext("session-1", Path.GetTempPath()), CancellationToken.None);

    private static string NameJson(string name) => $$"""{"name":"{{name}}"}""";

    [Theory]
    [InlineData("alpha")]
    [InlineData("ALPHA")]
    [InlineData("Alpha")]
    public async Task A_known_name_returns_the_whole_file(string name)
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.Entries);

        var result = await InvokeAsync(tool, NameJson(name));

        Assert.False(result.IsError, result.Content);
        Assert.Equal(catalog.AlphaText, result.Content);
    }

    [Fact]
    public async Task An_unknown_name_is_an_error_that_lists_the_available_names()
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.Entries);

        var result = await InvokeAsync(tool, NameJson("gamma"));

        Assert.True(result.IsError);
        Assert.Contains("No skill named 'gamma' is available", result.Content, StringComparison.Ordinal);
        Assert.Contains("Available: alpha, beta", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_style_name_is_resolved_against_the_catalog_only_and_never_reads_a_file()
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.Entries);

        var result = await InvokeAsync(tool, NameJson("../../etc/passwd"));

        // The name is matched against the discovered names, not joined onto a root: no traversal can reach a file.
        Assert.True(result.IsError);
        Assert.Contains("No skill named", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.AlphaText, result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", "requires a 'name' string")]
    [InlineData("""{"name":5}""", "requires a 'name' string")]
    [InlineData("""{"name":null}""", "requires a 'name' string")]
    [InlineData("""{"name":"   "}""", "requires a 'name' string")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("5", "must be a JSON object")]
    [InlineData("not json", "arguments are invalid")]
    public async Task Arguments_without_a_name_string_are_an_error(string json, string expectedFragment)
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.Entries);

        var result = await InvokeAsync(tool, json);

        Assert.True(result.IsError);
        Assert.Contains(expectedFragment, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skill_file_that_cannot_be_read_is_an_error_with_the_reason()
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.WithMissingFile);

        var result = await InvokeAsync(tool, NameJson("alpha"));

        Assert.True(result.IsError);
        Assert.Contains("could not be read", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_past_the_cap_is_cut_and_says_by_how_much()
    {
        var root = TestTempFiles.NewDirectory("forge-skill-tool-");
        try
        {
            var text = new string('x', MaxTextChars + 500);
            var file = TestTempFiles.WriteFile(root, "long/SKILL.md", text);
            var tool = new SkillTool(() => new SlashEntry[] { new("long", "a long skill", IsSkill: true, file) });

            var result = await InvokeAsync(tool, NameJson("long"));

            Assert.False(result.IsError, result.Content);
            Assert.Equal(MaxTextChars + 500, text.Length);
            Assert.StartsWith(new string('x', MaxTextChars), result.Content, StringComparison.Ordinal);
            Assert.Contains("500 characters truncated", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public void The_tool_is_read_only()
    {
        using var catalog = new Catalog();
        var tool = new SkillTool(catalog.Entries);

        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }
}
