using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The composer's model and orchestration chips. The <c>Flyout</c> elements in the XAML only hold each picker's content:
/// it is moved into a <see cref="PickerPopup"/> that lives in the overlay layer, which opens several times faster than a flyout
/// that re-attaches its content on every click.
/// </summary>
public partial class ComposerToolbar : UserControl
{
    private readonly PickerPopup model;
    private readonly PickerPopup orchestration;

    public ComposerToolbar()
    {
        InitializeComponent();
        model = Take("ModelChip");
        orchestration = Take("OrchestrationChip");
    }

    /// <summary>The picker behind a chip, for the benchmark to open without a pointer.</summary>
    internal PickerPopup Picker(string chipName) => chipName switch
    {
        "ModelChip" => model,
        _ => orchestration,
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        model.SetDataContext(DataContext);
        orchestration.SetDataContext(DataContext);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Build the pickers once the window has drawn its first frame, not in the middle of creating the session.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    model.Warm();
                    orchestration.Warm();
                }
            },
            DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        model.Detach();
        orchestration.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private PickerPopup Take(string chipName)
    {
        var chip = this.FindControl<Button>(chipName)!;
        var flyout = (Flyout)chip.Flyout!;
        var content = (Control)flyout.Content!;
        flyout.Content = null;
        chip.Flyout = null;
        return new PickerPopup(chip, content);
    }

    private void OnModelPicked(object? sender, RoutedEventArgs e) => model.Close(restoreFocus: true);

    /// <summary>
    /// Opens the session's turn ledger window: the rows are read off the UI thread (the composer holds only the source),
    /// then the dialog is shown over this view's window. A source that failed reads as an empty timeline rather than an
    /// error dialog - the window is an observation, not an operation.
    /// </summary>
    private async void OnSessionLedgerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposerViewModel { SessionLedger: { } read } composer)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return;
        }

        try
        {
            var rows = await Task.Run(() => read(CancellationToken.None)).ConfigureAwait(true);
            await TurnLedgerWindow.ShowAsync(owner, rows).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The store is best effort here as everywhere: a failed read costs the window, not the composer.
            System.Diagnostics.Trace.TraceWarning($"session ledger read failed: {exception.Message}");
        }
    }

    private void OnOrchestrationPicked(object? sender, RoutedEventArgs e) => orchestration.Close(restoreFocus: true);

    /// <summary>
    /// The pointer let go of the reasoning dial. The dial reports a continuous thumb position while it is dragged, so the thumb
    /// is pulled onto the step it landed on here instead of jumping there mid-drag.
    /// </summary>
    private void OnEffortReleased(object? sender, RoutedEventArgs e) =>
        (DataContext as ComposerViewModel)?.SnapEffort();
}
