using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The new-branch dialog: owns nothing but the window. It starts the view model's one load when it opens, turns the two
/// footer buttons into the dialog result (<see cref="NewBranchChoice"/>, or null for cancel/Esc/close) and disposes the
/// view model when it closes.
/// </summary>
internal partial class NewBranchDialog : Window
{
    private NewBranchDialogViewModel? model;

    public NewBranchDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        model = DataContext as NewBranchDialogViewModel;
        if (model is not null)
        {
            _ = model.InitializeAsync();
        }

        // The prefilled name is what the user usually edits, so the box takes the focus with the whole text selected:
        // typing replaces it and Enter accepts it.
        BranchNameBox.Focus();
        BranchNameBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        model?.Dispose();
        model = null;
        base.OnClosed(e);
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (model?.Result is { } choice)
        {
            Close(choice);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    // Button raises Click before it runs its Command; hiding the flyout on the dispatcher lets the pick land first.
    private void OnBranchPicked(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => BasePicker.Flyout?.Hide(), DispatcherPriority.Input);

    /// <summary>
    /// Popups are drawn in the window's overlay layer (Program.cs), so this flyout cannot extend past the dialog: the branch
    /// list is capped to the space left under the picker (or above it, where the flyout flips), and scrolls instead of being
    /// cut off by a short window. The card's header, search box and padding take about 72 px above the list.
    /// </summary>
    private void OnBasePickerOpening(object? sender, EventArgs e)
    {
        if (BasePicker.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }

        var below = Bounds.Height - (origin.Y + BasePicker.Bounds.Height) - 8;
        var above = origin.Y - 8;
        if (BasePicker.Flyout is Flyout flyout)
        {
            // Pick the side with room explicitly: the overlay host is clipped by the window, so a menu placed on the short
            // side would be cut off no matter how small its content is.
            flyout.Placement = below >= above ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.TopEdgeAlignedLeft;
        }

        BasePickerView.MaximumListHeight = Math.Clamp(Math.Max(below, above) - 72, 64, 360);
    }
}
