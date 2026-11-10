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
    public BranchPickerView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a branch row is clicked, after the row's selection command ran.</summary>
    public event EventHandler? BranchSelected;

    // Button raises Click before it runs its Command, so the flyout's owner hides on the dispatcher instead of here: hiding
    // synchronously would detach the row and clear its bindings (the item's DataContext and so its Command) mid-click.
    private void OnBranchPicked(object? sender, RoutedEventArgs e) => BranchSelected?.Invoke(this, EventArgs.Empty);
}
