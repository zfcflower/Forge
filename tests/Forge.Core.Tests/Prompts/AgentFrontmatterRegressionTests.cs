using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class AgentFrontmatterRegressionTests
{
    [Fact]
    public void Inline_tool_list_in_brackets_ignores_a_trailing_comment()
    {
        var agent = AgentFileLoader.Parse("---\nname: reader\ntools: [read, grep] # read-only\n---\nbody", "fallback");

        Assert.Equal(["read", "grep"], agent.ToolNames);
    }

    [Fact]
    public void Unbracketed_tool_list_ignores_a_trailing_comment()
    {
        var agent = AgentFileLoader.Parse("---\ntools: read, grep # read-only\n---\nbody", "fallback");

        Assert.Equal(["read", "grep"], agent.ToolNames);
    }

    [Fact]
    public void Hash_inside_quoted_tool_names_is_preserved()
    {
        var agent = AgentFileLoader.Parse("---\ntools: [\"a#b\", 'c # d', grep]\n---\nbody", "fallback");

        Assert.Equal(["a#b", "c # d", "grep"], agent.ToolNames);
    }

    [Fact]
    public void Quoted_hash_is_preserved_and_the_trailing_comment_still_dropped()
    {
        var agent = AgentFileLoader.Parse("---\ntools: [\"a#b\"] # comment\n---\nbody", "fallback");

        Assert.Equal(["a#b"], agent.ToolNames);
    }

    [Fact]
    public void Hash_without_leading_whitespace_is_part_of_an_unquoted_name()
    {
        var agent = AgentFileLoader.Parse("---\ntools: [a#b, c]\n---\nbody", "fallback");

        Assert.Equal(["a#b", "c"], agent.ToolNames);
    }

    [Fact]
    public void Comment_only_tools_value_leaves_tools_null()
    {
        var agent = AgentFileLoader.Parse("---\nname: x\ntools: # no tools yet\n---\nbody", "fallback");

        Assert.Null(agent.ToolNames);
    }

    [Fact]
    public void Block_list_items_ignore_trailing_comments()
    {
        var agent = AgentFileLoader.Parse(
            """
            ---
            tools:
              - read # primary
              - grep
            ---
            body
            """,
            "fallback");

        Assert.Equal(["read", "grep"], agent.ToolNames);
    }
}
