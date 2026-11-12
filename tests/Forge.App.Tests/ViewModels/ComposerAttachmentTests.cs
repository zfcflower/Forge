using System.Text;
using Avalonia.Media.Imaging;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Prompts.Templates;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Non-image attachments in the composer: the one classifier behind pick, paste and drop, the per-kind limits, the model and
/// wire gates with their toasts, the blocked state after a model switch, and the chips in the queue and the transcript.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ComposerAttachmentTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%stub\n"u8.ToArray();
    private static readonly byte[] WavBytes = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "fmt "u8];
    private static readonly byte[] Mp4Bytes = [0, 0, 0, 0x18, .. "ftyp"u8, .. "isom"u8, 0, 0, 0, 0];
    private static readonly byte[] PngBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    // ------------------------------------------------------------------ classifier

    public static TheoryData<byte[], string?, string?, string?> Signatures => new()
    {
        { PdfBytes, "a.pdf", "Pdf", "application/pdf" },
        { PdfBytes, "renamed.txt", "Pdf", "application/pdf" },
        { WavBytes, "a.wav", "Audio", "audio/wav" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8], "a.avi", "Video", "video/x-msvideo" },
        { [.. "ID3"u8, 4, 0], "a.mp3", "Audio", "audio/mpeg" },
        { [0xFF, 0xFB, 0x90, 0x00], "bare.mp3", "Audio", "audio/mpeg" },
        { [0xFF, 0xFB, 0x90, 0x00], "bare.bin", null, null },
        { [.. "fLaC"u8, 0], "a.flac", "Audio", "audio/flac" },
        { [.. "OggS"u8, 0], "a.ogg", "Audio", "audio/ogg" },
        { [.. "OggS"u8, 0], "a.ogv", "Video", "video/ogg" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0], "a.webm", "Video", "video/webm" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0], "a.weba", "Audio", "audio/webm" },
        { [0x1A, 0x45, 0xDF, 0xA3, 0], "a.mkv", "Video", "video/x-matroska" },
        { Mp4Bytes, "a.mp4", "Video", "video/mp4" },
        { [0, 0, 0, 0x18, .. "ftyp"u8, .. "M4A "u8], "a.bin", "Audio", "audio/mp4" },
        { [0, 0, 0, 0x18, .. "ftyp"u8, .. "isom"u8], "a.m4a", "Audio", "audio/mp4" },
        { [0, 0, 0, 0x14, .. "ftyp"u8, .. "qt  "u8], "a.mov", "Video", "video/quicktime" },
        { [0, 0, 1, 0xBA, 0], "a.mpg", "Video", "video/mpeg" },
        { PngBytes, "shot.png", "Image", "image/png" },
        { "class A { }\n"u8.ToArray(), "A.cs", "Text", "text/plain" },
        { [0xEF, 0xBB, 0xBF, .. "bom"u8], "bom.txt", "Text", "text/plain" },
        { "plain"u8.ToArray(), null, "Text", "text/plain" },
        { [(byte)'a', 0, (byte)'b'], "nul.txt", null, null },
        { [0xC3, 0x28], "bad.txt", null, null },
        { [], "empty.txt", null, null },
    };

    [Theory]
    [MemberData(nameof(Signatures))]
    public void Files_are_classified_by_their_leading_bytes(byte[] data, string? name, string? kind, string? mediaType)
    {
        var type = FileAttachmentRules.Classify(data, name);

        Assert.Equal(kind, type?.Kind.ToString());
        Assert.Equal(mediaType, type?.MediaType);
    }

    [Theory]
    [InlineData("a.PDF", "Pdf")]
    [InlineData("a.mp3", "Audio")]
    [InlineData("a.webm", "Video")]
    [InlineData("Program.cs", "Text")]
    [InlineData("C:/x/README.md", "Text")]
    [InlineData("a.jpeg", "Image")]
    [InlineData("archive.zip", null)]
    [InlineData("Makefile", null)]
    [InlineData(null, null)]
    public void The_extension_says_what_a_file_claims_to_be(string? path, string? expected) =>
        Assert.Equal(expected, FileAttachmentRules.KindFromExtension(path)?.ToString());

    [Fact]
    public void Each_kind_has_its_size_limit_and_the_read_is_bounded_by_the_claimed_kind()
    {
        Assert.Equal(FileAttachmentRules.MaxPdfBytes, FileAttachmentRules.MaxBytes(AttachmentKind.Pdf));
        Assert.Equal(FileAttachmentRules.MaxAudioBytes, FileAttachmentRules.MaxBytes(AttachmentKind.Audio));
        Assert.Equal(FileAttachmentRules.MaxVideoBytes, FileAttachmentRules.MaxBytes(AttachmentKind.Video));
        Assert.Equal(FileAttachmentRules.MaxTextBytes, FileAttachmentRules.MaxBytes(AttachmentKind.Text));
        Assert.Equal(FileAttachmentRules.MaxTextBytes, FileAttachmentRules.ReadLimit("a.json"));
        Assert.Equal(FileAttachmentRules.MaxPdfBytes, FileAttachmentRules.ReadLimit("unknown.bin"));
    }

    [Fact]
    public void Text_decodes_strictly_and_drops_a_bom()
    {
        Assert.True(FileAttachmentRules.TryDecodeText([0xEF, 0xBB, 0xBF, .. "hi"u8], out var text));
        Assert.Equal("hi", text);
        Assert.True(FileAttachmentRules.TryDecodeText(Encoding.UTF8.GetBytes("\u00e9\u4e2d"), out var wide));
        Assert.Equal("\u00e9\u4e2d", wide);
        Assert.False(FileAttachmentRules.TryDecodeText([0xC3, 0x28], out _));
        Assert.False(FileAttachmentRules.TryDecodeText([(byte)'a', 0], out _));
    }

    // ------------------------------------------------------------------ adding files

    [Fact]
    public async Task A_pdf_is_attached_as_a_file_block_and_taken_after_the_images()
    {
        await RunAsync(async (composer, toasts) =>
        {
            Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(PdfBytes, "spec.pdf"));
            Assert.Equal(AttachmentRejection.None, await composer.AddImageAsync(PngBytes, "shot.png"));

            var chip = Assert.Single(composer.Files);
            Assert.Equal("spec.pdf", chip.Name);
            Assert.Equal(AttachmentKind.Pdf, chip.Kind);
            Assert.Equal(PdfBytes.Length, chip.Bytes);
            Assert.Equal(AttachmentStrings.Caption("PDF", PdfBytes.Length), chip.Caption);
            Assert.True(composer.HasFileAttachments);
            Assert.True(composer.HasImageAttachments);

            var taken = composer.TakeAttachments();
            Assert.IsType<ImageBlock>(taken[0]);
            var file = Assert.IsType<FileBlock>(taken[1]);
            Assert.Equal(new FileBlock("application/pdf", Convert.ToBase64String(PdfBytes), "spec.pdf"), file);
            Assert.False(composer.HasAttachments);
            Assert.Empty(toasts.Calls);
        });
    }

    [Fact]
    public async Task A_text_file_is_inlined_in_the_attachment_shape()
    {
        await RunAsync(async (composer, _) =>
        {
            Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync("var x = 1;\n"u8.ToArray(), "a.js"));

            var chip = Assert.Single(composer.Files);
            Assert.Equal(AttachmentKind.Text, chip.Kind);
            var block = Assert.IsType<TextBlock>(chip.Block);
            Assert.Equal("File: a.js\n```\nvar x = 1;\n```", block.Text);
            Assert.True(TextAttachment.IsAttachment(block));
        });
    }

    [Fact]
    public async Task A_text_file_that_is_not_utf8_is_refused_with_a_toast()
    {
        await RunAsync(async (composer, toasts) =>
        {
            Assert.Equal(AttachmentRejection.NotText, await composer.AddFileAsync([0xC3, 0x28, 0x41], "latin1.txt"));
            Assert.Equal(AttachmentRejection.NotText, await composer.AddFileAsync([(byte)'a', 0, (byte)'b'], "nul.cs"));

            Assert.Empty(composer.Files);
            Assert.Contains("nul.cs", composer.AttachmentError, StringComparison.Ordinal);
            Assert.Equal(2, toasts.Calls.Count);
            Assert.All(toasts.Calls, call =>
            {
                Assert.Equal(NotificationLevel.Warning, call.Level);
                Assert.Equal(AttachmentStrings.RefusedTitleKey, call.TitleKey);
                Assert.Equal(AttachmentStrings.NotTextKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:NotText", call.Key);
            });
        });
    }

    [Fact]
    public async Task An_unknown_binary_is_refused_inline_without_a_toast()
    {
        await RunAsync(async (composer, toasts) =>
        {
            Assert.Equal(AttachmentRejection.Unsupported, await composer.AddFileAsync([0x50, 0x4B, 3, 4, 0], "a.zip"));
            Assert.Empty(composer.Files);
            Assert.Contains("a.zip", composer.AttachmentError, StringComparison.Ordinal);
            Assert.Empty(toasts.Calls);
        });
    }

    [Fact]
    public async Task A_text_file_over_its_limit_is_refused()
    {
        await RunAsync(async (composer, toasts) =>
        {
            var big = new byte[FileAttachmentRules.MaxTextBytes + 1];
            big.AsSpan().Fill((byte)'a');

            Assert.Equal(AttachmentRejection.TooLarge, await composer.AddFileAsync(big, "big.txt"));
            Assert.Empty(composer.Files);
            Assert.Contains("big.txt", composer.AttachmentError, StringComparison.Ordinal);
            Assert.Empty(toasts.Calls);

            // Exactly at the limit is accepted.
            Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(big[..^1], "fits.txt"));
        });
    }

    [Fact]
    public async Task A_file_on_disk_over_its_kind_limit_is_refused_before_reading()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".txt");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(FileAttachmentRules.MaxTextBytes + 1);
        }

        try
        {
            await RunAsync(async (composer, _) =>
            {
                Assert.Equal(AttachmentRejection.TooLarge, await composer.AddFileAsync(path));
                Assert.Empty(composer.Files);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_file_on_disk_is_read_classified_and_attached_and_a_missing_one_is_reported()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, PdfBytes);
        try
        {
            await RunAsync(async (composer, _) =>
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(path));
                Assert.Equal(Path.GetFileName(path), Assert.Single(composer.Files).Name);

                var missing = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".md");
                Assert.Equal(AttachmentRejection.Unsupported, await composer.AddFileAsync(missing));
                Assert.Contains(Path.GetFileName(missing), composer.AttachmentError, StringComparison.Ordinal);
                Assert.Single(composer.Files);
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_eleventh_file_is_refused()
    {
        await RunAsync(async (composer, _) =>
        {
            for (var i = 0; i < FileAttachmentRules.MaxFilesPerMessage; i++)
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync("x"u8.ToArray(), $"{i}.txt"));
            }

            Assert.Equal(AttachmentRejection.TooMany, await composer.AddFileAsync("x"u8.ToArray(), "extra.txt"));
            Assert.Equal(FileAttachmentRules.MaxFilesPerMessage, composer.Files.Count);
            Assert.Equal(AttachmentStrings.TooMany, composer.AttachmentError);
        });
    }

    [Fact]
    public async Task Removing_a_file_chip_drops_it_and_raises_a_change()
    {
        await RunAsync(async (composer, _) =>
        {
            await composer.AddFileAsync(PdfBytes, "a.pdf");
            await composer.AddFileAsync("b"u8.ToArray(), "b.txt");
            var changes = 0;
            composer.AttachmentsChanged += () => changes++;

            composer.Files[0].RemoveCommand.Execute(null);

            Assert.Equal("b.txt", Assert.Single(composer.Files).Name);
            Assert.Equal(1, changes);
        });
    }

    // ------------------------------------------------------------------ model and wire gates

    [Fact]
    public async Task A_model_known_not_to_take_pdf_refuses_it_with_a_toast()
    {
        await RunAsync(
            async (composer, toasts) =>
            {
                Assert.Equal(AttachmentRejection.ModelRejects, await composer.AddFileAsync(PdfBytes, "spec.pdf"));
                Assert.Equal(AttachmentRejection.ModelRejects, await composer.AddFileAsync(PdfBytes, "again.pdf"));

                Assert.Empty(composer.Files);
                Assert.Contains("again.pdf", composer.AttachmentError, StringComparison.Ordinal);
                Assert.Equal(2, toasts.Calls.Count);
                Assert.All(toasts.Calls, call =>
                {
                    Assert.Equal(NotificationLevel.Warning, call.Level);
                    Assert.Equal(AttachmentStrings.ModelRejectsKey, call.BodyKey);

                    // One stable key: repeated refusals count up on one toast.
                    Assert.Equal("attachment-refused:scope:ModelRejects", call.Key);
                });

                // Text files are never gated by the model.
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync("ok"u8.ToArray(), "ok.txt"));
            },
            inputs: static (_, _) => ModelInputs.Text | ModelInputs.Image);
    }

    [Fact]
    public async Task A_file_refused_by_model_is_refused_by_its_name_before_reading()
    {
        await RunAsync(
            async (composer, toasts) =>
            {
                var missing = Path.Combine(Path.GetTempPath(), "forge-" + Guid.NewGuid().ToString("N") + ".mp3");

                Assert.Equal(AttachmentRejection.ModelRejects, await composer.AddFileAsync(missing));
                Assert.Single(toasts.Calls);
            },
            inputs: static (_, _) => ModelInputs.Text);
    }

    [Fact]
    public async Task An_unknown_capability_allows_the_file()
    {
        await RunAsync(
            async (composer, toasts) =>
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(WavBytes, "a.wav"));
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(Mp4Bytes, "a.mp4"));
                Assert.Equal(2, composer.Files.Count);
                Assert.False(composer.AttachmentsBlocked);
                Assert.Empty(toasts.Calls);
            },
            inputs: static (_, _) => null,
            wire: static (_, _) => true);
    }

    [Fact]
    public async Task A_catalogue_lookup_that_throws_counts_as_unknown()
    {
        await RunAsync(
            async (composer, _) => Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(PdfBytes, "a.pdf")),
            inputs: static (_, _) => throw new InvalidOperationException("catalogue not loaded"));
    }

    [Fact]
    public async Task A_wire_format_that_cannot_carry_the_file_refuses_it_with_a_toast()
    {
        // The test provider speaks the Anthropic protocol: PDF and images, no audio or video.
        await RunAsync(async (composer, toasts) =>
        {
            Assert.Equal(AttachmentRejection.WireUnsupported, await composer.AddFileAsync(WavBytes, "voice.wav"));
            Assert.Equal(AttachmentRejection.WireUnsupported, await composer.AddFileAsync(Mp4Bytes, "clip.mp4"));
            Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(PdfBytes, "spec.pdf"));

            Assert.Equal("spec.pdf", Assert.Single(composer.Files).Name);
            Assert.Equal(2, toasts.Calls.Count);
            Assert.All(toasts.Calls, call =>
            {
                Assert.Equal(AttachmentStrings.WireUnsupportedKey, call.BodyKey);
                Assert.Equal("attachment-refused:scope:WireUnsupported", call.Key);
            });
        });
    }

    [Fact]
    public async Task The_wire_gate_is_asked_per_media_type_and_provider()
    {
        var asked = new List<(string? Provider, string MediaType)>();
        await RunAsync(
            async (composer, _) =>
            {
                Assert.Equal(AttachmentRejection.WireUnsupported, await composer.AddFileAsync(WavBytes, "a.wav"));
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync([.. "ID3"u8, 4, 0], "a.mp3"));
            },
            inputs: static (_, _) => null,
            wire: (provider, mediaType) =>
            {
                asked.Add((provider, mediaType));
                return mediaType == "audio/mpeg";
            });

        Assert.Equal(("test", "audio/wav"), asked[0]);
        Assert.Contains(("test", "audio/mpeg"), asked);
        Assert.All(asked, static a => Assert.Equal("test", a.Provider));
    }

    // ------------------------------------------------------------------ model switch

    [Fact]
    public async Task Switching_to_a_model_that_cannot_take_the_attachments_blocks_sending_without_dropping_them()
    {
        await RunAsync(
            async (composer, toasts) =>
            {
                Assert.Equal(AttachmentRejection.None, await composer.AddFileAsync(PdfBytes, "spec.pdf"));
                Assert.False(composer.AttachmentsBlocked);
                var changes = 0;
                composer.AttachmentsChanged += () => changes++;

                composer.Model = "model-b";

                Assert.True(composer.AttachmentsBlocked);
                Assert.Equal(AttachmentStrings.Blocked(ModelInputs.Pdf).Resolve(), composer.AttachmentsBlockedHint);
                Assert.Single(composer.Files);
                Assert.True(changes > 0, "the owner re-evaluates CanSend");
                var toast = Assert.Single(toasts.Calls);
                Assert.Equal(AttachmentStrings.BlockedTitleKey, toast.TitleKey);
                Assert.Equal("attachments-blocked:scope", toast.Key);

                // Staying blocked does not toast again; going back unblocks silently.
                composer.Model = "model-c";
                Assert.True(composer.AttachmentsBlocked);
                Assert.Single(toasts.Calls);
                composer.Model = "model-a";
                Assert.False(composer.AttachmentsBlocked);
                Assert.Equal(string.Empty, composer.AttachmentsBlockedHint);
                Assert.Single(toasts.Calls);

                // Removing the refused file is the other way out.
                composer.Model = "model-b";
                Assert.True(composer.AttachmentsBlocked);
                composer.Files[0].RemoveCommand.Execute(null);
                Assert.False(composer.AttachmentsBlocked);
            },
            inputs: static (model, _) => model == "model-a" ? null : ModelInputs.Text);
    }

    [Fact]
    public async Task Restored_attachments_the_model_cannot_take_are_kept_and_block()
    {
        await RunAsync(
            (composer, _) =>
            {
                composer.RestoreAttachments([new FileBlock("video/mp4", "AAAA", "clip.mp4"), TextAttachment.Create("a.txt", "x")]);

                Assert.Equal(2, composer.Files.Count);
                Assert.Equal(AttachmentKind.Video, composer.Files[0].Kind);
                Assert.Equal(AttachmentKind.Text, composer.Files[1].Kind);
                Assert.Equal("a.txt", composer.Files[1].Name);
                Assert.True(composer.AttachmentsBlocked);
                return Task.CompletedTask;
            },
            inputs: static (_, _) => null);
    }

    // ------------------------------------------------------------------ queue chip and transcript

    [Fact]
    public async Task A_queued_message_with_files_labels_its_chip_as_attachments()
    {
        await SharedUi.Session.Dispatch(
            () =>
            {
                var prompt = new ComposerPrompt("read", [new ImageBlock("image/png", "AAAA"), new FileBlock("application/pdf", "AAAA", "a.pdf")]);
                var chip = new QueuedMessageViewModel(prompt, isInterjection: false, static _ => { });
                Assert.True(chip.HasAttachments);
                Assert.Equal(AttachmentStrings.Count(2), chip.AttachmentsLabel);
                Assert.False(new ComposerPrompt(string.Empty, [new FileBlock("application/pdf", "AAAA")]).IsEmpty);
                return true;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task A_replayed_user_message_shows_file_chips_and_keeps_the_typed_text_apart()
    {
        var pdf = new FileBlock("application/pdf", Convert.ToBase64String(new byte[2048]), "spec.pdf");
        var text = TextAttachment.Create("notes.md", "# notes");
        var items = await SharedUi.Session.Dispatch(
            () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                TranscriptReplay.Apply(
                    transcript,
                    [
                        new StoredMessage(1, new ChatMessage(MessageRole.User, [new TextBlock("read these"), pdf, text]), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(2, ChatMessage.Assistant("done"), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(3, new ChatMessage(MessageRole.User, [new FileBlock("audio/wav", "AAAA")]), null, DateTimeOffset.UnixEpoch),
                        new StoredMessage(4, ChatMessage.Assistant("heard"), null, DateTimeOffset.UnixEpoch),
                    ]);
                return transcript.Items.ToList();
            },
            CancellationToken.None);

        var users = items.OfType<UserMessageViewModel>().ToList();
        Assert.Equal(2, users.Count);
        Assert.Equal("read these", users[0].Text);
        Assert.False(users[0].HasImages);
        Assert.True(users[0].HasFiles);
        Assert.Equal(["spec.pdf", "notes.md"], users[0].Files.Select(static file => file.Name));
        Assert.Equal(2048, users[0].Files[0].Bytes);
        Assert.Equal(AttachmentKind.Text, users[0].Files[1].Kind);
        Assert.Equal(7, users[0].Files[1].Bytes);

        Assert.False(users[1].HasText);
        var audio = Assert.Single(users[1].Files);
        Assert.Equal("audio/wav", audio.Name);
        Assert.Equal(AttachmentKind.Audio, audio.Kind);
    }

    // ------------------------------------------------------------------ harness

    private static async Task RunAsync(
        Func<ComposerViewModel, RecordingNotifications, Task> body,
        Func<string?, string?, ModelInputs?>? inputs = null,
        Func<string?, string, bool>? wire = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await SharedUi.Session.Dispatch(
                async () =>
                {
                    using var services = ComposerSuggestionTests.TestServices.Create(root);
                    var toasts = new RecordingNotifications();
                    var composer = new ComposerViewModel(
                        services.Services,
                        _ => { },
                        new ComposerPreferences(),
                        string.Empty,
                        skillSource: () => [],
                        thinkingLevels: static (_, _) => ComposerSuggestionTests.EveryStep,
                        templateSource: static () => PromptTemplateSet.BuiltIn(),
                        imageCodec: new PassThroughCodec(),
                        imageInputs: inputs ?? (static (_, _) => null),
                        wireCarries: wire,
                        notifications: toasts,
                        notificationScope: "scope");
                    await body(composer, toasts);
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

    internal sealed class RecordingNotifications : INotifications
    {
        private readonly List<(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key)> calls = [];

        public IReadOnlyList<(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key)> Calls => calls;

        public Task NotifyAsync(NotificationLevel level, LocText title, LocText body, string? key = null, CancellationToken ct = default)
        {
            calls.Add((level, title.Key, body.Key, key));
            return Task.CompletedTask;
        }
    }

    private sealed class PassThroughCodec : IImageCodec
    {
        public PreparedImage Prepare(byte[] data, string mediaType) => new(mediaType, data, null);

        public Bitmap? Thumbnail(byte[] data) => null;
    }
}
