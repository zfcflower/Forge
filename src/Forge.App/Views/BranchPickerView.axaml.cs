using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Forge.App.Views;

/// <summary>
/// The shared branch menu: the changes panel's compare-base picker and the new-branch dialog's start-point picker both
/// host this control inside their own flyout. A pick runs the row's own command (the view model's selection); the owner
/// listens to <see cref="BranchSelected"/> to close its flyout.
/// </summary>
public partial class BranchPickerView : UserControl
{
    private double maximumListHeight = 360;

    public BranchPickerView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The cap of the branch list. The new-branch dialog lowers it to the window space left around its picker: popups are
    /// drawn in the window's overlay layer (Program.cs), so a flyout taller than the window is clipped, and the card has to
    /// scroll its list instead of running off the window.
    /// </summary>
    public double MaximumListHeight
    {
        get => maximumListHeight;
        set
        {
            maximumListHeight = value;
            ListScroll.MaxHeight = value;
        }
    }

    /// <summary>Raised when a branch row is clicked, after the row's selection command ran.</summary>
    public event EventHandler? BranchSelected;

    // Button raises Click before it runs its Command, so the flyout's owner hides on the dispatcher instead of here: hiding
    // synchronously would detach the row and clear its bindings (the item's DataContext and so its Command) mid-click.
    private void OnBranchPicked(object? sender, RoutedEventArgs e) => BranchSelected?.Invoke(this, EventArgs.Empty);
}
