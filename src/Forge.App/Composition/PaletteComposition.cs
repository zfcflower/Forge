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
        RegisterSection(composition.Preferences, settings);
    }

    /// <summary>
    /// The section itself, without the composition root: the entry point the module calls and the seam a test uses, so the
    /// registration and its view template are provable without building the whole graph.
    /// </summary>
    internal static void RegisterSection(AppPreferences preferences, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Add(new SettingsSection(
            SettingsSectionIds.Palette,
            PaletteSettingsSectionViewModel.SectionTitle,
            SettingsSectionIds.PaletteOrder,
            new PaletteSettingsSectionViewModel(preferences),
            SettingsGroups.Interface,
            "IconSearch"));
        SectionViewTemplate.Register<PaletteSettingsSectionViewModel>(static () => new PaletteSettingsSectionView());
    }
}
