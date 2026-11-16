using Forge.App.Input;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W10e's composition module: the <c>keymap</c> settings section (search, rebind capture, conflict detection, reset),
/// its view, and the one runtime wiring the wave needs — binding <see cref="KeymapApplier"/> to the live preferences so a
/// rebind rebuilds the window key bindings, the editor menu gestures and the view key handlers immediately.
/// <see cref="FeatureComposition.RegisterAll"/> already calls this, so the wave never edits the settings page or
/// <c>App.axaml</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class KeymapComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        RegisterSection(composition.Preferences, settings);
    }

    /// <summary>
    /// The section, its view template and the applier wiring, without the composition root: the entry point the module calls,
    /// so the registration and the live application are reachable without building the whole graph.
    /// </summary>
    internal static void RegisterSection(AppPreferences preferences, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(settings);

        settings.Add(new SettingsSection(
            SettingsSectionIds.Keymap,
            KeymapStrings.SettingsTitleKey,
            SettingsSectionIds.KeymapOrder,
            new KeymapSettingsSectionViewModel(preferences),
            SettingsGroups.Interface,
            "IconSlash"));
        SectionViewTemplate.Register<KeymapSettingsSectionViewModel>(static () => new KeymapSettingsSectionView());

        KeymapApplier.Bind(preferences);
    }
}
