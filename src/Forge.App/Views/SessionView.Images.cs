using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The composer's three ways in for attachments: pasting (Ctrl+V or the context menu) when the clipboard holds files or an
/// image, dropping files on the composer card, and the attach button's file picker. All three hand paths (or the pasted
/// image's bytes) to the composer view model, whose one classifier checks, gates and keeps them.
/// </summary>
public partial class SessionView
{
    /// <summary>Set while a paste this view decided is text is handed back to the text box, so it is not intercepted twice.</summary>
    private bool pastingText;

    private void AttachImageInput(TextBox input)
    {
        input.PastingFromClipboard += OnInputPasting;
        if (this.FindControl<Border>("ComposerCard") is { } card)
        {
            DragDrop.SetAllowDrop(card, true);
            card.AddHandler(DragDrop.DragOverEvent, OnComposerDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
            card.AddHandler(DragDrop.DropEvent, OnComposerDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private void OnInputPasting(object? sender, RoutedEventArgs e)
    {
        if (pastingText || sender is not TextBox input || viewModel is not { } session ||
            TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            pastingText = false;
            return;
        }

        // Whether the clipboard holds an image is only known asynchronously: hold the paste, look, and either attach the
        // images or hand the paste back to the text box.
        e.Handled = true;
        _ = PasteAsync(input, session.Composer, clipboard);
    }

    private async Task PasteAsync(TextBox input, ComposerViewModel composer, IClipboard clipboard)
    {
        var attached = false;
        try
        {
            if (await clipboard.TryGetFilesAsync().ConfigureAwait(true) is { } files)
            {
                var paths = new List<string>();
                foreach (var file in files)
                {
                    if (file is IStorageFile && file.TryGetLocalPath() is { } path)
                    {
                        paths.Add(path);
                    }
                }

                if (paths.Count > 0)
                {
                    attached = true;
                    await AttachFilesAsync(composer, paths).ConfigureAwait(true);
                }
            }

            if (!attached && await clipboard.TryGetBitmapAsync().ConfigureAwait(true) is { } bitmap)
            {
                attached = true;
                var bytes = await Task.Run(() => EncodePng(bitmap)).ConfigureAwait(true);
                await composer.AddImageAsync(bytes, ImageStrings.ClipboardName).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException or
                                              UnauthorizedAccessException)
        {
            attached = false;
        }

        if (!attached)
        {
            pastingText = true;
            try
            {
                input.Paste();
            }
            finally
            {
                pastingText = false;
            }
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using (bitmap)
        {
            using var output = new MemoryStream();
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
            return output.ToArray();
        }
    }

    private static void OnComposerDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects &= DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnComposerDrop(object? sender, DragEventArgs e)
    {
        IEnumerable<IStorageItem>? files;
        try
        {
            files = viewModel is null ? null : e.DataTransfer.TryGetFiles();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            files = null;
        }

        if (viewModel is not { } session || files is null)
        {
            return;
        }

        e.Handled = true;
        var paths = new List<string>();
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }

        await AttachFilesAsync(session.Composer, paths).ConfigureAwait(true);
    }

    private async void OnAttachImagesClick(object? sender, RoutedEventArgs e)
    {
        if (viewModel is not { } session || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;
        try
        {
            picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = AttachmentStrings.PickerTitle,
                AllowMultiple = true,
                FileTypeFilter = PickerFilters(),
            }).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or NotSupportedException or
                                              UnauthorizedAccessException)
        {
            return;
        }

        var paths = new List<string>(picked.Count);
        foreach (var file in picked)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }

        await AttachFilesAsync(session.Composer, paths).ConfigureAwait(true);
        this.FindControl<TextBox>("Input")?.Focus();
    }

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>The picker's filters: every supported file first (the default), then one per kind.</summary>
    private static List<FilePickerFileType> PickerFilters() =>
    [
        Filter(
            AttachmentStrings.AllTypes,
            [
                .. ImageExtensions, .. FileAttachmentRules.PdfExtensions, .. FileAttachmentRules.AudioExtensions,
                .. FileAttachmentRules.VideoExtensions, .. FileAttachmentRules.TextExtensions,
            ]),
        Filter(ImageStrings.FileType, ImageExtensions),
        Filter(AttachmentStrings.PdfType, FileAttachmentRules.PdfExtensions),
        Filter(AttachmentStrings.AudioType, FileAttachmentRules.AudioExtensions),
        Filter(AttachmentStrings.VideoType, FileAttachmentRules.VideoExtensions),
        Filter(AttachmentStrings.TextType, FileAttachmentRules.TextExtensions),
    ];

    private static FilePickerFileType Filter(string title, IEnumerable<string> extensions) =>
        new(title) { Patterns = [.. extensions.Select(static extension => "*" + extension)] };

    /// <summary>
    /// Attaches each file in turn through the composer's classifier; refusals show inline (and as a toast where the model or
    /// provider refuses). Stops at the per-message limit, since every further file would be refused for the same reason.
    /// </summary>
    private static async Task AttachFilesAsync(ComposerViewModel composer, IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (await composer.AddFileAsync(path).ConfigureAwait(true) is AttachmentRejection.TooMany)
            {
                break;
            }
        }
    }

    private void OnDismissAttachmentError(object? sender, RoutedEventArgs e) => viewModel?.Composer.DismissAttachmentError();
}
