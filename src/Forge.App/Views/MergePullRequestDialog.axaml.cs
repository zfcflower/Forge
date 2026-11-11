using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Git;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The 合并 PR dialog: it turns the two buttons into the answer (<see cref="PullRequestMergeChoice"/>, or null for
/// cancel/Esc/close) and keeps no state of its own. Nothing is merged here — the row calls the platform afterwards.
/// </summary>
internal partial class MergePullRequestDialog : Window
{
    public MergePullRequestDialog()
    {
        InitializeComponent();
    }

    private void OnMerge(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MergePullRequestDialogViewModel model)
        {
            Close(model.ToChoice());
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
