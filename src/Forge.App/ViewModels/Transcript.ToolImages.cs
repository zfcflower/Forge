using Forge.Abstractions;
using Forge.App.Preferences;

namespace Forge.App.ViewModels;

/// <summary>
/// The live preference a tool-row image reads, ambient like <c>EditorSettings.Current</c>: transcript rows are built by
/// a data template deep inside a session and cannot be handed the composition root, so the composition root replaces
/// <see cref="ThumbnailEdge"/> with a read of <see cref="AppPreferences.ChatToolImageThumbnailPixels"/> before the first
/// window opens. Tests and any host without preferences keep the built-in default.
/// </summary>
internal static class ToolImageSettings
{
    /// <summary>Longest edge a tool-row thumbnail is decoded to, in device-independent pixels.</summary>
    internal static Func<int> ThumbnailEdge { get; set; } = static () => AppPreferences.DefaultChatToolImageThumbnailPixels;
}

/// <summary>
/// One image a tool returned, shown under the row's result text. The stored bytes are never re-encoded: the thumbnail
/// only bounds how large a decode the row asks for, lazily and off the UI thread, like every transcript image.
/// </summary>
internal sealed class ToolImageViewModel : ImageThumbnailViewModel
{
    public ToolImageViewModel(ImageBlock block, int maxLongEdge)
        : base(block, ContentPlaceholders.DecodedLength(block.Data), thumbnail: null, maxLongEdge)
    {
        ThumbnailEdge = maxLongEdge;
    }

    /// <summary>Longest edge the row shows the image at, in device-independent pixels; the item template binds <c>MaxWidth</c>/<c>MaxHeight</c> to it.</summary>
    public int ThumbnailEdge { get; }
}

/// <summary>The picture half of <see cref="ToolRowViewModel"/>: the images a finished result carried.</summary>
internal sealed partial class ToolRowViewModel
{
    private IReadOnlyList<ToolImageViewModel> images = [];

    /// <summary>The images the finished tool result carried, in order; empty for a text-only tool.</summary>
    public IReadOnlyList<ToolImageViewModel> Images => images;

    public bool HasImages => images.Count > 0;

    /// <summary>
    /// Takes the image blocks of a finished result into the row. The transcript always shows what the tool produced:
    /// <see cref="AppPreferences.ChatToolImagesToModel"/> only governs what the model receives, never what this row displays.
    /// The list is replaced (and notified) because the row is already on screen when the result lands.
    /// </summary>
    partial void CaptureImages(IReadOnlyList<ContentBlock> blocks)
    {
        List<ToolImageViewModel>? found = null;
        var edge = ToolImageSettings.ThumbnailEdge();
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is ImageBlock image)
            {
                (found ??= []).Add(new ToolImageViewModel(image, edge));
            }
        }

        if (found is null)
        {
            return;
        }

        images = found;
        OnPropertyChanged(nameof(Images));
        OnPropertyChanged(nameof(HasImages));
    }
}
