using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Client;

/// <summary>The base64 payload an MCP image content keeps on <see cref="McpContent.Data"/>: only a displayable media
/// type, within the size cap.</summary>
public sealed class McpContentDataTests
{
    [Fact]
    public async Task A_displayable_image_within_the_cap_keeps_its_data_on_the_content_block()
    {
        const string base64 = "iVBORw0KGgoAAAANSUhEUg==";
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"image","data":"{{base64}}","mimeType":"image/png"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        var block = Assert.Single(result.Content!);
        Assert.Equal("image", block.Type);
        Assert.Equal("image/png", block.MimeType);
        Assert.Equal(base64, block.Data);
        Assert.StartsWith("[image: image/png, ", block.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"image","data":"AAAA","mimeType":"image/svg+xml"}""")]
    [InlineData("""{"type":"image","data":"AAAA","mimeType":"image/bmp"}""")]
    [InlineData("""{"type":"audio","data":"AAAA","mimeType":"audio/mpeg"}""")]
    public async Task A_block_the_app_cannot_display_keeps_its_placeholder_and_no_data(string block)
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => $$"""{"content":[{{block}}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        var content = Assert.Single(result.Content!);
        Assert.Null(content.Data);
        Assert.StartsWith("[", content.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_over_the_cap_keeps_its_placeholder_and_no_data()
    {
        // 6_990_508 base64 characters estimate 5_242_881 decoded bytes, just over the 5 MiB cap.
        var oversized = new string('A', 6_990_508);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"image","data":"{{oversized}}","mimeType":"image/png"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        var content = Assert.Single(result.Content!);
        Assert.Null(content.Data);
        Assert.Equal("[image: image/png, 5 MiB]", content.Text);
    }
}
