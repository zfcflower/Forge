using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W8's composition module: the resource inspector section and its feed. Empty until the wave fills it in; <see cref="FeatureComposition.RegisterAll"/>
/// already calls it, so the wave never edits <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class InspectorComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
    }
}
