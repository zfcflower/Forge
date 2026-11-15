using Avalonia.Controls;
using Avalonia.Input;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The pull request review-thread panel's view. It holds no state of its own — the panel is a view model with commands —
/// and does only the two things the view can do: render a comment body and close on Escape. Every other interaction is a
/// binding.
/// </summary>
public partial class PrCommentsView : UserControl
{
    public PrCommentsView() => InitializeComponent();

    /// <summary>Escape closes the panel, like the checks and plan panels.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || DataContext is not PrCommentsViewModel model)
        {
            return;
        }

        if (model.CloseCommand.CanExecute(null))
        {
            e.Handled = true;
            model.CloseCommand.Execute(null);
        }
    }
}