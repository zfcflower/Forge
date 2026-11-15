using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W10e's composition module: the <c>keymap</c> settings section (search, rebind capture, conflict detection, reset).
/// The id and order are already allocated in <see cref="SettingsSectionIds"/>; the overrides live on
/// <see cref="AppPreferences.KeymapOverrides"/>. W10e fills this in following <c>InspectorComposition</c>'s shape. The
/// keymap catalog and applier themselves live in <c>Input/</c> and are wired where the window is built.
/// </summary>
internal static class KeymapComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);
    }
}