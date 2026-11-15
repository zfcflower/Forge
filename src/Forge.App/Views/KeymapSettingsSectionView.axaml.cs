using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.Settings;

namespace Forge.App.Views;

/// <summary>
/// The <c>keymap</c> section's view: three scope groups of rebindable actions and the capture surface that turns a key press
/// into a normalized gesture. The code-behind only owns what needs an input element — starting a capture focuses the surface,
/// and a key press over that surface feeds the section view model.
/// </summary>
internal sealed partial class KeymapSettingsSectionView : UserControl
{
    public KeymapSettingsSectionView() => InitializeComponent();

    private void OnRebindClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KeymapSettingsSectionViewModel section || sender is not Button { DataContext: KeymapRowViewModel row })
        {
            return;
        }

        section.BeginRebind(row);
        CaptureSurface.Focus();
    }

    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KeymapSettingsSectionViewModel section)
        {
            return;
        }

        section.CaptureKey(e.Key, e.KeyModifiers);
        e.Handled = true;
    }
}
