using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W10b's composition module: the <c>updates</c> settings section (manifest URL, check frequency, channel, current
/// version and the check-now action). The id and order are already allocated in <see cref="SettingsSectionIds"/>; the
/// preferences (<c>UpdatesManifestUrl</c>, <c>UpdatesCheckFrequency</c>, <c>UpdatesChannel</c>) are already on
/// <see cref="AppPreferences"/>. W10b fills this in following <c>InspectorComposition</c>'s shape: register the section
/// view model + <c>SectionViewTemplate</c>, nothing else. The update service itself is built in the composition root.
/// </summary>
internal static class UpdatesComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);
    }
}