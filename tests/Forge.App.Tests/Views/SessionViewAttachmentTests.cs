using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.Tests.ViewModels;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests.Views;

/// <summary>
/// The real <see cref="SessionView"/>'s attachment code-behind (<c>SessionView.Images.cs</c>), run headless: dropping files on
/// the composer card, pasting from the clipboard (files, bitmap, text), the attach button, and what the card then shows (chips,
/// the refusal line, the blocked hint). The composer's own classifier is covered by <c>ComposerAttachmentTests</c>; these tests
/// prove the view hands the right things to it and that the visual tree follows. Model capabilities come from a real catalogue
/// snapshot, so the gates are the production ones, not a stub.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionViewAttachmentTests
{
    private const string SessionId = "attach-session";
    private const string TextOnly = "text-model";
    private const string ImageModel = "image-model";
    private const string FullModel = "full-model";

    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(90);
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%stub\n"u8.ToArray();
    private static readonly byte[] OnePixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static readonly byte[] WavBytes = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. "fmt "u8];

    // ------------------------------------------------------------------ drop

    [Fact]
    public async Task Dropping_an_image_a_pdf_and_a_text_file_adds_a_thumbnail_and_two_chips_with_the_text_inlined()
    {
        await RunAsync(FullModel, async rig =>
        {
            var png = rig.WritePng("shot.png");
            var pdf = rig.Write("spec.pdf", PdfBytes);
            var text = rig.Write("notes.txt", "hello\nworld"u8.ToArray());

            rig.Drop(png, pdf, text);
            Assert.True(await UntilAsync(() => rig.Composer.Attachments.Count == 1 && rig.Composer.Files.Count == 2));
            rig.Settle();

            var image = Assert.Single(rig.Composer.Attachments);
            Assert.Equal("image/png", Assert.IsType<ImageBlock>(image.Block).MediaType);
            Assert.Equal(["spec.pdf", "notes.txt"], rig.Composer.Files.Select(static file => file.Name));
            Assert.Equal(AttachmentKind.Pdf, rig.Composer.Files[0].Kind);
            Assert.Equal(AttachmentKind.Text, rig.Composer.Files[1].Kind);

            var pdfBlock = Assert.IsType<FileBlock>(rig.Composer.Files[0].Block);
            Assert.Equal("application/pdf", pdfBlock.MediaType);
            Assert.Equal("spec.pdf", pdfBlock.Name);

            var inlined = Assert.IsType<Forge.Abstractions.TextBlock>(rig.Composer.Files[1].Block);
            Assert.StartsWith("File: notes.txt", inlined.Text, StringComparison.Ordinal);
            Assert.True(TextAttachment.TryParse(inlined.Text, out var name, out var bytes));
            Assert.Equal("notes.txt", name);
            Assert.Equal(11, bytes);
            Assert.Contains("hello\nworld", inlined.Text, StringComparison.Ordinal);

            // The tree shows them: the file strip is visible with both names, the image strip with its remove button.
            var strip = rig.ById<Control>("composer-file-attachments");
            Assert.True(strip.IsVisible);
            var shown = strip.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ToList();
            Assert.Contains("spec.pdf", shown);
            Assert.Contains("notes.txt", shown);
            Assert.Equal(2, rig.AllById<Button>("composer-file-remove").Count);
            Assert.True(rig.ById<Control>("composer-attachments").IsVisible);
            Assert.Single(rig.AllById<Button>("composer-attachment-remove"));
            Assert.Empty(rig.Refusals);
            Assert.False(rig.Composer.HasAttachmentError);
        });
    }

    [Fact]
    public async Task A_pdf_dropped_on_a_model_without_file_input_is_refused_with_the_documented_toast_and_no_chip()
    {
        await RunAsync(ImageModel, async rig =>
        {
            rig.Drop(rig.Write("spec.pdf", PdfBytes));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            rig.Settle();

            Assert.Empty(rig.Composer.Files);
            Assert.Empty(rig.Composer.Attachments);
            var toast = Assert.Single(rig.Refusals);
            Assert.Equal(NotificationLevel.Warning, toast.Level);
            Assert.Equal(AttachmentStrings.RefusedTitleKey, toast.TitleKey);
            Assert.Equal(AttachmentStrings.ModelRejectsKey, toast.BodyKey);
            Assert.Equal("attachment-refused:" + SessionId + ":ModelRejects", toast.Key);
            Assert.Equal(AttachmentStrings.ModelRejects("spec.pdf", AttachmentKind.Pdf).Resolve(), rig.ErrorLine());
        });
    }

    [Fact]
    public async Task An_image_dropped_on_a_text_only_model_is_refused_with_the_image_toast_and_no_thumbnail()
    {
        await RunAsync(TextOnly, async rig =>
        {
            rig.Drop(rig.WritePng("shot.png"));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));

            Assert.Empty(rig.Composer.Attachments);
            var toast = Assert.Single(rig.Refusals);
            Assert.Equal(ImageStrings.ModelRejectsKey, toast.BodyKey);
            Assert.Equal("attachment-refused:" + SessionId + ":ModelRejects", toast.Key);
            Assert.False(rig.Composer.AcceptsImages);
        });
    }

    [Fact]
    public async Task Audio_dropped_on_a_provider_whose_wire_format_cannot_carry_it_is_refused_even_when_the_model_takes_it()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("clip.wav", WavBytes));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));

            Assert.Empty(rig.Composer.Files);
            var toast = Assert.Single(rig.Refusals);
            Assert.Equal(AttachmentStrings.WireUnsupportedKey, toast.BodyKey);
            Assert.Equal("attachment-refused:" + SessionId + ":WireUnsupported", toast.Key);
        });
    }

    [Fact]
    public async Task A_binary_file_named_as_text_is_refused_with_a_toast_and_no_chip()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("broken.txt", [(byte)'a', 0, (byte)'b']));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));

            Assert.Empty(rig.Composer.Files);
            Assert.Equal(AttachmentStrings.NotTextKey, Assert.Single(rig.Refusals).BodyKey);
        });
    }

    [Fact]
    public async Task An_unsupported_file_and_a_dropped_folder_show_the_refusal_line_and_add_nothing()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("archive.zip", [0x50, 0x4B, 0x03, 0x04, 0xFF, 0x00]));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Equal(AttachmentStrings.Unsupported("archive.zip").Resolve(), rig.Composer.AttachmentError);
            Assert.Empty(rig.Composer.Files);

            var folder = Path.Combine(rig.Directory, "a-folder");
            Directory.CreateDirectory(folder);
            rig.Composer.DismissAttachmentError();
            rig.DropItems(Storage.Folder(folder));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Empty(rig.Composer.Files);
            Assert.Empty(rig.Composer.Attachments);
        });
    }

    [Fact]
    public async Task A_file_that_vanished_before_the_drop_was_handled_shows_a_read_failure_and_adds_nothing()
    {
        await RunAsync(FullModel, async rig =>
        {
            var missing = Path.Combine(rig.Directory, "gone.txt");
            rig.Drop(missing);
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));

            Assert.Empty(rig.Composer.Files);
            Assert.Contains("gone.txt", rig.Composer.AttachmentError, StringComparison.Ordinal);
        });
    }
    [Theory]
    [InlineData("big.pdf", FileAttachmentRules.MaxPdfBytes)]
    [InlineData("big.wav", FileAttachmentRules.MaxAudioBytes)]
    [InlineData("big.mp4", FileAttachmentRules.MaxVideoBytes)]
    [InlineData("big.txt", FileAttachmentRules.MaxTextBytes)]
    public async Task A_file_one_byte_over_its_kinds_limit_is_refused_before_it_is_read(string name, long limit)
    {
        await RunAsync(null, async rig =>
        {
            rig.Drop(rig.Sparse(name, limit + 1));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            rig.Settle();

            Assert.Empty(rig.Composer.Files);
            Assert.Equal(AttachmentStrings.TooLarge(name, limit).Resolve(), rig.ErrorLine());
            Assert.Empty(rig.Refusals);
        });
    }

    [Fact]
    public async Task A_text_file_of_exactly_the_limit_is_still_accepted()
    {
        await RunAsync(null, async rig =>
        {
            rig.Drop(rig.Write("edge.txt", new byte[(int)FileAttachmentRules.MaxTextBytes].Select(static _ => (byte)'a').ToArray()));
            Assert.True(await UntilAsync(() => rig.Composer.Files.Count == 1));

            Assert.Equal(FileAttachmentRules.MaxTextBytes, rig.Composer.Files[0].Bytes);
            Assert.False(rig.Composer.HasAttachmentError);
        });
    }

    [Fact]
    public async Task Dropping_twelve_files_keeps_ten_and_reports_the_cap_without_trying_the_rest()
    {
        await RunAsync(null, async rig =>
        {
            var paths = Enumerable.Range(0, 12).Select(i => rig.Write($"f{i:D2}.txt", "x"u8.ToArray())).ToArray();

            rig.Drop(paths);
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            rig.Settle();

            Assert.Equal(FileAttachmentRules.MaxFilesPerMessage, rig.Composer.Files.Count);
            Assert.Equal(
                Enumerable.Range(0, 10).Select(static i => $"f{i:D2}.txt"),
                rig.Composer.Files.Select(static file => file.Name));
            Assert.Equal(AttachmentStrings.TooMany, rig.ErrorLine());
            Assert.Equal(10, rig.AllById<Button>("composer-file-remove").Count);

            // A later drop is refused for the same reason; the chips already there are untouched.
            rig.Composer.DismissAttachmentError();
            rig.Drop(rig.Write("extra.txt", "x"u8.ToArray()));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Equal(10, rig.Composer.Files.Count);
            Assert.DoesNotContain(rig.Composer.Files, static file => file.Name == "extra.txt");
        });
    }

    // ------------------------------------------------------------------ drag over

    [Fact]
    public async Task Dragging_files_over_the_card_offers_copy_only_and_marks_the_event_handled()
    {
        await RunAsync(FullModel, rig =>
        {
            var path = rig.Write("a.txt", "x"u8.ToArray());
            var args = rig.DragOver(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, FileTransfer(path));

            Assert.Equal(DragDropEffects.Copy, args.DragEffects);
            Assert.True(args.Handled);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Dragging_text_over_the_card_is_left_alone_so_nothing_is_offered_for_attachment()
    {
        await RunAsync(FullModel, rig =>
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("just words"));
            var args = rig.DragOver(DragDropEffects.Copy | DragDropEffects.Move, transfer);

            Assert.False(args.Handled);
            Assert.Equal(DragDropEffects.Copy | DragDropEffects.Move, args.DragEffects);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Dropping_text_attaches_nothing_and_leaves_the_event_to_others()
    {
        await RunAsync(FullModel, async rig =>
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText("just words"));
            var args = rig.Raise(DragDrop.DropEvent, transfer, DragDropEffects.Copy);
            await rig.QuietAsync();

            Assert.False(args.Handled);
            Assert.Empty(rig.Composer.Files);
            Assert.Empty(rig.Composer.Attachments);
        });
    }

    // ------------------------------------------------------------------ paste

    [Fact]
    public async Task Pasting_plain_text_stays_text_and_attaches_nothing()
    {
        await RunAsync(FullModel, async rig =>
        {
            await rig.Clipboard.SetTextAsync("hello world");
            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Workspace.Draft == "hello world"));
            Assert.Equal("hello world", rig.Input.Text);
            Assert.False(rig.Composer.HasAttachments);
            Assert.Empty(rig.Refusals);
        });
    }

    [Fact]
    public async Task Pasting_with_an_empty_clipboard_does_nothing_and_does_not_throw()
    {
        await RunAsync(FullModel, async rig =>
        {
            await rig.Clipboard.ClearAsync();
            rig.Input.Paste();
            await rig.QuietAsync();

            Assert.False(rig.Composer.HasAttachments);
            Assert.True(string.IsNullOrEmpty(rig.Workspace.Draft));
        });
    }

    [Fact]
    public async Task Pasting_a_bitmap_is_taken_from_the_text_box_and_handed_to_the_composer_not_pasted_as_text()
    {
        // The headless platform cannot encode a bitmap (Bitmap.Save writes zero bytes), so the composer receives empty PNG
        // bytes and refuses them as unsupported; what this proves is the routing: the paste is intercepted, the clipboard
        // bitmap reaches AddImageAsync, and no text is inserted. The success path (PNG -> thumbnail) is proven with dropped
        // files above, which share AddImageAsync.
        await RunAsync(FullModel, async rig =>
        {
            await rig.Clipboard.SetBitmapAsync(NewBitmap());
            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError || rig.Composer.Attachments.Count == 1));
            Assert.True(string.IsNullOrEmpty(rig.Input.Text));
            Assert.True(string.IsNullOrEmpty(rig.Workspace.Draft));
            Assert.Empty(rig.Refusals);
        });
    }
    [Fact]
    public async Task Pasting_a_bitmap_on_a_text_only_model_is_refused_with_a_toast_and_is_not_pasted_as_text()
    {
        await RunAsync(TextOnly, async rig =>
        {
            await rig.Clipboard.SetBitmapAsync(NewBitmap());
            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Empty(rig.Composer.Attachments);
            Assert.Equal(ImageStrings.ModelRejectsKey, Assert.Single(rig.Refusals).BodyKey);
            Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        });
    }

    [Fact]
    public async Task Pasting_file_paths_attaches_each_file_through_the_classifier()
    {
        await RunAsync(FullModel, async rig =>
        {
            var pdf = rig.Write("spec.pdf", PdfBytes);
            var text = rig.Write("a.md", "# title"u8.ToArray());
            await rig.Clipboard.SetFilesAsync([Storage.File(pdf), Storage.File(text)]);

            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Composer.Files.Count == 2));
            Assert.Equal(["spec.pdf", "a.md"], rig.Composer.Files.Select(static file => file.Name));
            Assert.Equal(AttachmentKind.Text, rig.Composer.Files[1].Kind);
            Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        });
    }

    [Fact]
    public async Task Pasting_a_pdf_path_on_a_model_without_file_input_is_refused_and_nothing_is_pasted()
    {
        await RunAsync(ImageModel, async rig =>
        {
            await rig.Clipboard.SetFilesAsync([Storage.File(rig.Write("spec.pdf", PdfBytes))]);

            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Empty(rig.Composer.Files);
            var toast = Assert.Single(rig.Refusals);
            Assert.Equal(AttachmentStrings.ModelRejectsKey, toast.BodyKey);
            Assert.Equal("attachment-refused:" + SessionId + ":ModelRejects", toast.Key);
            Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        });
    }

    [Fact]
    public async Task Pasting_an_unsupported_file_path_shows_the_refusal_line_and_inserts_no_text()
    {
        await RunAsync(FullModel, async rig =>
        {
            await rig.Clipboard.SetFilesAsync([Storage.File(rig.Write("archive.zip", [0x50, 0x4B, 0x03, 0x04, 0xFF]))]);

            rig.Input.Paste();

            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            Assert.Empty(rig.Composer.Files);
            Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        });
    }

    // ------------------------------------------------------------------ pick

    [Fact]
    public async Task The_attach_button_with_a_platform_that_has_no_file_picker_does_nothing_and_does_not_throw()
    {
        // The headless platform's storage provider is the framework's no-op one and TopLevel.StorageProvider cannot be
        // replaced from outside, so the picked-files half of OnAttachImagesClick (paths -> AttachFilesAsync) is covered by the
        // drop and paste tests, which share AttachFilesAsync; this proves the click itself is safe when nothing is picked.
        await RunAsync(FullModel, async rig =>
        {
            var button = rig.ById<Button>("composer-attach-images");
            Assert.True(button.IsEnabled);

            rig.Click(button);
            await rig.QuietAsync();

            Assert.False(rig.Composer.HasAttachments);
            Assert.False(rig.Composer.HasAttachmentError);
            Assert.Empty(rig.Refusals);
        });
    }

    // ------------------------------------------------------------------ chips, blocked state, send

    [Fact]
    public async Task Clicking_a_chips_remove_button_removes_only_that_chip_and_clears_the_refusal_line()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("one.txt", "1"u8.ToArray()), rig.Write("two.txt", "2"u8.ToArray()));
            Assert.True(await UntilAsync(() => rig.Composer.Files.Count == 2));
            rig.Settle();

            var removes = rig.AllById<Button>("composer-file-remove");
            Assert.Equal(2, removes.Count);
            rig.Click(removes[0]);
            rig.Settle();

            Assert.Equal("two.txt", Assert.Single(rig.Composer.Files).Name);
            Assert.Single(rig.AllById<Button>("composer-file-remove"));

            rig.Click(rig.AllById<Button>("composer-file-remove")[0]);
            rig.Settle();
            Assert.Empty(rig.Composer.Files);
            Assert.False(rig.ById<Control>("composer-file-attachments").IsVisible);
        });
    }

    [Fact]
    public async Task Clicking_the_thumbnails_remove_button_removes_the_image()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.WritePng("shot.png"));
            Assert.True(await UntilAsync(() => rig.Composer.Attachments.Count == 1));
            rig.Settle();

            rig.Click(rig.AllById<Button>("composer-attachment-remove").Single());
            rig.Settle();

            Assert.Empty(rig.Composer.Attachments);
            Assert.False(rig.Composer.HasAttachments);
        });
    }

    [Fact]
    public async Task The_dismiss_button_clears_the_refusal_line()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("archive.zip", [0x50, 0x4B, 0x03, 0x04, 0xFF]));
            Assert.True(await UntilAsync(() => rig.Composer.HasAttachmentError));
            rig.Settle();
            Assert.NotEmpty(rig.ErrorLine());

            var dismiss = rig.Window.GetVisualDescendants().OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == Loc.Instance["session.images.dismiss"]);
            rig.Click(dismiss);
            rig.Settle();

            Assert.False(rig.Composer.HasAttachmentError);
        });
    }

    [Fact]
    public async Task Switching_to_a_model_that_cannot_take_a_chip_shows_the_blocked_hint_toasts_once_and_disables_send()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("spec.pdf", PdfBytes));
            Assert.True(await UntilAsync(() => rig.Composer.Files.Count == 1));
            rig.Workspace.Draft = "read it";
            rig.Settle();
            Assert.True(rig.ById<Button>("session-send").IsEnabled);
            Assert.False(rig.ById<Control>("composer-attachments-blocked").IsVisible);

            rig.Composer.Model = TextOnly;
            rig.Settle();

            Assert.True(rig.ById<Control>("composer-attachments-blocked").IsVisible);
            Assert.False(rig.ById<Button>("session-send").IsEnabled);
            Assert.Single(rig.Composer.Files);
            var blocked = Assert.Single(rig.Toasts.Calls, static call => call.Key?.StartsWith("attachments-blocked", StringComparison.Ordinal) == true);
            Assert.Equal("attachments-blocked:" + SessionId, blocked.Key);

            // Removing the refused chip is the way out: the hint goes and send comes back.
            rig.Click(rig.AllById<Button>("composer-file-remove").Single());
            rig.Settle();
            Assert.False(rig.ById<Control>("composer-attachments-blocked").IsVisible);
            Assert.True(rig.ById<Button>("session-send").IsEnabled);
        });
    }

    [Fact]
    public async Task Sending_takes_the_chips_out_of_the_composer_and_puts_them_on_the_user_message()
    {
        await RunAsync(FullModel, async rig =>
        {
            rig.Drop(rig.Write("spec.pdf", PdfBytes), rig.Write("notes.txt", "hi"u8.ToArray()));
            Assert.True(await UntilAsync(() => rig.Composer.Files.Count == 2));
            rig.Workspace.Draft = "please read";
            rig.Settle();

            var send = rig.ById<Button>("session-send");
            Assert.True(send.IsEnabled);
            rig.Click(send);

            Assert.True(await UntilAsync(() => !rig.Composer.HasAttachments));
            rig.Settle();
            Assert.Empty(rig.Composer.Files);
            Assert.Empty(rig.Composer.Attachments);
            Assert.False(rig.ById<Control>("composer-file-attachments").IsVisible);
            Assert.Equal(string.Empty, rig.Workspace.Draft);

            var message = rig.Workspace.Transcript.Items.OfType<UserMessageViewModel>().Single();
            Assert.Equal("please read", message.Text);
            Assert.Equal(["spec.pdf", "notes.txt"], message.Files.Select(static file => file.Name));
            Assert.True(await UntilAsync(() => !rig.Workspace.IsRunning));
        });
    }

    // ------------------------------------------------------------------ harness

    private static DataTransfer FileTransfer(params string[] paths)
    {
        var transfer = new DataTransfer();
        foreach (var path in paths)
        {
            transfer.Add(DataTransferItem.CreateFile(Storage.File(path)));
        }

        return transfer;
    }

    private static WriteableBitmap NewBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * frame.Size.Height];
            Array.Fill(pixels, (byte)0x80);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }

        return bitmap;
    }

    /// <summary>Waits (yielding to the dispatcher) until a background read or encode has landed.</summary>
    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return condition();
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the shared UI thread against a real <see cref="SessionView"/> in a window. The model
    /// is one of the three catalogue entries (text; text and image; everything) or null for an unlisted one, whose inputs are
    /// unknown and so gate nothing.
    /// </summary>
    private static async Task RunAsync(string? model, Func<Rig, Task> body)
    {
        using var language = new LanguageScope(UiLanguage.English);
        var root = Path.Combine(Path.GetTempPath(), "forge-view-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await SharedUi.Session.Dispatch(
                async () =>
                {
                    using var rig = Rig.Create(root, model);
                    await body(rig);
                    return true;
                },
                new CancellationTokenSource(StepBudget).Token);
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

    private sealed class Rig : IDisposable
    {
        private readonly ComposerSuggestionTests.TestServices services;

        private Rig(
            ComposerSuggestionTests.TestServices services,
            SessionWorkspaceViewModel workspace,
            ComposerAttachmentTests.RecordingNotifications toasts,
            Window window,
            string directory)
        {
            this.services = services;
            Workspace = workspace;
            Toasts = toasts;
            Window = window;
            Directory = directory;
        }

        public SessionWorkspaceViewModel Workspace { get; }

        public ComposerViewModel Composer => Workspace.Composer;

        public ComposerAttachmentTests.RecordingNotifications Toasts { get; }

        public Window Window { get; }

        public string Directory { get; }

        public TextBox Input => ById<TextBox>("Input");

        public IClipboard Clipboard => Window.Clipboard ?? throw new InvalidOperationException("no clipboard in the headless window");

        /// <summary>Only the attachment refusals; the workspace's own toasts (an unconfigured provider, say) are not under test.</summary>
        public IEnumerable<(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key)> Refusals =>
            Toasts.Calls.Where(static call => call.Key?.StartsWith("attachment-refused", StringComparison.Ordinal) == true);

        public static Rig Create(string root, string? model)
        {
            var services = ComposerSuggestionTests.TestServices.Create(root);
            var models = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                [TextOnly] = Entry(ModelInputs.Text),
                [ImageModel] = Entry(ModelInputs.Text | ModelInputs.Image),
                [FullModel] = Entry(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf | ModelInputs.Audio | ModelInputs.Video),
            };
            services.Services.ModelCatalog.Apply(new ModelCatalogSnapshot(
                "https://models.dev/api.json",
                "\"v1\"",
                DateTimeOffset.UtcNow,
                new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)
                {
                    ["anthropic"] = models,
                }));

            var directory = Path.Combine(root, "files");
            System.IO.Directory.CreateDirectory(directory);
            var toasts = new ComposerAttachmentTests.RecordingNotifications();
            var info = new SessionInfo(SessionId, root, "attach", At);
            var store = new EmptySessionStore(At);
            var workspace = new SessionWorkspaceViewModel(
                info,
                new SessionRuntime(
                    new AgentRuntimeServices(
                        static _ => new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText()),
                        HostClient: null,
                        store,
                        new EmptySessionCatalog(At),
                        new EmptyTodoStore("todo-1", At),
                        new ModelSettings("model")),
                    info),
                checkpoints: null,
                store,
                services.Services,
                composerPreferences: new ComposerPreferences(),
                fileSource: static _ => Task.FromResult<IReadOnlyList<string>>([]),
                skillSource: static () => [],
                notifications: toasts);
            if (model is not null)
            {
                workspace.Composer.Model = model;
            }

            var window = new Window { Width = 1200, Height = 800, Content = new SessionView { DataContext = workspace } };
            window.Show();
            var rig = new Rig(services, workspace, toasts, window, directory);
            rig.Settle();
            return rig;
        }

        private static ModelCatalogModel Entry(ModelInputs inputs) => new(100_000, null, new ModelPricing(1, 2, 0, 0), inputs, null);

        public string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string Sparse(string name, long length)
        {
            var path = Path.Combine(Directory, name);
            using var stream = File.Create(path);
            stream.SetLength(length);
            return path;
        }

        /// <summary>
        /// A real 1x1 PNG. Decoding works in the headless platform but encoding does not (Bitmap.Save writes nothing), so the
        /// file is not produced from a bitmap.
        /// </summary>
        public string WritePng(string name) => Write(name, OnePixelPng);
        public void Drop(params string[] paths) => DropItems([.. paths.Select(static path => Storage.File(path))]);

        public void DropItems(params IStorageItem[] items)
        {
            var transfer = new DataTransfer();
            foreach (var item in items)
            {
                transfer.Add(DataTransferItem.CreateFile(item));
            }

            Raise(DragDrop.DropEvent, transfer, DragDropEffects.Copy);
        }

        public DragEventArgs DragOver(DragDropEffects offered, IDataTransfer transfer) =>
            Raise(DragDrop.DragOverEvent, transfer, offered);

        public DragEventArgs Raise(RoutedEvent<DragEventArgs> routedEvent, IDataTransfer transfer, DragDropEffects effects)
        {
            var card = ById<Border>("ComposerCard");
            var args = new DragEventArgs(routedEvent, transfer, card, new Point(10, 10), KeyModifiers.None)
            {
                DragEffects = effects,
            };
            card.RaiseEvent(args);
            return args;
        }

        public T ById<T>(string id) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => IsId(control, id))
            ?? throw new InvalidOperationException("no " + typeof(T).Name + " with id or name '" + id + "'");

        public List<T> AllById<T>(string id) where T : Control =>
            [.. Window.GetVisualDescendants().OfType<T>().Where(control => IsId(control, id) && control.IsVisible)];

        /// <summary>A real left click at the middle of <paramref name="button"/> (press and release through the window).</summary>
        public void Click(Button button)
        {
            Settle();
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)
                         ?? throw new InvalidOperationException("the button is not in the window");
            Window.MouseDown(center, MouseButton.Left);
            Window.MouseUp(center, MouseButton.Left);
            Settle();
        }

        public string ErrorLine() => ById<TextBlock>("composer-attachment-error").Text ?? string.Empty;

        private static bool IsId(Control control, string id) =>
            string.Equals(AutomationProperties.GetAutomationId(control), id, StringComparison.Ordinal) ||
            string.Equals(control.Name, id, StringComparison.Ordinal);

        /// <summary>Lets any in-flight async-void handler finish its work (there is nothing to poll when it adds nothing).</summary>
        public async Task QuietAsync()
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(10, CancellationToken.None);
                Dispatcher.UIThread.RunJobs();
            }

            Settle();
        }

        public void Settle()
        {
            for (var pass = 0; pass < 2; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            Workspace.Dispose();
            services.Dispose();
        }
    }

    /// <summary>
    /// What a drop or the clipboard hands the view: the framework's own file-backed storage items (the class is internal to
    /// Avalonia and the storage interfaces cannot be implemented outside it, so they are built by reflection).
    /// </summary>
    private static class Storage
    {
        private static readonly Type FileType = typeof(IStorageItem).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile")!;
        private static readonly Type FolderType = typeof(IStorageItem).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFolder")!;

        public static IStorageFile File(string path) =>
            (IStorageFile)Activator.CreateInstance(FileType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [new FileInfo(path)], null)!;

        public static IStorageItem Folder(string path) =>
            (IStorageItem)Activator.CreateInstance(FolderType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [new DirectoryInfo(path)], null)!;
    }
}
