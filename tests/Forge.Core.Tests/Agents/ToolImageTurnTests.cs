using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// A tool that returns a picture, through the loop: the block keeps it, the stored message keeps it, and each provider
/// request decides from <see cref="AgentOptions"/> whether the model may see it.
/// </summary>
public sealed class ToolImageTurnTests
{
    private const string Data = "iVBORw0KGgo=";

    private static ToolResult Screenshot() => new([new TextBlock("captured 1280x720"), new ImageBlock("image/png", Data)]);

    private static FakeTool ShotTool(string reply = "captured 1280x720", bool withImage = true) =>
        new("shot", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(
            withImage ? Screenshot() : new ToolResult(reply)));

    /// <summary>The tool-role message of the second request: what the model is told the tool produced.</summary>
    private static ToolResultBlock ToolResultOf(ChatRequest request)
    {
        foreach (var message in request.Messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result)
                {
                    return result;
                }
            }
        }

        throw new InvalidOperationException("no tool result reached the second request");
    }

    private static async Task<FakeProvider> RunOneToolCallAsync(AgentOptions options, ITool? tool = null)
    {
        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("call-1", "shot", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [tool ?? ShotTool()], options: options);
        await AgentTestHost.RunRootAsync(host);
        return provider;
    }

    [Fact]
    public async Task A_tool_result_keeps_its_image_and_its_placeholder_text()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions());

        var result = ToolResultOf(provider.Requests[1]);
        var image = Assert.Single(result.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Data, image.Data);
        Assert.Equal("captured 1280x720\n[image: image/png]", result.Content);
        Assert.Equal("captured 1280x720", result.Text);
    }

    [Fact]
    public async Task A_text_only_tool_result_is_unchanged()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions(), ShotTool(withImage: false));

        var result = ToolResultOf(provider.Requests[1]);
        Assert.Empty(result.Images);
        Assert.Equal("captured 1280x720", result.Content);
        Assert.Equal(result.Text, result.Content);
        Assert.Single(result.Blocks);
    }

    [Fact]
    public async Task The_turn_sends_tool_images_by_default()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions());

        Assert.True(provider.Requests[1].IncludeToolImages);
    }

    [Fact]
    public async Task The_switch_stops_the_images_from_reaching_the_model_but_keeps_them_in_the_record()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions { SendToolImagesToModel = false });

        Assert.False(provider.Requests[1].IncludeToolImages);
        Assert.Single(ToolResultOf(provider.Requests[1]).Images);
    }

    [Fact]
    public async Task A_model_known_not_to_take_images_gets_the_placeholder_only()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions { ModelInputs = _ => ModelInputs.Text | ModelInputs.Pdf });

        Assert.False(provider.Requests[1].IncludeToolImages);
        Assert.Single(ToolResultOf(provider.Requests[1]).Images);
    }

    [Fact]
    public async Task A_model_known_to_take_images_gets_them()
    {
        var provider = await RunOneToolCallAsync(
            new AgentOptions { ModelInputs = _ => ModelInputs.Text | ModelInputs.Image });

        Assert.True(provider.Requests[1].IncludeToolImages);
    }

    [Fact]
    public async Task A_model_the_catalogue_says_nothing_about_gets_them()
    {
        var provider = await RunOneToolCallAsync(new AgentOptions { ModelInputs = _ => null });

        Assert.True(provider.Requests[1].IncludeToolImages);
    }

    [Fact]
    public async Task Spilling_a_long_result_into_an_artifact_keeps_the_image()
    {
        var store = new InMemoryArtifactStore();
        var tool = new FakeTool("shot", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(
            new ToolResult([new TextBlock(new string('x', 500)), new ImageBlock("image/png", Data)])));
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };

        var provider = new FakeProvider((request, index) => index == 0
            ? Turn.Call(("call-1", "shot", "{}"))
            : Turn.Say("done"));
        var host = AgentTestHost.Create(provider, [tool], options: options, artifacts: store);

        await AgentTestHost.RunRootAsync(host);

        var result = ToolResultOf(provider.Requests[1]);
        Assert.Single(result.Images);
        Assert.Contains("stored as artifact", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 500), result.Text, StringComparison.Ordinal);
    }
}
