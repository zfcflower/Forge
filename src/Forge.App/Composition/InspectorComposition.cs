using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W8's composition module: the <c>inspector</c> settings section and its view. It registers only this one section;
/// the inspector itself (the overlay's view model) lives in <c>ViewModels/ResourceInspectorViewModel.cs</c> and is not
/// built here. <see cref="FeatureComposition.RegisterAll"/> already calls it, so the wave never edits
/// <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// <para>The two values the section writes are read where they are used — the tail length whenever the inspector refreshes
/// its output view, the refresh interval when it next builds its timer — so nothing here caches one.</para>
/// </summary>
internal static class InspectorComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        SectionViewTemplate.Register<ResourceInspectorSettingsViewModel>(static () => new ResourceInspectorSettingsSectionView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.Inspector,
            ResourceInspectorStrings.SettingsTitleKey,
            SettingsSectionIds.InspectorOrder,
            new ResourceInspectorSettingsViewModel(composition.Preferences),
            SettingsGroups.Interface,
            "IconLayers"));
    }
}
