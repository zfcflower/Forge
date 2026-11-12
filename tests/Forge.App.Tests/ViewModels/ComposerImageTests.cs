using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The composer's image attachments: the format, count and size limits, the model capability gate, removal and taking the
/// images with the message, and the pure helpers behind them. Decoding runs through a fake codec (the headless session has
/// no image decoder).
/// </summary>
[Collection("forge-app-ui")]
public sealed class ComposerImageTests
{
    private static readonly byte[] PngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16];

    // ------------------------------------------------------------------ pure rules

    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, null)]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, null)]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, null)]
    [InlineData(new byte[0], null)]
    public void The_format_is_read_from_the_leading_bytes(byte[] data, string? expected) =>
        Assert.Equal(expected, ImageAttachmentRules.SniffMediaType(data));

    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("C:/photos/IMG_1.JPG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("anim.gif", true)]
    [InlineData("a.webp", true)]
    [InlineData("notes.txt", false)]
    [InlineData("archive.png.zip", false)]
    [InlineData("png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_file_is_recognised_by_its_extension(string? path, bool expected) =>
        Assert.Equal(expected, ImageAttachmentRules.HasImageExtension(path));

    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(2048, 2048, 2048, 2048)]
    [InlineData(4096, 2048, 2048, 1024)]
    [InlineData(3000, 4000, 1536, 2048)]
    [InlineData(100_000, 10, 2048, 1)]
    public void An_image_over_the_long_edge_is_scaled_down_keeping_its_aspect(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ImageAttachmentRules.FitWithin(width, height));

    [Fact]
    public void Fitting_a_degenerate_size_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageAttachmentRules.FitWithin(0, 10));

    [Fact]
    public void The_user_message_puts_the_text_first_and_drops_an_empty_text()
    {
        var png = new ImageBlock("image/png", "AAAA");
        var jpeg = new ImageBlock("image/jpeg", "BBBB");

        var both = SessionRuntime.UserMessage("look", [png, jpeg]);
        Assert.Equal(MessageRole.User, both.Role);
        Assert.Equal([new TextBlock("look"), png, jpeg], both.Content);

        Assert.Equal([png], SessionRuntime.UserMessage(string.Empty, [png]).Content);
        Assert.Equal([new TextBlock("plain")], SessionRuntime.UserMessage("plain", []).Content);
    }

    // ------------------------------------------------------------------ attaching

    [Fact]
    public async Task An_image_is_attached_with_its_sniffed_type_and_base64_data()
    {
        await RunAsync(async (composer, codec) =>
        {
            var changes = 0;
            composer.AttachmentsChanged += () => changes++;

            Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(PngBytes, "shot.png"));

            var attached = Assert.Single(composer.Attachments);
            Assert.Equal("image/png", attached.Block.MediaType);
            Assert.Equal(Convert.ToBase64String(PngBytes), attached.Block.Data);
            Assert.Equal(PngBytes.Length, attached.EncodedBytes);
            Assert.True(composer.HasAttachments);
            Assert.False(composer.HasAttachmentError);
            Assert.Equal(1, changes);
            Assert.Equal(["image/png"], codec.Prepared);
        });
    }

    [Fact]
    public async Task Bytes_that_are_no_supported_image_are_refused_before_decoding()
    {
        await RunAsync(async (composer, codec) =>
        {
            Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageAsync("%PDF-1.7"u8.ToArray(), "doc.png"));
            Assert.Empty(composer.Attachments);
            Assert.True(composer.HasAttachmentError);
            Assert.Contains("doc.png", composer.AttachmentError, StringComparison.Ordinal);
            Assert.Empty(codec.Prepared);
        });
    }

    [Fact]
    public async Task An_image_that_does_not_decode_is_refused()
    {
        await RunAsync(async (composer, codec) =>
        {
            codec.Behaviour = static (_, _) => throw new InvalidDataException("corrupt");

            Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageAsync(PngBytes, "broken.png"));
            Assert.Empty(composer.Attachments);
            Assert.Contains("broken.png", composer.AttachmentError, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_image_still_over_five_megabytes_after_scaling_is_refused()
    {
        await RunAsync(async (composer, codec) =>
        {
            codec.Behaviour = static (_, type) => new PreparedImage(type, new byte[ImageAttachmentRules.MaxEncodedBytes + 1], null);

            Assert.Equal(AttachmentRejection.TooLarge, await composer.AddImageAsync(JpegBytes, "huge.jpg"));
            Assert.Empty(composer.Attachments);
            Assert.Contains("huge.jpg", composer.AttachmentError, StringComparison.Ordinal);

            // Exactly at the limit is still accepted, and a success clears the earlier refusal.
            codec.Behaviour = static (_, type) => new PreparedImage(type, new byte[ImageAttachmentRules.MaxEncodedBytes], null);
            Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(JpegBytes, "big.jpg"));
            Assert.False(composer.HasAttachmentError);
        });
    }

    [Fact]
    public async Task The_scaled_result_decides_the_type_and_bytes_sent()
    {
        await RunAsync(async (composer, codec) =>
        {
            byte[] scaled = [.. PngBytes, 1, 2, 3];
            codec.Behaviour = (_, _) => new PreparedImage("image/png", scaled, null);

            Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(
                [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a'], "anim.gif"));

            var block = Assert.Single(composer.Attachments).Block;
            Assert.Equal("image/png", block.MediaType);
            Assert.Equal(Convert.ToBase64String(scaled), block.Data);
            Assert.Equal(["image/gif"], codec.Prepared);
        });
    }

    [Fact]
    public async Task The_eleventh_image_is_refused()
    {
        await RunAsync(async (composer, _) =>
        {
            for (var i = 0; i < ImageAttachmentRules.MaxImagesPerMessage; i++)
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(PngBytes, $"{i}.png"));
            }

            Assert.Equal(AttachmentRejection.TooMany, await composer.AddImageAsync(PngBytes, "extra.png"));
            Assert.Equal(ImageAttachmentRules.MaxImagesPerMessage, composer.Attachments.Count);
            Assert.True(composer.HasAttachmentError);
        });
    }

    [Fact]
    public async Task Images_still_decoding_count_against_the_limit()
    {
        await RunAsync(async (composer, codec) =>
        {
            using var release = new ManualResetEventSlim();
            codec.Behaviour = (_, type) =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return new PreparedImage(type, PngBytes, null);
            };

            var inFlight = new List<Task<AttachmentRejection>>();
            for (var i = 0; i < ImageAttachmentRules.MaxImagesPerMessage; i++)
            {
                inFlight.Add(composer.AddImageAsync(PngBytes, $"{i}.png"));
            }

            // Nothing has landed yet, but ten are on their way.
            Assert.Empty(composer.Attachments);
            Assert.Equal(AttachmentRejection.TooMany, await composer.AddImageAsync(PngBytes, "extra.png"));

            release.Set();
            Assert.All(await Task.WhenAll(inFlight), result => Assert.Equal(AttachmentRejection.None, result));
            Assert.Equal(ImageAttachmentRules.MaxImagesPerMessage, composer.Attachments.Count);
        });
    }

    [Fact]
    public async Task A_file_without_an_image_extension_is_refused_without_being_read()
    {
        await RunAsync(async (composer, codec) =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".txt");

            Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageFileAsync(missing));
            Assert.Empty(codec.Prepared);
            Assert.Contains(Path.GetFileName(missing), composer.AttachmentError, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_reported_and_not_attached()
    {
        await RunAsync(async (composer, _) =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".png");

            Assert.Equal(AttachmentRejection.Unsupported, await composer.AddImageFileAsync(missing));
            Assert.Empty(composer.Attachments);
            Assert.Contains(Path.GetFileName(missing), composer.AttachmentError, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_image_file_is_read_and_attached()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".JPG");
        await File.WriteAllBytesAsync(path, JpegBytes);
        try
        {
            await RunAsync(async (composer, _) =>
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddImageFileAsync(path));
                Assert.Equal("image/jpeg", Assert.Single(composer.Attachments).Block.MediaType);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ removing, taking, restoring

    [Fact]
    public async Task Remove_drops_one_image_and_take_empties_the_list_in_order()
    {
        await RunAsync(async (composer, codec) =>
        {
            var next = 0;
            codec.Behaviour = (_, type) => new PreparedImage(type, [.. PngBytes, (byte)next++], null);
            for (var i = 0; i < 3; i++)
            {
                await composer.AddImageAsync(PngBytes, $"{i}.png");
            }

            var blocks = composer.Attachments.Select(static image => image.Block).ToList();
            var changes = 0;
            composer.AttachmentsChanged += () => changes++;

            composer.Attachments[1].RemoveCommand.Execute(null);
            Assert.Equal(1, changes);
            Assert.Equal([blocks[0], blocks[2]], composer.Attachments.Select(static image => image.Block));

            var taken = composer.TakeAttachments();
            Assert.Equal([blocks[0], blocks[2]], taken);
            Assert.Empty(composer.Attachments);
            Assert.False(composer.HasAttachments);
            Assert.Equal(2, changes);

            Assert.Empty(composer.TakeAttachments());
            Assert.Equal(2, changes);
        });
    }

    [Fact]
    public async Task Restored_images_come_back_up_to_the_limit()
    {
        await RunAsync((composer, _) =>
        {
            var images = Enumerable.Range(0, 12).Select(i => new ImageBlock("image/png", Convert.ToBase64String(new byte[i + 1]))).ToList();

            composer.RestoreAttachments(images);

            Assert.Equal(images.Take(ImageAttachmentRules.MaxImagesPerMessage), composer.Attachments.Select(static image => image.Block));
            Assert.Equal(1, composer.Attachments[0].EncodedBytes);
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ capability gate

    [Fact]
    public async Task A_model_listing_only_text_inputs_refuses_images()
    {
        await RunAsync(
            async (composer, codec) =>
            {
                Assert.False(composer.AcceptsImages);
                Assert.Equal(ImageStrings.ModelRejects, composer.ImageGateHint);

                Assert.Equal(AttachmentRejection.ModelRejects, await composer.AddImageAsync(PngBytes, "a.png"));
                Assert.Equal(AttachmentRejection.ModelRejects, await composer.AddImageFileAsync("C:/nowhere/a.png"));
                Assert.Empty(composer.Attachments);
                Assert.Empty(codec.Prepared);
                Assert.Equal(ImageStrings.ModelRejects, composer.AttachmentError);
            },
            static (_, _) => ModelInputs.Text);
    }

    [Theory]
    [InlineData((int)(ModelInputs.Text | ModelInputs.Image))]
    [InlineData(-1)]
    public async Task A_model_listing_image_input_or_an_unknown_model_accepts_images(int inputs)
    {
        await RunAsync(
            async (composer, _) =>
            {
                Assert.True(composer.AcceptsImages);
                Assert.Equal(string.Empty, composer.ImageGateHint);
                Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(PngBytes, "a.png"));
            },
            (_, _) => inputs < 0 ? null : (ModelInputs)inputs);
    }

    [Fact]
    public async Task A_catalogue_lookup_that_throws_is_treated_as_unknown()
    {
        await RunAsync(
            (composer, _) =>
            {
                Assert.True(composer.AcceptsImages);
                return Task.CompletedTask;
            },
            static (_, _) => throw new InvalidOperationException("catalogue not loaded"));
    }

    // ------------------------------------------------------------------ queue chip and transcript

    [Fact]
    public async Task A_queued_message_with_images_labels_its_chip()
    {
        await SharedUi.Session.Dispatch(
            () =>
            {
                var prompt = new ComposerPrompt("look", [new ImageBlock("image/png", "AAAA"), new ImageBlock("image/png", "BBBB")]);
                var chip = new QueuedMessageViewModel(prompt, isInterjection: false, static _ => { });
                Assert.True(chip.HasAttachments);
                Assert.Equal(ImageStrings.Count(2), chip.AttachmentsLabel);
                Assert.Same(prompt.Attachments, chip.Prompt.Attachments);

                var plain = new QueuedMessageViewModel("text", isInterjection: false, static _ => { });
                Assert.False(plain.HasAttachments);
                Assert.Equal(string.Empty, plain.AttachmentsLabel);
                Assert.True(ComposerPrompt.FromText(" ").IsEmpty);
                Assert.False(new ComposerPrompt(string.Empty, prompt.Attachments).IsEmpty);
                return true;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_replayed_user_message_shows_its_images_and_an_image_only_message_is_kept()
    {
        var png = new ImageBlock("image/png", Convert.ToBase64String(new byte[2048]));
        var items = await SharedUi.Session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        new StoredMessage(1, new ChatMessage(MessageRole.User, [new TextBlock("what"), png]), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(2, ChatMessage.Assistant("a cat"), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(3, new ChatMessage(MessageRole.User, [png]), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(4, ChatMessage.Assistant("again"), null, DateTimeOffset.UnixEpoch),
                    ]);
                return transcript.Items.ToList();
            },
            CancellationToken.None);

        var users = items.OfType<UserMessageViewModel>().ToList();
        Assert.Equal(2, users.Count);
        Assert.Equal("what", users[0].Text);
        Assert.True(users[0].HasText);
        Assert.Equal(png, Assert.Single(users[0].Images).Block);
        Assert.Equal(2048, users[0].Images[0].EncodedBytes);
        Assert.False(users[1].HasText);
        Assert.True(users[1].HasImages);
    }

    // ------------------------------------------------------------------ harness

    private static async Task RunAsync(
        Func<ComposerViewModel, FakeCodec, Task> body,
        Func<string?, string?, ModelInputs?>? inputs = null)
    {
        var root = TestTempFiles.NewDirectory("forge-images-");
        try
        {
            await SharedUi.Session.Dispatch(
                async () =>
                {
                    using var services = ComposerSuggestionTests.TestServices.Create(root);
                    var codec = new FakeCodec();
                    var composer = new ComposerViewModel(
                        services.Services,
                        _ => { },
                        new ComposerPreferences(),
                        string.Empty,
                        skillSource: () => [],
                        thinkingLevels: static (_, _) => ComposerSuggestionTests.EveryStep,
                        templateSource: static () => PromptTemplateSet.BuiltIn(),
                        imageCodec: codec,
                        imageInputs: inputs ?? (static (_, _) => null));
                    await body(composer, codec);
                    return true;
                },
                CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Hands the bytes back unchanged by default; records the type of every image it was asked to prepare.</summary>
    private sealed class FakeCodec : IImageCodec
    {
        private readonly Lock gate = new();
        private readonly List<string> prepared = [];

        public Func<byte[], string, PreparedImage> Behaviour { get; set; } = static (data, type) => new PreparedImage(type, data, null);

        public IReadOnlyList<string> Prepared
        {
            get
            {
                lock (gate)
                {
                    return [.. prepared];
                }
            }
        }

        public PreparedImage Prepare(byte[] data, string mediaType)
        {
            lock (gate)
            {
                prepared.Add(mediaType);
            }

            return Behaviour(data, mediaType);
        }

        public Bitmap? Thumbnail(byte[] data) => null;
    }
}
