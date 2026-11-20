using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Logging;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Views;

public partial class FilesToolView : UserControl
{
    public FilesToolView()
    {
        InitializeComponent();
        // The row under the pointer reaches the menu through the panel: the menu itself belongs to the list, so the
        // rebuild that a watcher tick performs between the right-click and the menu opening cannot lose it. The press is
        // handled on the way down, before the attached menu's own handler on this list opens it.
        TreeList.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Remembers which file the menu is about, from the press that opens it (or clears it on a normal click).</summary>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FilesPanelViewModel viewModel)
        {
            return;
        }

        viewModel.ContextRow = e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
            ? RowAt(e.Source as Visual)
            : null;
    }

    /// <summary>The menu belongs to every row: only "open in new tab" is a file's item (see the XAML).</summary>
    private void OnTreeContextOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not FilesPanelViewModel viewModel)
        {
            e.Cancel = true;
            return;
        }

        // The keyboard path (Shift+F10) has no press: the selected row stands in for it.
        viewModel.ContextRow ??= TreeList.SelectedItem as FileNodeViewModel;
        if (viewModel.ContextRow is null)
        {
            e.Cancel = true;
        }
    }

    private static FileNodeViewModel? RowAt(Visual? source)
    {
        for (var node = source; node is not null; node = node.GetVisualParent())
        {
            if (node is Control { DataContext: FileNodeViewModel row })
            {
                return row;
            }
        }

        return null;
    }

    private void OnCopyFullPathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel { ContextRow: { } row } viewModel)
        {
            Copy(viewModel.FullPathOf(row));
        }
    }

    private void OnCopyRelativePathClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FilesPanelViewModel { ContextRow: { } row })
        {
            Copy(row.RelativePath);
        }
    }

    private void Copy(string text)
    {
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = CopyAsync(clipboard, text);
        }
    }

    private static async Task CopyAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"copy failed: {exception.Message}");
        }
    }

    // Button raises Click before it runs its Command. Hiding the flyout synchronously detaches the row and clears its bindings
    // (the item's DataContext and so its Command), so the pick was lost; the flyout closes once the command has run.
    private void OnScopePicked(object? sender, RoutedEventArgs e) => HideAfterCommand(ScopePicker);

    /// <summary>Fills in the lazily read parts of the picker (uncommitted count, turn file counts, commits) when it opens.</summary>
    private void OnScopeFlyoutOpening(object? sender, EventArgs e)
    {
        if (DataContext is FilesPanelViewModel { ChangeReview: { } review })
        {
            _ = review.RefreshScopePickerAsync();
        }
    }

    private void OnBranchPicked(object? sender, EventArgs e) => HideAfterCommand(CompareBranchPicker);

    private static void HideAfterCommand(Button picker) =>
        Dispatcher.UIThread.Post(() => picker.Flyout?.Hide(), DispatcherPriority.Input);

    private void OnChangedSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not FilesPanelViewModel viewModel || sender is not ListBox list)
        {
            return;
        }

        switch (list.SelectedItem)
        {
            case FileDiffViewModel diff:
                viewModel.OpenChanged(diff);
                break;
            case ChangedFileViewModel change:
                viewModel.OpenChanged(change);
                break;
        }
    }
}
