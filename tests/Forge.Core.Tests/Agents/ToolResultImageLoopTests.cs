using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>An image in a tool result travels through the real loop: the event, the history, the store, the spill and the estimate.</summary>
public sealed class ToolResultImageLoopTests
{
    private static readonly ImageBlock Shot = new("image/png", Convert.ToBase64String(new byte[3000]));

    private static string Workspace => Path.GetTempPath();

    private static FakeTool Returning(ToolResult result) =>
        new("shot", ToolSideEffect.ReadOnly, (_, _, _) => new ValueTask<ToolResult>(result));

    private static string ArtifactId(string note)
    {
        var start = note.IndexOf('\'') + 1;
        return note[start..note.IndexOf('\'', start)];
    }

    private static async Task<(AgentInstance Root, List<AgentEvent> Events)> RunAsync(
        ToolResult result,
        AgentOptions? options = null,
        InMemoryArtifactStore? artifacts = null,
        InMemorySessionStore? store = null,
        string? sessionId = null)
    {
        var host = AgentTestHost.Create(
            AgentTestScripts.OneToolCallThenSay("shot"),
            [Returning(result)],
            options: options,
            artifacts: artifacts,
            store: store);
        var root = host.CreateRoot(sessionId ?? "session", Workspace);
        var events = await host.RunToCompletionAsync(root);
        return (root, events);
    }

    [Fact]
    public async Task A_text_and_image_result_reaches_the_event_the_history_and_the_store_unchanged()
    {
        var store = new InMemorySessionStore();
        var session = await store.CreateSessionAsync(Workspace, "images", CancellationToken.None);
        var produced = new ToolResult([new TextBlock("shot taken"), Shot]);

        var (root, events) = await RunAsync(produced, store: store, sessionId: session.Id);

        var finished = Assert.Single(events.OfType<AgentToolFinished>());
        Assert.Equal(produced.Blocks, finished.Result.Blocks);
        var inHistory = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(produced.Blocks, inHistory.Blocks);
        Assert.Equal("shot taken\n[image: image/png, 3 KB]", inHistory.Content);
        var persisted = Assert.Single(store.Appended(session.Id)[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(inHistory, persisted);
    }

    [Fact]
    public async Task The_next_provider_request_carries_the_tool_result_with_its_image()
    {
        var provider = AgentTestScripts.OneToolCallThenSay("shot");
        var host = AgentTestHost.Create(provider, [Returning(new ToolResult([new TextBlock("t"), Shot]))]);
        var root = host.CreateRoot("session", Workspace);

        await host.RunToCompletionAsync(root);

        var sent = provider.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Single();
        Assert.Contains(Shot, sent.Blocks);
    }

    [Fact]
    public async Task Over_threshold_text_is_stored_in_full_while_the_image_survives()
    {
        var artifacts = new InMemoryArtifactStore();
        var payload = new string('x', 500);
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };

        var (root, events) = await RunAsync(new ToolResult([new TextBlock(payload), Shot]), options, artifacts);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(2, result.Blocks.Count);
        var preview = Assert.IsType<TextBlock>(result.Blocks[0]);
        Assert.StartsWith(new string('x', 20), preview.Text);
        Assert.Contains("read_artifact", preview.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, preview.Text, StringComparison.Ordinal);
        Assert.Equal(Shot, result.Blocks[1]);
        Assert.Equal(result.Blocks, Assert.Single(events.OfType<AgentToolFinished>()).Result.Blocks);
        Assert.Equal(payload, artifacts.Text(ArtifactId(preview.Text)));
    }

    [Fact]
    public async Task Several_text_blocks_over_the_threshold_are_stored_together_and_every_image_is_kept()
    {
        var artifacts = new InMemoryArtifactStore();
        var other = new ImageBlock("image/jpeg", "AAAA");
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 10 };
        var first = new string('a', 80);
        var second = new string('b', 80);

        var (root, _) = await RunAsync(new ToolResult([new TextBlock(first), Shot, new TextBlock(second), other]), options, artifacts);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(3, result.Blocks.Count);
        var preview = Assert.IsType<TextBlock>(result.Blocks[0]);
        Assert.Equal(Shot, result.Blocks[1]);
        Assert.Equal(other, result.Blocks[2]);
        Assert.Equal(first + "\n" + second, artifacts.Text(ArtifactId(preview.Text)));
    }

    [Fact]
    public async Task An_image_with_short_text_is_not_spilled()
    {
        var artifacts = new InMemoryArtifactStore();
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };
        var produced = new ToolResult([new TextBlock("short"), Shot]);

        var (root, _) = await RunAsync(produced, options, artifacts);

        Assert.Equal(produced.Blocks, Assert.Single(root.History[2].Content.OfType<ToolResultBlock>()).Blocks);
    }

    [Fact]
    public async Task A_huge_error_result_is_never_spilled()
    {
        var artifacts = new InMemoryArtifactStore();
        var payload = new string('e', 500);
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };

        var (root, _) = await RunAsync(new ToolResult([new TextBlock(payload), Shot], true), options, artifacts);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(payload, Assert.IsType<TextBlock>(result.Blocks[0]).Text);
    }

    [Fact]
    public async Task Text_only_over_the_threshold_still_becomes_a_single_text_block_preview()
    {
        var artifacts = new InMemoryArtifactStore();
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };

        var (root, _) = await RunAsync(new ToolResult(new string('x', 500)), options, artifacts);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        var only = Assert.IsType<TextBlock>(Assert.Single(result.Blocks));
        Assert.StartsWith(new string('x', 20), only.Text);
        Assert.Contains("500 bytes", only.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_exactly_at_the_threshold_is_not_spilled()
    {
        var artifacts = new InMemoryArtifactStore();
        var options = new AgentOptions { ArtifactThresholdChars = 100, ArtifactPreviewChars = 20 };
        var text = new string('x', 100);

        var (root, _) = await RunAsync(new ToolResult([new TextBlock(text), Shot]), options, artifacts);

        var result = Assert.Single(root.History[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(text, Assert.IsType<TextBlock>(result.Blocks[0]).Text);
    }

    [Fact]
    public void A_tool_result_image_adds_the_flat_image_cost_to_the_estimate()
    {
        var counter = new ApproximateTokenCounter();
        var textOnly = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c", [new TextBlock("some output")])]);
        var withImage = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c", [new TextBlock("some output"), Shot])]);
        var withTwo = new ChatMessage(MessageRole.Tool, [new ToolResultBlock("c", [new TextBlock("some output"), Shot, Shot])]);
        var hugeImage = new ChatMessage(
            MessageRole.Tool,
            [new ToolResultBlock("c", [new TextBlock("some output"), new ImageBlock("image/png", new string('A', 4_000_000))])]);

        var baseline = counter.Count([textOnly]);

        Assert.True(counter.Count([withImage]) >= baseline + ApproximateTokenCounter.ImageTokens);
        Assert.True(counter.Count([withTwo]) >= baseline + (2 * ApproximateTokenCounter.ImageTokens));
        Assert.Equal(counter.Count([withImage]), counter.Count([hugeImage]));
    }
}
