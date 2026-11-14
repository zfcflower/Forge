using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The resource inspector's overlay (wave W8). Only what needs the visual tree lives here: the keyboard lands on the close
/// button so Escape works the moment the panel opens, a click on the scrim (and only on it) closes the panel, and Escape does
/// the same. Everything the rows show and do is bound.
/// </summary>
public partial class ResourceInspectorView : UserControl
{
    private ResourceInspectorViewModel? model;

    public ResourceInspectorView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as ResourceInspectorViewModel);
        CloseButton?.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Bind(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Bind(DataContext as ResourceInspectorViewModel);
    }

    private void Bind(ResourceInspectorViewModel? viewModel) => model = viewModel;

    /// <summary>A click that started on the scrim itself closes the panel; one inside the card does not.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Scrim))
        {
            return;
        }

        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void Close()
    {
        if (model?.CloseCommand.CanExecute(null) == true)
        {
            model.CloseCommand.Execute(null);
        }
    }
}