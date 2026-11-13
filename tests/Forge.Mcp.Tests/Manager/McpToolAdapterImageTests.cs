using Forge.Abstractions;
using Forge.Mcp.Tests.Fixtures;
using Xunit;
using static Forge.Mcp.Tests.Fixtures.ManagerFixture;

namespace Forge.Mcp.Tests.Manager;

/// <summary>MCP <c>image</c> content that the adapter turns into a real <see cref="ImageBlock"/> on the tool result.</summary>
public sealed class McpToolAdapterImageTests
{
    private static readonly ToolCall Call = new("c", "srv__echo", "{}");
    private static readonly ToolContext Context = new("s", "/");

    [Fact]
    public async Task A_kept_png_becomes_an_image_block_with_the_exact_base64_and_no_text_placeholder()
    {
        const string base64 = "iVBORw0KGgoAAAANSUhEUg==";
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"image","data":"{{base64}}","mimeType":"image/png"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        var image = Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(base64, image.Data);
        // The derived text still shows a placeholder (a text-only reader knows an image arrived), but the model-facing
        // blocks carry the picture itself and do not repeat the placeholder.
        Assert.Equal("[image: image/png]", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Text_and_image_keep_the_text_first_and_the_image_second()
    {
        const string base64 = "iVBORw0KGgo=";
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"text","text":"shot taken"},{"type":"image","data":"{{base64}}","mimeType":"image/png"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        Assert.Equal(2, result.Blocks.Count);
        var text = Assert.IsType<TextBlock>(result.Blocks[0]);
        Assert.Equal("shot taken", text.Text);
        Assert.DoesNotContain("[image:", text.Text, StringComparison.Ordinal);
        Assert.Equal(base64, Assert.IsType<ImageBlock>(result.Blocks[1]).Data);
    }

    [Fact]
    public async Task An_image_over_the_byte_cap_keeps_its_placeholder_text_but_no_data()
    {
        // 6_990_508 base64 characters estimate 5_242_881 decoded bytes, just over the 5 MiB cap.
        var oversized = new string('A', 6_990_508);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"image","data":"{{oversized}}","mimeType":"image/png"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("[image: image/png, 5 MiB]", text.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task An_svg_image_stays_a_placeholder()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            """{"content":[{"type":"image","data":"PHN2Zz4=","mimeType":"image/svg+xml"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("[image: image/svg+xml, 6 B]", text.Text);
    }

    [Fact]
    public async Task A_text_only_result_is_the_same_single_text_block_as_before()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            """{"content":[{"type":"text","text":"hello"},{"type":"text","text":"world"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("hello\nworld", text.Text);
        Assert.Equal("hello\nworld", result.Content);
        Assert.False(result.IsError);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task The_character_truncation_still_applies_with_its_note_and_the_image_survives()
    {
        var text = new string('a', 10) + new string('b', 40);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            $$"""{"content":[{"type":"text","text":"{{text}}"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxResultChars: 10);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        Assert.Equal("aaaaaaaaaa\n…[output truncated: 40 characters omitted]", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        Assert.IsType<ImageBlock>(result.Blocks[1]);
    }

    [Fact]
    public async Task IsError_is_preserved_alongside_a_kept_image()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ =>
            """{"content":[{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}],"isError":true}""");
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);

        var result = await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
    }
}
