using Avalonia.Media.Imaging;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Changes;

/// <summary>
/// The two pictures of one image change, side by side (wave W17a). It reads both sides' bytes only when the row is actually
/// shown, and decodes them through the file preview's own decode path
/// (<see cref="ImageFilePreviewViewModel.Build(byte[], string, long, Func{byte[], Bitmap?}?)"/>) on a pool thread, so a change
/// set full of screenshots decodes nothing until one of them is scrolled into view.
/// <para>A side without bytes (added, deleted, above the preview limit, unreadable) stays null and the view says so instead of
/// drawing an empty frame.</para>
/// </summary>
internal sealed class ImageDiffViewModel : ObservableObject
{
    private readonly IDiffFileItem file;
    private readonly Func<byte[], Bitmap?>? decoder;
    private ImageFilePreviewViewModel? before;
    private ImageFilePreviewViewModel? after;
    private bool loading;

    /// <param name="decoder">Overrides the real bitmap decoder (headless tests have no Skia); null uses the preview's.</param>
    public ImageDiffViewModel(IDiffFileItem file, Func<byte[], Bitmap?>? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        this.file = file;
        this.decoder = decoder;
    }

    /// <summary>The base version; null until loaded, and for a file that did not exist there.</summary>
    public ImageFilePreviewViewModel? Before
    {
        get => before;
        private set
        {
            if (Set(ref before, value))
            {
                OnPropertyChanged(nameof(HasBefore));
            }
        }
    }

    /// <summary>The new version; null until loaded, and for a deleted file.</summary>
    public ImageFilePreviewViewModel? After
    {
        get => after;
        private set
        {
            if (Set(ref after, value))
            {
                OnPropertyChanged(nameof(HasAfter));
            }
        }
    }

    /// <summary>Whether the side has a decoded picture to show. A side whose bytes could not be decoded (an LFS pointer,
    /// a corrupt file) reads as "nothing here" instead of an empty box with a 0×0 caption.</summary>
    public bool HasBefore => before?.Image is not null;

    public bool HasAfter => after?.Image is not null;

    /// <summary>Releases both decodes; the host calls it when the row leaves the screen (one native bitmap per side).</summary>
    public void Release()
    {
        before?.Release();
        after?.Release();
        Before = null;
        After = null;
    }

    public bool IsLoading
    {
        get => loading;
        private set => Set(ref loading, value);
    }

    /// <summary>Reads and decodes both sides once; a second call while the first is running does nothing.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (loading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var sides = await file.ReadImageSidesAsync(ct).ConfigureAwait(true);
            var name = System.IO.Path.GetFileName(file.Path);
            var (builtBefore, builtAfter) = await Task.Run(
                    () => (Before: Build(sides.Before, name), After: Build(sides.After, name)),
                    ct)
                .ConfigureAwait(true);
            Before = builtBefore;
            After = builtAfter;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private ImageFilePreviewViewModel? Build(byte[]? data, string name) =>
        data is null ? null : ImageFilePreviewViewModel.Build(data, name, data.LongLength, decoder);
}