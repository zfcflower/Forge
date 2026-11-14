using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The <c>idle-archive</c> section's view: one switch, one numeric window and one scope picker, all writing the live
/// preferences themselves.
/// </summary>
internal sealed partial class IdleArchiveSettingsSectionView : UserControl
{
    public IdleArchiveSettingsSectionView() => InitializeComponent();
}
