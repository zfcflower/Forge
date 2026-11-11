using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Git;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The 打开 PR dialog: it turns the two buttons into the dialog result (<see cref="PullRequestDraft"/>, or null for
/// cancel/Esc/close) and keeps no state of its own.
/// </summary>
internal partial class PullRequestDialog : Window
{
    public PullRequestDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The title is what the user usually edits (it arrives prefilled), so it takes the focus selected.
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PullRequestDialogViewModel model && model.CanCreate)
        {
            Close(model.ToDraft());
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
