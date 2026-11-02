using Forge.Abstractions;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class AgentFileLoaderTests
{
    private const string FullFile =
        """
        ---
        name: Auditor
        description: "Checks contracts: strict mode"
        model: claude-sonnet
        thinking: high
        tools: [read, grep, find]
        ---
        You audit contracts.

        Report only material findings.
        """;

    [Fact]
    public void Parse_reads_frontmatter_and_body()
    {
        var agent = AgentFileLoader.Parse(FullFile, "fallback");

        Assert.Equal("Auditor", agent.Name);
        Assert.Equal("Checks contracts: strict mode", agent.Description);
        Assert.Equal("claude-sonnet", agent.Model.Model);
        Assert.Equal(ThinkingEffort.High, agent.Model.Thinking);
        Assert.Equal(["read", "grep", "find"], agent.ToolNames);
        Assert.Equal("You audit contracts.\n\nReport only material findings.", agent.Prompt);
    }

    [Fact]
    public void Parse_defaults_missing_fields()
    {
        var agent = AgentFileLoader.Parse(
            """
            ---
            description: no name here
            ---
            body
            """,
            "from-file-name");

        Assert.Equal("from-file-name", agent.Name);
        Assert.Equal("no name here", agent.Description);
        Assert.Equal("default", agent.Model.Model);
        Assert.Equal(ThinkingEffort.Off, agent.Model.Thinking);
        Assert.Null(agent.ToolNames);
        Assert.Equal("body", agent.Prompt);
    }

    [Fact]
    public void Parse_without_frontmatter_treats_whole_file_as_body()
    {
        var agent = AgentFileLoader.Parse("# Notes\n\nJust markdown.\n", "File Agent");

        Assert.Equal("File Agent", agent.Name);
        Assert.Equal(string.Empty, agent.Description);
        Assert.Equal("default", agent.Model.Model);
        Assert.Equal(ThinkingEffort.Off, agent.Model.Thinking);
        Assert.Null(agent.ToolNames);
        Assert.Equal("# Notes\n\nJust markdown.", agent.Prompt);
    }

    [Fact]
    public void Parse_unterminated_frontmatter_treats_whole_file_as_body()
    {
        const string text = "---\nname: Broken\ntools: read\n\nbody line\n";
        var agent = AgentFileLoader.Parse(text, "fallback");

        Assert.Equal("fallback", agent.Name);
        Assert.Null(agent.ToolNames);
        Assert.Contains("---\nname: Broken", agent.Prompt, StringComparison.Ordinal);
        Assert.EndsWith("body line", agent.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_handles_quotes_comments_and_casing()
    {
        var agent = AgentFileLoader.Parse(
            """
            ---
            # leading comment
            NAME: 'single quoted'
            Description: "escaped \"quote\" inside" # trailing comment
            Thinking: Medium
            tools: read ,  'edit' ,write
            ---
            prompt
            """,
            "fallback");

        Assert.Equal("single quoted", agent.Name);
        Assert.Equal("escaped \"quote\" inside", agent.Description);
        Assert.Equal(ThinkingEffort.Medium, agent.Model.Thinking);
        Assert.Equal(["read", "edit", "write"], agent.ToolNames);
    }

    [Fact]
    public void Parse_accepts_block_tool_lists()
    {
        var agent = AgentFileLoader.Parse(
            """
            ---
            name: blocky
            tools:
              - read
              - grep
            ---
            prompt
            """,
            "fallback");

        Assert.Equal(["read", "grep"], agent.ToolNames);
    }

    [Theory]
    [InlineData("off", ThinkingEffort.Off)]
    [InlineData("low", ThinkingEffort.Low)]
    [InlineData("medium", ThinkingEffort.Medium)]
    [InlineData("high", ThinkingEffort.High)]
    [InlineData("HIGH", ThinkingEffort.High)]
    [InlineData("maximum", ThinkingEffort.Off)]
    [InlineData("", ThinkingEffort.Off)]
    public void Parse_maps_thinking_effort(string value, ThinkingEffort expected)
    {
        var agent = AgentFileLoader.Parse($"---\nthinking: {value}\n---\nbody", "fallback");

        Assert.Equal(expected, agent.Model.Thinking);
    }

    [Fact]
    public void Parse_normalizes_crlf_in_body()
    {
        var agent = AgentFileLoader.Parse("---\r\nname: crlf\r\n---\r\nline one\r\nline two\r\n", "fallback");

        Assert.Equal("line one\nline two", agent.Prompt);
        Assert.Equal("crlf", agent.Name);
    }

    [Fact]
    public void Parse_empty_tools_value_leaves_tools_null()
    {
        var agent = AgentFileLoader.Parse("---\nname: x\ntools:\n---\nbody", "fallback");
        var emptyList = AgentFileLoader.Parse("---\nname: x\ntools: []\n---\nbody", "fallback");

        Assert.Null(agent.ToolNames);
        Assert.Null(emptyList.ToolNames);
    }

    [Fact]
    public void Parse_strips_the_byte_order_mark()
    {
        var plain = AgentFileLoader.Parse("\uFEFFbody text", "fallback");
        var withFrontmatter = AgentFileLoader.Parse("\uFEFF---\nname: bom\n---\nbody text", "fallback");

        Assert.Equal("body text", plain.Prompt);
        Assert.Equal("body text", withFrontmatter.Prompt);
        Assert.Equal("bom", withFrontmatter.Name);
    }

    [Fact]
    public void LoadFile_reads_from_disk_and_names_from_file()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("plain-file.md", "Just a prompt.\n");

        var agent = AgentFileLoader.LoadFile(path);

        Assert.Equal("plain-file", agent.Name);
        Assert.Equal("Just a prompt.", agent.Prompt);
    }

    [Fact]
    public async Task LoadFileAsync_reads_from_disk()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("async-agent.md", FullFile);

        var agent = await AgentFileLoader.LoadFileAsync(path, CancellationToken.None);

        Assert.Equal("Auditor", agent.Name);
        Assert.Equal(["read", "grep", "find"], agent.ToolNames);
    }

    [Fact]
    public void LoadFile_missing_file_throws() =>
        Assert.Throws<FileNotFoundException>(() => AgentFileLoader.LoadFile(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "forge-missing-" + Guid.NewGuid().ToString("N") + ".md")));

    [Fact]
    public void LoadDirectory_loads_markdown_files_in_name_order()
    {
        using var temp = new TempDirectory();
        temp.Write("b-second.md", "---\nname: second\n---\nbody b");
        temp.Write("a-first.md", "---\nname: first\n---\nbody a");
        temp.Write("ignored.txt", "not an agent");
        temp.Write("nested/nested.md", "nested should be skipped");

        var agents = AgentFileLoader.LoadDirectory(temp.Path);

        Assert.Equal(["first", "second"], agents.Select(a => a.Name));
    }

    [Fact]
    public async Task LoadDirectoryAsync_loads_markdown_files_in_name_order()
    {
        using var temp = new TempDirectory();
        temp.Write("b.md", "body b");
        temp.Write("a.md", "body a");

        var agents = await AgentFileLoader.LoadDirectoryAsync(temp.Path, CancellationToken.None);

        Assert.Equal(["a", "b"], agents.Select(a => a.Name));
    }

    [Fact]
    public void LoadDirectory_empty_directory_returns_empty_list()
    {
        using var temp = new TempDirectory();

        Assert.Empty(AgentFileLoader.LoadDirectory(temp.Path));
    }

    [Fact]
    public void LoadDirectory_missing_directory_throws() =>
        Assert.Throws<DirectoryNotFoundException>(() => AgentFileLoader.LoadDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "forge-missing-" + Guid.NewGuid().ToString("N"))));
}
