using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Prompts;

namespace Forge.App.Settings.Prompts;

/// <summary>Builds the <c>prompts</c> settings section for the composition root.</summary>
internal static class PromptsSettingsSection
{
    /// <summary>
    /// The section in the AGENT group. <paramref name="workspace"/> is read on the UI thread whenever the list loads or an
    /// editor opens (the same source the skills section uses), null meaning no workspace is open.
    /// </summary>
    public static SettingsSection Create(PromptTemplateService service, Func<string?> workspace) =>
        new(
            SettingsSectionIds.Prompts,
            PromptsStrings.SectionTitleKey,
            SettingsSectionIds.PromptsOrder,
            new PromptsSettingsViewModel(service, workspace),
            SettingsGroups.Agent,
            "IconSlash");
}
