using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W4's composition module: the chat (tool images) and files (viewer limits) sections. Empty until the wave fills it in; <see cref="FeatureComposition.RegisterAll"/>
/// already calls it, so the wave never edits <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class ViewerSettingsComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
    }
}
