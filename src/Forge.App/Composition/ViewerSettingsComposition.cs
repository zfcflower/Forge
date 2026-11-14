using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.ViewModels;
using Forge.App.ViewModels.Preview;
using Forge.App.Views;
using Forge.App.Views.Settings;

namespace Forge.App.Composition;

/// <summary>
/// Wave W4's composition module: the chat (tool images) and files (viewer limits) sections, plus the two live projections
/// those features read. <see cref="FeatureComposition.RegisterAll"/> already calls it, so the wave never edits
/// <c>AppComposition.cs</c>. Section ids and orders are in <see cref="SettingsSectionIds"/>.
/// </summary>
internal static class ViewerSettingsComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        var preferences = composition.Preferences;

        // A file document's limits and a tool row's thumbnail edge are projections of the live preferences, like the editor
        // options: both are read when a document or a row is built, so a change in the settings page applies to the next one.
        // Bound here, before the first window opens; a host without preferences keeps the built-in defaults.
        FilePreviewSettings.Current = new FilePreviewSettings(preferences);
        ToolImageSettings.ThumbnailEdge = () => preferences.ChatToolImageThumbnailPixels;

        // The section bodies come from this module's own views: SectionViewTemplate is the one data template App.axaml keeps
        // for the waves' settings sections, so registering the factory here is what replaced editing that frozen file.
        SectionViewTemplate.Register<ChatSettingsSectionViewModel>(static () => new ChatSettingsSectionView());
        SectionViewTemplate.Register<FilesSettingsSectionViewModel>(static () => new FilesSettingsSectionView());

        // The chat section: how large a tool result's images render and whether the model receives them.
        settings.Add(new SettingsSection(
            SettingsSectionIds.Chat,
            ChatStrings.SettingsTitle,
            SettingsSectionIds.ChatOrder,
            new ChatSettingsSectionViewModel(preferences),
            SettingsGroups.Interface,
            "IconChat"));

        // The files section: what the rich viewer parses (CSV rows, notebook output, image size limit).
        settings.Add(new SettingsSection(
            SettingsSectionIds.Files,
            PreviewStrings.SectionTitleKey,
            SettingsSectionIds.FilesOrder,
            new FilesSettingsSectionViewModel(preferences),
            SettingsGroups.Interface,
            "IconEye"));
    }
}
