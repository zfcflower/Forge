using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaloniaEdit.Editing;
using Forge.App.Editing;
using Forge.App.Review;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class FileDocumentView : UserControl
{
    private FileDocumentViewModel? model;
    private ForgeEditorBridge? bridge;
    private ReviewOverlay? review;
    private bool attached;

    public FileDocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        InitEditorLsp();
        review = new ReviewOverlay(Editor, new ReviewOverlayParts
        {
            Layer = ReviewLayer,
            Icon = ReviewIconButton,
            Composer = ReviewComposer,
            RangeText = ReviewRangeText,
            BodyBox = ReviewBodyBox,
            ComposerStatus = ReviewComposerStatus,
            SaveButton = ReviewSaveButton,
            SendButton = ReviewSendButton,
            DeleteButton = ReviewDeleteButton,
            CancelButton = ReviewCancelButton,
            SendAllButton = ReviewSendAll,
            StatusText = ReviewStatus,
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Editor.ActualThemeVariantChanged += OnThemeChanged;
        Connect();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Editor.ActualThemeVariantChanged -= OnThemeChanged;
        Disconnect();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (attached)
        {
            Connect();
        }
    }

    private void Connect()
    {
        Disconnect();
        model = DataContext as FileDocumentViewModel;
        if (model is null)
        {
            return;
        }

        model.PropertyChanged += OnModelPropertyChanged;
        review?.Bind(model.ReviewController);
        Rebind();
    }

    private void Disconnect()
    {
        DetachEditorLsp();
        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model = null;
        }

        review?.Bind(null);
        bridge?.Dispose();
        bridge = null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileDocumentViewModel.EditorDocument))
        {
            Rebind();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => bridge?.ApplyTheme();

    private void Rebind()
    {
        DetachEditorLsp();
        bridge?.Dispose();
        bridge = null;
        if (model?.EditorMirror is not { } mirror)
        {
            review?.Refresh();
            return;
        }

        bridge = new ForgeEditorBridge(Editor, model, mirror, model.Path);
        AttachEditorLsp(model);
        review?.Refresh();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var copy = e.Key == Key.C && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta
            || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Control;
        if (!copy || Editor.TextArea.Selection is not SimpleSelection { IsEmpty: false })
        {
            return;
        }

        e.Handled = true;
        _ = CopyAsync();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (DataContext is not FileDocumentViewModel model)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            model.ReportCopyError("clipboard unavailable");
            return;
        }

        var text = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Document?.Text ?? string.Empty;
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            model.ReportCopyError(string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine("[file] copy failed: " + exception.Message);
            model.ReportCopyError(exception.Message);
        }
    }

}
