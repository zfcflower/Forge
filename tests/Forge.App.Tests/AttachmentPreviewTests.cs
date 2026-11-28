using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A sent message's attachment opens on click: an image or a text file in FORGE's own preview, a PDF, an audio track or a
/// video in the system's application — the only reader that knows those. Both come out of the same step, because a message
/// carries the bytes inline and a preview opens files: the attachment becomes one under the temp folder first.
/// </summary>
[Collection("forge-app-ui")]
public sealed class AttachmentPreviewTests
{
    /// <summary>A one-pixel PNG: enough for the classifier and the writer, small enough to inline.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>Collects where a click sent the file: FORGE's editor entry, or the system's own opener.</summary>
    private sealed class Openers : IDisposable
    {
        private readonly Action<string>? editor;
        private readonly Action<string>? system;

        internal Openers()
        {
            editor = EditorFileOpener.Handler;
            system = SystemFileOpener.Handler;
            EditorFileOpener.Handler = path => Edited = path;
            SystemFileOpener.Handler = path => Opened = path;
        }

        internal string? Edited { get; private set; }

        internal string? Opened { get; private set; }

        public void Dispose()
        {
            EditorFileOpener.Handler = editor;
            SystemFileOpener.Handler = system;
        }
    }

    [Fact]
    public async Task An_image_attachment_opens_in_forges_preview_as_a_png_file()
    {
        using var openers = new Openers();
        var image = new ImageBlock("image/png", Convert.ToBase64String(Png));
        var tile = new TranscriptImageViewModel(image);

        await AttachmentOpener.OpenAsync(tile);

        Assert.Null(openers.Opened);
        var path = Assert.IsType<string>(openers.Edited);
        // The preview opens files and classifies them by extension, so the picture has to be one.
        Assert.EndsWith(".png", path, StringComparison.Ordinal);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_text_attachment_opens_in_the_editor_under_its_own_name()
    {
        using var openers = new Openers();
        var block = TextAttachment.Create("notes.md", "# the attached heading");
        var chip = new TranscriptFileViewModel(block, "notes.md", AttachmentKind.Text, "text/plain", 20);

        await AttachmentOpener.OpenAsync(chip);

        Assert.Null(openers.Opened);
        var path = Assert.IsType<string>(openers.Edited);
        Assert.EndsWith("-notes.md", path, StringComparison.Ordinal);
        Assert.Contains("the attached heading", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_attachment_goes_to_the_systems_own_application()
    {
        using var openers = new Openers();
        var block = new FileBlock("application/pdf", Convert.ToBase64String("%PDF-1.4 the report"u8), "quarterly.pdf");
        var chip = new TranscriptFileViewModel(block, "quarterly.pdf", AttachmentKind.Pdf, "application/pdf", 19);

        await AttachmentOpener.OpenAsync(chip);

        // FORGE renders no PDF, so the editor must not see one: the file goes to the OS, named as it came.
        Assert.Null(openers.Edited);
        var path = Assert.IsType<string>(openers.Opened);
        Assert.EndsWith("-quarterly.pdf", path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public Task A_sent_message_draws_its_attachments_as_tiles_that_open_them() => SharedUi.RunAsync(() =>
    {
        var image = new ImageBlock("image/png", Convert.ToBase64String(Png));
        var file = new FileBlock("application/pdf", Convert.ToBase64String("%PDF-1.4"u8), "report.pdf");
        var message = new UserMessageViewModel("have a look", DateTimeOffset.UnixEpoch, [image, file]);

        var window = new Window { Content = new ContentControl { Content = message }, Width = 640, Height = 480 };
        window.Show();
        SharedUi.Settle(window);

        var tiles = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("attachment-tile"))
            .ToArray();
        Assert.Equal(2, tiles.Length);
        // Each tile runs its own attachment's click, and names the action rather than echoing the file: the name is what a
        // screen reader announces for a control with no text of its own.
        Assert.Same(message.Images[0].OpenCommand, tiles[0].Command);
        Assert.Equal(AttachmentStrings.OpenImage, AutomationProperties.GetName(tiles[0]));
        Assert.Same(message.Files[0].OpenCommand, tiles[1].Command);
        Assert.Equal(AttachmentStrings.Open, AutomationProperties.GetName(tiles[1]));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task An_attachment_tile_is_bare_chrome_rather_than_a_default_button() => SharedUi.RunAsync(() =>
    {
        var image = new ImageBlock("image/png", Convert.ToBase64String(Png));
        var message = new UserMessageViewModel("have a look", DateTimeOffset.UnixEpoch, [image]);

        var window = new Window { Content = new ContentControl { Content = message }, Width = 640, Height = 480 };
        window.Show();
        SharedUi.Settle(window);

        // The tile's own border draws it, so the button around it must carry no chrome at all. This is the rules' scope
        // being real: they live in the app-global sheet, because these tiles are drawn from App.axaml's templates inside the
        // transcript list, where no view-scoped sheet applies. A default Fluent button would show up here as padding, a
        // border and a 32 px minimum height.
        var tile = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("attachment-tile"));
        Assert.Equal(default, tile.Padding);
        Assert.Equal(default, tile.BorderThickness);
        Assert.Equal(0d, tile.MinHeight);
        window.Close();
        return Task.CompletedTask;
    });
}
