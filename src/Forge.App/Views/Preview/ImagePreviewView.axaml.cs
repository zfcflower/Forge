using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Services;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Views.Preview;

/// <summary>
/// The image preview: the decoded bitmap in a zoom/pan view (centred, wheel zoom, drag, double click toggles fit/100 %) with the
/// zoom tools on the toolbar's right. The view only draws while attached: the view model owns the bitmap and disposes it when
/// the tab closes, so the view lets go of it as soon as it leaves the tree or the view model changes.
/// </summary>
public partial class ImagePreviewView : UserControl
{
    public ImagePreviewView()
    {
        InitializeComponent();
        Zoom.ZoomChanged += () => ZoomText.Text = (Zoom.Zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Zoom.Show(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Show();
    }

    private void Show()
    {
        var model = TopLevel.GetTopLevel(this) is null ? null : DataContext as ImageFilePreviewViewModel;
        Zoom.Show(model?.Image, model?.Vector);
        // Previous/next only for a picture of the reply that was last opened large.
        PreviousButton.IsVisible = NextButton.IsVisible = model is not null && PictureSet.Contains(model.Path);
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) => Step(-1);

    private void OnNext(object? sender, RoutedEventArgs e) => Step(1);

    private void Step(int step)
    {
        if (DataContext is ImageFilePreviewViewModel model && PictureSet.Step(model.Path, step) is { } path)
        {
            EditorFileOpener.Handler?.Invoke(path);
        }
    }

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Zoom.ZoomBy(1 / 1.25);

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Zoom.ZoomBy(1.25);

    private void OnActual(object? sender, RoutedEventArgs e) => Zoom.Actual();

    private void OnFit(object? sender, RoutedEventArgs e) => Zoom.Fit();
}
