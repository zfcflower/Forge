using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Forge.App.Changes;

/// <summary>
/// The side-by-side view of one image change (wave W17a), hosted by the diff editor's embed row through
/// <c>DiffEditorView.ImageDiffFactory</c>. It loads its two pictures when it is first attached, so scrolling past an image
/// file costs nothing, and it keeps them while the diff editor keeps the row realized.
/// </summary>
internal sealed partial class ImageDiffView : UserControl
{
    private readonly ImageDiffViewModel viewModel;
    private bool loaded;

    public ImageDiffView(IDiffFileItem file, Func<byte[], Bitmap?>? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        viewModel = new ImageDiffViewModel(file, decoder);
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>The view model behind the two pictures, for tests and for the host's height measurement.</summary>
    internal ImageDiffViewModel ViewModel => viewModel;

    /// <summary>
    /// Raised once after the asynchronous load settles. The host re-measures on it: the height measured before the pictures
    /// arrived is an empty-box height, and without this signal a row that decoded late keeps it until the row is rebuilt.
    /// </summary>
    internal event EventHandler? Decoded;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (loaded)
        {
            return;
        }

        loaded = true;
        _ = LoadAndNotifyAsync();
    }

    private async Task LoadAndNotifyAsync()
    {
        try
        {
            await viewModel.LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            Decoded?.Invoke(this, EventArgs.Empty);
        }
    }
}