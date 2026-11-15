using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The <c>git</c> settings section's view (wave W17b): the rows of the app-level template plus the review mode and the
/// automatic review switch. The code-behind only loads the XAML - every row edits its preference through the section view
/// model - and it is registered through <see cref="SectionViewTemplate"/> from the composition root, the way the sections of
/// the later waves are, so the shared <c>App.axaml</c> does not have to be edited for one more git preference.
/// </summary>
internal sealed partial class GitSettingsSectionView : UserControl
{
    public GitSettingsSectionView() => InitializeComponent();
}
