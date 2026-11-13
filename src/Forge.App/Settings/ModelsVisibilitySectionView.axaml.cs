using Avalonia.Controls;

namespace Forge.App.Settings;

/// <summary>
/// The <c>models-visibility</c> section's view. It is a plain list with one switch per row; the switches write the
/// preferences themselves, so the view needs no code beyond the compiled bindings.
/// </summary>
internal sealed partial class ModelsVisibilitySectionView : UserControl
{
    public ModelsVisibilitySectionView() => InitializeComponent();
}
