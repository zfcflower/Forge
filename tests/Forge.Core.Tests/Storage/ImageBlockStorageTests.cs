using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>An attached image in the message log, in every text view of a message and in the token estimate.</summary>
public sealed class ImageBlockStorageTests
{
    // 3000 bytes of payload: exactly 4000 base64 characters, no padding.
    private static readonly string Base64 = Convert.ToBase64String(new byte[3000]);

    [Fact]
    public void An_image_block_round_trips_with_a_fixed_field_order()
    {
        var message = new ChatMessage(MessageRole.User, [new TextBlock("see"), new ImageBlock("image/webp", "AAAA")]);

        var json = MessageJson.Serialize(message);

        Assert.Equal(
            """{"role":"user","content":[{"type":"text","text":"see"},{"type":"image","media_type":"image/webp","data":"AAAA"}]}""",
            json);
        Assert.Equal(message.Content, MessageJson.Deserialize(json).Content);
        Assert.Equal(json, MessageJson.Serialize(MessageJson.Deserialize(json)));
    }

    [Theory]
    [InlineData("""{"role":"user","content":[{"type":"image","data":"AAAA"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"image","media_type":"image/png"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"image","media_type":"image/png","data":42}]}""")]
    public void A_stored_image_missing_its_type_or_data_fails_loudly(string json) =>
        Assert.Throws<JsonException>(() => MessageJson.Deserialize(json));

    [Fact]
    public void Full_text_and_search_text_carry_a_placeholder_never_the_base64()
    {
        var message = new ChatMessage(MessageRole.User, [new TextBlock("what is this"), new ImageBlock("image/png", Base64)]);

        var full = MessageJson.ExtractFullText(message);
        var search = MessageJson.ExtractSearchText(message);

        Assert.Equal("what is this\n[image: image/png, 3 KB]", full);
        Assert.Equal(full, search);
        Assert.DoesNotContain(Base64, full, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("AA==", 1)]
    [InlineData("AAA=", 2)]
    [InlineData("AAAA", 3)]
    [InlineData("AAAAAAAA", 6)]
    public void Decoded_length_is_read_from_length_and_padding(string base64, long expected) =>
        Assert.Equal(expected, MessageJson.DecodedLength(base64));

    [Fact]
    public void A_tiny_image_still_shows_one_kilobyte()
    {
        Assert.Equal("[image: image/gif, 1 KB]", MessageJson.ImagePlaceholder(new ImageBlock("image/gif", "R0lG")));
        Assert.Equal("[image: image/jpeg, 1 KB]", MessageJson.ImagePlaceholder(new ImageBlock("image/jpeg", string.Empty)));
    }

    [Fact]
    public void The_estimate_counts_an_image_as_a_flat_1600_tokens_regardless_of_its_data_length()
    {
        var counter = new ApproximateTokenCounter();
        var small = new ChatMessage(MessageRole.User, [new ImageBlock("image/png", "AAAA")]);
        var large = new ChatMessage(MessageRole.User, [new ImageBlock("image/png", new string('A', 4_000_000))]);
        var withText = new ChatMessage(MessageRole.User, [new TextBlock(new string('x', 400)), new ImageBlock("image/png", "AAAA")]);
        var two = new ChatMessage(MessageRole.User, [new ImageBlock("image/png", "AAAA"), new ImageBlock("image/png", "AAAA")]);

        // 4 tokens of per-message overhead on top of the image.
        Assert.Equal(ApproximateTokenCounter.ImageTokens + 4, counter.Count([small]));
        Assert.Equal(counter.Count([small]), counter.Count([large]));
        Assert.Equal(ApproximateTokenCounter.ImageTokens + 100 + 4, counter.Count([withText]));
        Assert.Equal(2 * ApproximateTokenCounter.ImageTokens + 4, counter.Count([two]));
    }
}
