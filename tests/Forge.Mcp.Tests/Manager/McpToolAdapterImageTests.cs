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

    /// <summary>Starts a single scripted <c>srv</c> server answering <c>ToolsCall</c> with <paramref name="toolsCallResultJson"/>, then invokes its first tool with the shared call/context.</summary>
    private static async Task<ToolResult> InvokeScriptedAsync(string toolsCallResultJson, int maxResultChars = 64 * 1024)
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => toolsCallResultJson);
        await using var fixture = new ManagerFixture(Of(Stdio("srv")), _ => server, maxResultChars: maxResultChars);
        await fixture.Manager.StartAsync("srv", CancellationToken.None);
        return await fixture.Manager.Tools[0].InvokeAsync(Call, Context, CancellationToken.None);
    }

    [Fact]
    public async Task A_kept_png_becomes_an_image_block_with_the_exact_base64_and_no_text_placeholder()
    {
        const string base64 = "iVBORw0KGgoAAAANSUhEUg==";
        var result = await InvokeScriptedAsync(
            $$"""{"content":[{"type":"image","data":"{{base64}}","mimeType":"image/png"}]}""");

        var image = Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(base64, image.Data);
        // The derived text still shows a placeholder (a text-only reader knows an image arrived), but the model-facing
        // blocks carry the picture itself and do not repeat the placeholder.
        Assert.Equal("[image: image/png, 1 KB]", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Text_and_image_keep_the_text_first_and_the_image_second()
    {
        const string base64 = "iVBORw0KGgo=";
        var result = await InvokeScriptedAsync(
            $$"""{"content":[{"type":"text","text":"shot taken"},{"type":"image","data":"{{base64}}","mimeType":"image/png"}]}""");

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
        var result = await InvokeScriptedAsync(
            $$"""{"content":[{"type":"image","data":"{{oversized}}","mimeType":"image/png"}]}""");

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("[image: image/png, 5 MiB]", text.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task An_svg_image_stays_a_placeholder()
    {
        var result = await InvokeScriptedAsync(
            """{"content":[{"type":"image","data":"PHN2Zz4=","mimeType":"image/svg+xml"}]}""");

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("[image: image/svg+xml, 6 B]", text.Text);
    }

    [Fact]
    public async Task A_text_only_result_is_the_same_single_text_block_as_before()
    {
        var result = await InvokeScriptedAsync(
            """{"content":[{"type":"text","text":"hello"},{"type":"text","text":"world"}]}""");

        var text = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.Equal("hello\nworld", text.Text);
        Assert.Equal("hello\nworld", result.Content);
        Assert.False(result.IsError);
        Assert.False(result.HasImages);
    }

    [Fact]
    public async Task The_character_truncation_still_applies_with_its_note_and_the_image_survives()
    {
        var text = new string('a', 10) + new string('b', 40);
        var result = await InvokeScriptedAsync(
            $$"""{"content":[{"type":"text","text":"{{text}}"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}""",
            maxResultChars: 10);

        Assert.Equal("aaaaaaaaaa\n…[output truncated: 40 characters omitted]", Assert.IsType<TextBlock>(result.Blocks[0]).Text);
        Assert.IsType<ImageBlock>(result.Blocks[1]);
    }

    [Fact]
    public async Task Images_past_the_per_result_budget_fall_back_to_their_placeholder_text()
    {
        // 6_000_000 base64 characters estimate 4_500_000 decoded bytes: each fits the per-image cap, four fit the 20 MiB
        // result budget, the fifth does not.
        var payload = new string('A', 6_000_000);
        var image = $$"""{"type":"image","data":"{{payload}}","mimeType":"image/png"}""";
        var body = $$"""{"content":[{{string.Join(",", Enumerable.Repeat(image, 5))}}]}""";
        var result = await InvokeScriptedAsync(body);

        Assert.Equal(4, result.Blocks.Count(static block => block is ImageBlock));
        var text = Assert.IsType<TextBlock>(result.Blocks[0]);
        Assert.StartsWith("[image: image/png, ", text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text.Text);
    }

    [Fact]
    public async Task IsError_is_preserved_alongside_a_kept_image()
    {
        var result = await InvokeScriptedAsync(
            """{"content":[{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}],"isError":true}""");

        Assert.True(result.IsError);
        Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
    }
}
