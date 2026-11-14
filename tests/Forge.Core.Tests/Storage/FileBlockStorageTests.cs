using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// An attached file in the message log (round trip, placeholder in every text view, token estimate) and the inline shape of
/// an attached text file.
/// </summary>
public sealed class FileBlockStorageTests
{
    // 3000 bytes of payload: exactly 4000 base64 characters, no padding.
    private static readonly string Base64 = Convert.ToBase64String(new byte[3000]);

    // ------------------------------------------------------------------ MessageJson

    [Fact]
    public void A_named_file_block_round_trips_with_a_fixed_field_order()
    {
        var message = new ChatMessage(
            MessageRole.User,
            [new TextBlock("read"), new FileBlock("application/pdf", "JVBE", "spec.pdf")]);

        var json = MessageJson.Serialize(message);

        Assert.Equal(
            """{"role":"user","content":[{"type":"text","text":"read"},{"type":"file","media_type":"application/pdf","data":"JVBE","name":"spec.pdf"}]}""",
            json);
        Assert.Equal(message.Content, MessageJson.Deserialize(json).Content);
        Assert.Equal(json, MessageJson.Serialize(MessageJson.Deserialize(json)));
    }

    [Fact]
    public void An_unnamed_file_block_omits_the_name_and_reads_back_as_null()
    {
        var message = new ChatMessage(MessageRole.User, [new FileBlock("audio/wav", "UklG")]);

        var json = MessageJson.Serialize(message);

        Assert.Equal("""{"role":"user","content":[{"type":"file","media_type":"audio/wav","data":"UklG"}]}""", json);
        var file = Assert.IsType<FileBlock>(Assert.Single(MessageJson.Deserialize(json).Content));
        Assert.Null(file.Name);
        Assert.Equal("audio/wav", file.MediaType);
    }

    [Theory]
    [InlineData("""{"role":"user","content":[{"type":"file","data":"AAAA"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"file","media_type":"application/pdf"}]}""")]
    [InlineData("""{"role":"user","content":[{"type":"file","media_type":"application/pdf","data":42}]}""")]
    public void A_stored_file_missing_its_type_or_data_fails_loudly(string json) =>
        Assert.Throws<JsonException>(() => MessageJson.Deserialize(json));

    [Fact]
    public void Full_text_and_search_text_carry_a_placeholder_never_the_base64()
    {
        var message = new ChatMessage(
            MessageRole.User,
            [new TextBlock("summarise"), new FileBlock("application/pdf", Base64, "report.pdf")]);

        var full = MessageJson.ExtractFullText(message);
        var search = MessageJson.ExtractSearchText(message);

        Assert.Equal("summarise\n[file: report.pdf, 3 KB]", full);
        Assert.Equal(full, search);
        Assert.DoesNotContain(Base64, full, StringComparison.Ordinal);
    }

    [Fact]
    public void The_placeholder_falls_back_to_the_media_type_and_shows_at_least_one_kilobyte()
    {
        Assert.Equal("[file: video/mp4, 1 KB]", ContentPlaceholders.FilePlaceholder(new FileBlock("video/mp4", "AAAA")));
        Assert.Equal("[file: audio/mpeg, 1 KB]", ContentPlaceholders.FilePlaceholder(new FileBlock("audio/mpeg", string.Empty, "  ")));
        Assert.Equal("[file: a.pdf, 3 KB]", ContentPlaceholders.FilePlaceholder(new FileBlock("application/pdf", Base64, "a.pdf")));
    }

    // ------------------------------------------------------------------ token estimate

    [Fact]
    public void The_estimate_counts_a_file_by_its_decoded_size()
    {
        var counter = new ApproximateTokenCounter();
        var tiny = new ChatMessage(MessageRole.User, [new FileBlock("application/pdf", "AAAA")]);
        var threeKb = new ChatMessage(MessageRole.User, [new FileBlock("application/pdf", Base64)]);

        Assert.Equal(ApproximateTokenCounter.FileTokensPerKilobyte, ApproximateTokenCounter.FileTokens(new FileBlock("x/y", "AAAA")));
        Assert.Equal(3L * ApproximateTokenCounter.FileTokensPerKilobyte, ApproximateTokenCounter.FileTokens(new FileBlock("x/y", Base64)));

        // 4 tokens of per-message overhead on top of the file.
        Assert.Equal(ApproximateTokenCounter.FileTokensPerKilobyte + 4, counter.Count([tiny]));
        Assert.Equal(3 * ApproximateTokenCounter.FileTokensPerKilobyte + 4, counter.Count([threeKb]));
    }

    // ------------------------------------------------------------------ text attachments

    [Fact]
    public void A_text_attachment_names_the_file_and_fences_the_content()
    {
        var block = TextAttachment.Create("notes.md", "# hi\nthere");

        Assert.Equal("File: notes.md\n```\n# hi\nthere\n```", block.Text);
        Assert.True(TextAttachment.TryParse(block.Text, out var name, out var bytes));
        Assert.Equal("notes.md", name);
        Assert.Equal(10, bytes);
        Assert.True(TextAttachment.IsAttachment(block));
    }

    [Fact]
    public void The_fence_is_longer_than_any_backtick_run_in_the_content()
    {
        var content = "before\n````\ncode\n````\nafter\n";
        var block = TextAttachment.Create("README.md", content);

        Assert.StartsWith("File: README.md\n`````\n", block.Text, StringComparison.Ordinal);
        Assert.EndsWith("after\n`````", block.Text, StringComparison.Ordinal);
        Assert.True(TextAttachment.TryParse(block.Text, out _, out var bytes));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(content.TrimEnd('\n')), bytes);
    }

    [Fact]
    public void Empty_content_a_multibyte_body_and_an_unsafe_name_still_parse()
    {
        var empty = TextAttachment.Create("empty.txt", string.Empty);
        Assert.True(TextAttachment.TryParse(empty.Text, out var emptyName, out var emptyBytes));
        Assert.Equal("empty.txt", emptyName);
        Assert.Equal(0, emptyBytes);

        var wide = TextAttachment.Create("wide.txt", "é中");
        Assert.True(TextAttachment.TryParse(wide.Text, out _, out var wideBytes));
        Assert.Equal(5, wideBytes);

        var odd = TextAttachment.Create("a\nb.txt", "x");
        Assert.True(TextAttachment.TryParse(odd.Text, out var oddName, out _));
        Assert.Equal("a b.txt", oddName);

        var unnamed = TextAttachment.Create("   ", "x");
        Assert.True(TextAttachment.TryParse(unnamed.Text, out var fallback, out _));
        Assert.Equal("file", fallback);
    }

    [Theory]
    [InlineData("plain typed text")]
    [InlineData("File: x.txt")]
    [InlineData("File: x.txt\nno fence\n")]
    [InlineData("File: x.txt\n```\nunclosed")]
    [InlineData("File: x.txt\n```\nbody```")]
    [InlineData("File: \n```\nbody\n```")]
    [InlineData("File: x.txt\n``\nbody\n``")]
    [InlineData("")]
    public void Typed_text_is_never_mistaken_for_an_attachment(string text)
    {
        Assert.False(TextAttachment.TryParse(text, out _, out _));
        Assert.False(TextAttachment.IsAttachment(new TextBlock(text)));
    }

    [Fact]
    public void Only_text_blocks_can_be_text_attachments() =>
        Assert.False(TextAttachment.IsAttachment(new FileBlock("text/plain", "AAAA", "a.txt")));
}
