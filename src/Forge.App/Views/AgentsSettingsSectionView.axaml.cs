using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The <c>agents</c> section's view: one numeric field for the sub-agent cap and one switch for spawning. Both write the
/// live preferences themselves, so the view needs no code beyond the compiled bindings.
/// </summary>
internal sealed partial class AgentsSettingsSectionView : UserControl
{
    public AgentsSettingsSectionView() => InitializeComponent();
}
