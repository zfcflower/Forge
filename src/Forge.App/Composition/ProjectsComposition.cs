using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.ViewModels;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W9's composition module: the <c>projects</c> settings section (where a clone or a new project goes, and how long a
/// clone may run). The add-project dialog and the first-run wizard need no registration here — the shell opens them from the
/// services <see cref="AppComposition"/> already owns. <see cref="FeatureComposition.RegisterAll"/> calls this, so the wave
/// never edits <c>AppComposition.cs</c>; the section's id and order come from <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class ProjectsComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        // The fallback directory is the same one the dialog starts in, so the section's "empty means this directory" line
        // and what the dialog then does cannot disagree.
        var section = new ProjectsSettingsSectionViewModel(
            composition.Preferences,
            ProjectCreationDefaults.ParentDirectoryOf(composition.Paths.GeneralWorkspaceDirectory));

        settings.Add(new SettingsSection(
            SettingsSectionIds.Projects,
            ProjectFlowStrings.SectionTitleKey,
            SettingsSectionIds.ProjectsOrder,
            section,
            SettingsGroups.Interface,
            "IconFolder"));

        SectionViewTemplate.Register<ProjectsSettingsSectionViewModel>(static () => new ProjectsSettingsSectionView());
    }
}
