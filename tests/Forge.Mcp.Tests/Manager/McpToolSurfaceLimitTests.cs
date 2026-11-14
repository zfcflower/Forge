using Forge.Mcp.Tests.Fixtures;
using Xunit;
using static Forge.Mcp.Tests.Fixtures.ManagerFixture;

namespace Forge.Mcp.Tests.Manager;

/// <summary>
/// What a server's <c>tools/list</c> may cost the model: a tool whose input schema is over the cap is not offered (and the
/// server's state says so), and an over-long description is cut. Every offered schema rides on every request.
/// </summary>
public class McpToolSurfaceLimitTests
{
    private static ScriptedServer ServerWith(string toolsListJson) =>
        new ScriptedServer().On(McpProtocol.ToolsList, _ => toolsListJson);

    [Fact]
    public async Task A_tool_whose_schema_exceeds_the_cap_is_not_offered_and_the_state_says_why()
    {
        var huge = new string('x', 300);
        var list = $$$"""{"tools":[{"name":"small","inputSchema":{"type":"object"}},{"name":"big","inputSchema":{"type":"object","description":"{{{huge}}}"}}]}""";
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => ServerWith(list), maxToolSchemaChars: 200);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(["srv__small"], fixture.Manager.Tools.Select(tool => tool.ExposedName));
        var state = fixture.Manager.GetState("srv");
        Assert.Equal(McpServerStatus.Ready, state.Status);
        Assert.Contains("'big'", state.LastError);
        Assert.Contains("200", state.LastError);
    }

    [Fact]
    public async Task A_schema_cap_of_zero_offers_every_tool()
    {
        var huge = new string('x', 300);
        var list = $$$"""{"tools":[{"name":"big","inputSchema":{"type":"object","description":"{{{huge}}}"}}]}""";
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => ServerWith(list), maxToolSchemaChars: 0);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        Assert.Equal(["srv__big"], fixture.Manager.Tools.Select(tool => tool.ExposedName));
        Assert.Null(fixture.Manager.GetState("srv").LastError);
    }

    [Fact]
    public async Task A_description_over_the_cap_is_cut_with_an_ellipsis()
    {
        var text = new string('d', 100);
        var list = $$$"""{"tools":[{"name":"echo","description":"{{{text}}}","inputSchema":{"type":"object"}}]}""";
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => ServerWith(list), maxToolDescriptionChars: 10);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var description = Assert.Single(fixture.Manager.Tools).Definition.Description;
        Assert.EndsWith(" " + new string('d', 9) + "…", description);
        Assert.DoesNotContain(new string('d', 10), description);
    }

    [Fact]
    public async Task A_description_cut_never_splits_a_surrogate_pair()
    {
        // Nine ASCII characters, then an emoji (two UTF-16 units) straddling the cut point of 10 characters.
        var list = """{"tools":[{"name":"echo","description":"abcdefgh\uD83D\uDE00tail","inputSchema":{"type":"object"}}]}""";
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => ServerWith(list), maxToolDescriptionChars: 10);

        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var description = Assert.Single(fixture.Manager.Tools).Definition.Description;
        Assert.EndsWith("abcdefgh…", description);
        foreach (var character in description)
        {
            Assert.False(char.IsSurrogate(character), "a cut description must not keep half a surrogate pair");
        }
    }
}
