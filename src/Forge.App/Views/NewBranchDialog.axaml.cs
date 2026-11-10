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
}
