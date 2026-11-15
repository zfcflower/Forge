using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W10b's composition module: the <c>updates</c> settings section (manifest URL, check frequency, channel, current
/// version and the check-now action). The id and order are already allocated in <see cref="SettingsSectionIds"/>; the
/// preferences (<c>UpdatesManifestUrl</c>, <c>UpdatesCheckFrequency</c>, <c>UpdatesChannel</c>) are already on
/// <see cref="AppPreferences"/>. <see cref="FeatureComposition.RegisterAll"/> calls this, so the wave never edits
/// <c>App.axaml</c> or the settings page. The update service itself is built in <see cref="AppComposition"/> (it needs
/// the network stack and the UI-state store) and exposed as <see cref="AppComposition.Updates"/>; this module only wires
/// its section view model and view template.
/// </summary>
internal static class UpdatesComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        SectionViewTemplate.Register<UpdatesSettingsSectionViewModel>(static () => new UpdatesSettingsSectionView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.Updates,
            UpdatesStrings.SettingsTitleKey,
            SettingsSectionIds.UpdatesOrder,
            new UpdatesSettingsSectionViewModel(composition.Preferences, composition.Updates),
            SettingsGroups.System,
            "IconArrowUp"));
    }
}
