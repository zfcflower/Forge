using System.Text.Json;
using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;
using static Forge.Mcp.Tests.Fixtures.ManagerFixture;

namespace Forge.Mcp.Tests.Manager;

public class McpToolSearchToolTests
{
    private static ToolCall Call(string argumentsJson) => new("c1", McpToolSearchTool.Name, argumentsJson);

    private static ToolContext Context => new("s", "/");

    [Fact]
    public void Definition_is_read_only_with_the_documented_name_and_a_valid_json_schema()
    {
        var tool = new McpToolSearchTool((_, _) => []);

        Assert.Equal("mcp_tool_search", tool.Definition.Name);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
        using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
        var root = schema.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(["query"], root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var limit = root.GetProperty("properties").GetProperty("limit");
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(20, limit.GetProperty("maximum").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"limit":5}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("{ not json")]
    public async Task A_missing_or_blank_query_is_an_error(string argumentsJson)
    {
        var tool = new McpToolSearchTool((_, _) => []);

        var result = await tool.InvokeAsync(Call(argumentsJson), Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("\"query\" is required", result.Content);
    }

    [Fact]
    public async Task Zero_matches_answers_with_the_no_match_message()
    {
        var tool = new McpToolSearchTool((_, _) => []);

        var result = await tool.InvokeAsync(Call("""{"query":"zzz"}"""), Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "No MCP tool matches \"zzz\". The configured servers expose no such tool; ask the user whether a server is missing or disabled.",
            result.Content);
    }

    [Fact]
    public async Task A_throwing_callback_becomes_an_error_result_with_its_message()
    {
        var tool = new McpToolSearchTool((_, _) => throw new InvalidOperationException("pin exploded"));

        var result = await tool.InvokeAsync(Call("""{"query":"x"}"""), Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("mcp_tool_search failed: pin exploded", result.Content);
    }

    [Theory]
    [InlineData("""{"query":"x","limit":0}""", 1)]
    [InlineData("""{"query":"x","limit":-3}""", 1)]
    [InlineData("""{"query":"x","limit":50}""", 20)]
    [InlineData("""{"query":"x","limit":7}""", 7)]
    [InlineData("""{"query":"x","limit":"abc"}""", 5)]
    [InlineData("""{"query":"x"}""", 5)]
    public async Task The_limit_is_clamped_to_the_documented_range_and_defaults_to_5(string argumentsJson, int expectedLimit)
    {
        var received = -1;
        var tool = new McpToolSearchTool((_, limit) =>
        {
            received = limit;
            return [];
        });

        await tool.InvokeAsync(Call(argumentsJson), Context, CancellationToken.None);

        Assert.Equal(expectedLimit, received);
    }

    [Fact]
    public async Task The_answer_lists_exposed_and_server_names_with_the_side_effect_hint()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("alpha"), Stdio("beta")));
        await fixture.Manager.StartAllAsync(CancellationToken.None);
        var tool = new McpToolSearchTool((_, _) => fixture.Manager.Tools);

        var result = await tool.InvokeAsync(Call("""{"query":"echo"}"""), Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("2 MCP tools are now available", result.Content);
        Assert.Contains("- alpha__echo [alpha, executes; needs approval]: [MCP server \"alpha\"] Echoes text", result.Content);
        Assert.Contains("- beta__echo [beta, executes; needs approval]: [MCP server \"beta\"] Echoes text", result.Content);
    }

    [Fact]
    public async Task Read_only_tools_are_listed_without_the_approval_hint()
    {
        await using var fixture = new ManagerFixture(Of(Stdio("srv", readOnlyTools: ["echo"])));
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        var tool = new McpToolSearchTool((_, _) => fixture.Manager.Tools);

        var result = await tool.InvokeAsync(Call("""{"query":"echo"}"""), Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("1 MCP tool is now available", result.Content);
        Assert.Contains("- srv__echo [srv]: [MCP server \"srv\"] Echoes text", result.Content);
        Assert.DoesNotContain("needs approval", result.Content);
    }
}
