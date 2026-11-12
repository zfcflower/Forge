using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// An attached <see cref="FileBlock"/> (PDF, audio, video) in each wire protocol, the static "which kinds can this wire
/// carry" table, and the clear failure - before anything is sent - for a kind the protocol has no shape for.
/// </summary>
public sealed class FileWireTests
{
    private const string PdfData = "JVBERi0xLjc=";
    private static readonly FileBlock Pdf = new("application/pdf", PdfData, "spec.pdf");
    private static readonly FileBlock UnnamedPdf = new("application/pdf", PdfData);
    private static readonly FileBlock Wav = new("audio/wav", "UklGRg==", "voice.wav");
    private static readonly FileBlock Mp3 = new("audio/mpeg", "SUQz", "song.mp3");
    private static readonly FileBlock Ogg = new("audio/ogg", "T2dnUw==", "voice.ogg");
    private static readonly FileBlock Mp4 = new("video/mp4", "AAAAGGZ0eXA=", "clip.mp4");

    private static ChatMessage UserWith(string text, params ContentBlock[] blocks) =>
        new(MessageRole.User, [new TextBlock(text), .. blocks]);

    // ------------------------------------------------------------------ capability table

    [Fact]
    public void Each_wire_format_declares_the_kinds_it_carries()
    {
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, AnthropicProvider.WireInputs);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf | ModelInputs.Audio, OpenAiCompatibleProvider.WireInputs);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, OpenAiResponsesProvider.WireInputs);
        Assert.Equal(AnthropicProvider.WireInputs, ProviderAttachments.WireInputs(ProviderFactory.AnthropicKind));
        Assert.Equal(OpenAiCompatibleProvider.WireInputs, ProviderAttachments.WireInputs(" OpenAI-Compatible "));
        Assert.Equal(ModelInputs.None, ProviderAttachments.WireInputs("carrier-pigeon"));
        Assert.Equal(ModelInputs.None, ProviderAttachments.WireInputs(null));
    }

    [Theory]
    [InlineData(ProviderProtocol.Anthropic, "application/pdf", true)]
    [InlineData(ProviderProtocol.Anthropic, "image/png", true)]
    [InlineData(ProviderProtocol.Anthropic, "audio/wav", false)]
    [InlineData(ProviderProtocol.Anthropic, "video/mp4", false)]
    [InlineData(ProviderProtocol.OpenAiCompatible, "application/pdf", true)]
    [InlineData(ProviderProtocol.OpenAiCompatible, "audio/wav", true)]
    [InlineData(ProviderProtocol.OpenAiCompatible, "audio/mpeg", true)]
    [InlineData(ProviderProtocol.OpenAiCompatible, "audio/ogg", false)]
    [InlineData(ProviderProtocol.OpenAiCompatible, "video/mp4", false)]
    [InlineData(ProviderProtocol.OpenAiResponses, "application/pdf", true)]
    [InlineData(ProviderProtocol.OpenAiResponses, "image/webp", true)]
    [InlineData(ProviderProtocol.OpenAiResponses, "audio/wav", false)]
    [InlineData(ProviderProtocol.OpenAiResponses, "video/webm", false)]
    public void Can_carry_is_decided_per_media_type(ProviderProtocol protocol, string mediaType, bool expected) =>
        Assert.Equal(expected, ProviderAttachments.CanCarry(protocol, mediaType));

    [Fact]
    public void An_unknown_provider_kind_carries_nothing() =>
        Assert.False(ProviderAttachments.CanCarry("unknown", "application/pdf"));

    [Theory]
    [InlineData("application/pdf", ModelInputs.Pdf)]
    [InlineData("APPLICATION/PDF", ModelInputs.Pdf)]
    [InlineData("image/png", ModelInputs.Image)]
    [InlineData("audio/flac", ModelInputs.Audio)]
    [InlineData("video/quicktime", ModelInputs.Video)]
    [InlineData("text/plain", ModelInputs.Text)]
    [InlineData("application/zip", ModelInputs.None)]
    [InlineData("", ModelInputs.None)]
    [InlineData(null, ModelInputs.None)]
    public void A_media_type_maps_to_its_input_kind(string? mediaType, ModelInputs expected) =>
        Assert.Equal(expected, ProviderAttachments.KindOf(mediaType));

    // ------------------------------------------------------------------ Anthropic

    [Fact]
    public async Task Anthropic_writes_a_pdf_as_a_base64_document_block()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWith("summarise", Pdf)]));

        var content = Assert.Single(handler.Last.Root.GetProperty("messages").EnumerateArray()).GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("summarise", content[0].GetProperty("text").GetString());
        var document = content[1];
        Assert.Equal("document", document.GetProperty("type").GetString());
        var source = document.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("application/pdf", source.GetProperty("media_type").GetString());
        Assert.Equal(PdfData, source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_drops_a_file_block_outside_a_user_message()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages:
        [
            ChatMessage.User("hi"),
            new ChatMessage(MessageRole.Assistant, [new TextBlock("reply"), Pdf]),
        ]));

        Assert.DoesNotContain(PdfData, handler.Last.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("audio/wav")]
    [InlineData("video/mp4")]
    public async Task Anthropic_refuses_audio_and_video_before_sending(string mediaType)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));
        var provider = new AnthropicProvider(TestData.AnthropicConfig(), handler.CreateClient());
        var file = new FileBlock(mediaType, "AAAA", "media.bin");

        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CollectAsync(TestData.Request(messages: [UserWith("listen", file)])));

        Assert.False(error.Retryable);
        Assert.Contains(mediaType, error.Message, StringComparison.Ordinal);
        Assert.Contains("media.bin", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    // ------------------------------------------------------------------ OpenAI-compatible

    [Fact]
    public async Task OpenAi_compatible_writes_a_pdf_as_a_file_part_with_a_data_url()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWith("read", Pdf, UnnamedPdf)]));

        var content = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("file", content[1].GetProperty("type").GetString());
        var file = content[1].GetProperty("file");
        Assert.Equal("spec.pdf", file.GetProperty("filename").GetString());
        Assert.Equal("data:application/pdf;base64," + PdfData, file.GetProperty("file_data").GetString());

        // A missing name is filled in, since the part requires one.
        Assert.Equal("attachment.pdf", content[2].GetProperty("file").GetProperty("filename").GetString());
    }

    [Fact]
    public async Task OpenAi_compatible_writes_wav_and_mp3_as_input_audio()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [new ChatMessage(MessageRole.User, [Wav, Mp3])]));

        var content = handler.Last.Root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("input_audio", content[0].GetProperty("type").GetString());
        Assert.Equal("UklGRg==", content[0].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal("wav", content[0].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("mp3", content[1].GetProperty("input_audio").GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("ogg")]
    [InlineData("mp4")]
    public async Task OpenAi_compatible_refuses_other_audio_and_video_before_sending(string which)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));
        var provider = new OpenAiCompatibleProvider(TestData.OpenAiConfig(), handler.CreateClient());
        var file = which == "ogg" ? Ogg : Mp4;

        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CollectAsync(TestData.Request(messages: [UserWith("play", file)])));

        Assert.False(error.Retryable);
        Assert.Contains(file.MediaType, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    // ------------------------------------------------------------------ Responses

    [Fact]
    public async Task Responses_writes_a_pdf_as_an_input_file_part()
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed));
        var provider = new OpenAiResponsesProvider(ResponsesConfig(), handler.CreateClient());

        await provider.CollectAsync(TestData.Request(messages: [UserWith("read", Pdf)]));

        var item = Assert.Single(handler.Last.Root.GetProperty("input").EnumerateArray());
        var content = item.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        var file = content[1];
        Assert.Equal("input_file", file.GetProperty("type").GetString());
        Assert.Equal("spec.pdf", file.GetProperty("filename").GetString());
        Assert.Equal("data:application/pdf;base64," + PdfData, file.GetProperty("file_data").GetString());
    }

    [Theory]
    [InlineData("audio/wav")]
    [InlineData("video/mp4")]
    public async Task Responses_refuses_audio_and_video_before_sending(string mediaType)
    {
        var handler = new FakeHttpMessageHandler().EnqueueSse(TestData.Sse(Completed));
        var provider = new OpenAiResponsesProvider(ResponsesConfig(), handler.CreateClient());

        var error = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CollectAsync(TestData.Request(messages: [UserWith("x", new FileBlock(mediaType, "AAAA"))])));

        Assert.False(error.Retryable);
        Assert.Equal(0, handler.Calls);
    }

    private const string Completed =
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}";

    private static ProviderConfig ResponsesConfig() =>
        new("responses", ProviderFactory.OpenAiResponsesKind, "https://api.example.test/v1", "sk-test");
}
