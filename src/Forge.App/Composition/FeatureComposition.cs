using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// The one place the composition root hands over to the feature modules added after the core graph exists. Each module is a
/// file of its own wave, so no two waves edit the same line; this list is frozen.
/// </summary>
internal static class FeatureComposition
{
    public static void RegisterAll(AppComposition composition, SettingsSections settings)
    {
        RuntimeSettingsComposition.Register(composition, settings);
        PaletteComposition.Register(composition, settings);
        ViewerSettingsComposition.Register(composition, settings);
        InspectorComposition.Register(composition, settings);
        ProjectsComposition.Register(composition, settings);
    }
}
