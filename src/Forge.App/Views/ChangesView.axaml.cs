using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Forge.App.Changes;
using Forge.App.Input;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

public partial class ChangesView : UserControl
{
    private ChangesViewModel? viewModel;
    private bool syncingFromScroll;

    public ChangesView()
    {
        InitializeComponent();
        DiffEditor.TopFileChanged += OnTopFileChanged;
        DiffEditor.MarkdownPreviewFactory = static file => new Changes.Markdown.MarkdownDiffView(file);
        DiffEditor.ImageDiffFactory = static file => new Changes.ImageDiffView(file);
        AddHandler(InputElement.KeyDownEvent, OnChangesKeyDown);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        KeymapApplier.Changed += OnKeymapChanged;
        ApplyKeymap();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        KeymapApplier.Changed -= OnKeymapChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeymapChanged() => ApplyKeymap();

    /// <summary>Applies the next-unviewed gesture from the catalog and refreshes the footer hint.</summary>
    private void ApplyKeymap() =>
        NextUnviewedGestureHint.Text = KeymapApplier.DisplayGesture(KeymapActionIds.NextUnviewedChange);

    private void OnChangesKeyDown(object? sender, KeyEventArgs e)
    {
        if (viewModel is null || !KeymapApplier.Gesture(KeymapActionIds.NextUnviewedChange).Matches(e))
        {
            return;
        }

        e.Handled = true;
        viewModel.NextUnviewedCommand?.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (viewModel is not null)
        {
            viewModel.FocusRequested -= OnFocusRequested;
            viewModel.CopyPathRequested -= OnCopyPathRequested;
        }

        viewModel = DataContext as ChangesViewModel;
        if (viewModel is not null)
        {
            viewModel.FocusRequested += OnFocusRequested;
            viewModel.CopyPathRequested += OnCopyPathRequested;
        }
    }

    private void OnFocusRequested(FileDiffViewModel file) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!syncingFromScroll)
                {
                    DiffEditor.ScrollToFile(file);
                }
            },
            DispatcherPriority.Background);

    private void OnTopFileChanged(IDiffFileItem file)
    {
        if (viewModel is null)
        {
            return;
        }

        // Scrolling moves the selection; the selection change must not scroll back.
        syncingFromScroll = true;
        try
        {
            viewModel.SyncSelectionFromScroll(file);
        }
        finally
        {
            syncingFromScroll = false;
        }
    }

    private async void OnCopyPathRequested(string path)
    {
        if (viewModel is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            viewModel.ReportCopyPathFailure("clipboard unavailable");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            AppLogs.Changes.ZLogWarning($"copy path failed: {exception.Message}");
            viewModel.ReportCopyPathFailure(exception.Message);
        }
    }
}
