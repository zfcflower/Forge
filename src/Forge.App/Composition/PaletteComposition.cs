using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W3's composition module: the palette section and its search sources. Empty until the wave fills it in; <see cref="FeatureComposition.RegisterAll"/>
/// already calls it, so the wave never edits <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class PaletteComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
    }
}
