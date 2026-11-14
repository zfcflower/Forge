using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W3's composition module: the <c>palette</c> section (results per source, the directories the content search skips).
/// The section view is registered through <see cref="SectionViewTemplate"/> rather than a template in <c>App.axaml</c>, so
/// this wave owns every file it touches. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class PaletteComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Add(new SettingsSection(
            SettingsSectionIds.Palette,
            PaletteSettingsSectionViewModel.SectionTitle,
            SettingsSectionIds.PaletteOrder,
            new PaletteSettingsSectionViewModel(composition.Preferences),
            SettingsGroups.Interface,
            "IconSearch"));
        SectionViewTemplate.Register<PaletteSettingsSectionViewModel>(static () => new PaletteSettingsSectionView());
    }
}
