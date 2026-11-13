using Forge.App.Preferences;

namespace Forge.App.Composition;

/// <summary>
/// Wave W9's composition module: the projects (clone, create) section and the first-run wizard services. Empty until the wave fills it in; <see cref="FeatureComposition.RegisterAll"/>
/// already calls it, so the wave never edits <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class ProjectsComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
    }
}
