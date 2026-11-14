using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Views;

namespace Forge.App.Composition;

/// <summary>
/// Wave W1's composition module: the four sections this wave owns — <c>models-visibility</c>, <c>agents</c>,
/// <c>idle-archive</c> and <c>notification-permission</c> — plus the section views they need. Registering both here is what
/// lets the wave stay out of <c>AppComposition.cs</c> and <c>App.axaml</c>: the composition root calls
/// <see cref="FeatureComposition.RegisterAll"/> once, and each view is created by an explicit factory, which keeps the
/// template Native AOT safe (no name-based type lookup).
/// <para>The two values these sections write are read elsewhere through factories, so nothing here caches one: the sub-agent
/// dials reach a live host before every turn, and the archive settings are read once per housekeeping run.</para>
/// </summary>
internal static class RuntimeSettingsComposition
{
    public static void Register(AppComposition composition, SettingsSections settings)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(settings);

        var preferences = composition.Preferences;

        // A cap edited while children are queued must reach the running trees now; the per-turn apply only covers the next turn.
        preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppPreferences.AgentsMaxParallelSubAgents))
            {
                composition.Runtimes.RefreshSubAgentLimits();
            }
        };

        SectionViewTemplate.Register<ModelsVisibilitySectionViewModel>(static () => new ModelsVisibilitySectionView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.ModelsVisibility,
            ModelsVisibilityStrings.SectionTitleKey,
            SettingsSectionIds.ModelsVisibilityOrder,
            new ModelsVisibilitySectionViewModel(composition.Providers, preferences),
            SettingsGroups.Agent,
            "IconEye"));

        SectionViewTemplate.Register<AgentsSettingsSectionViewModel>(static () => new AgentsSettingsSectionView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.Agents,
            AgentSettingsStrings.AgentsSectionTitleKey,
            SettingsSectionIds.AgentsOrder,
            new AgentsSettingsSectionViewModel(preferences),
            SettingsGroups.Agent,
            "IconBot"));

        SectionViewTemplate.Register<IdleArchiveSettingsSectionViewModel>(static () => new IdleArchiveSettingsSectionView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.IdleArchive,
            AgentSettingsStrings.IdleArchiveSectionTitleKey,
            SettingsSectionIds.IdleArchiveOrder,
            new IdleArchiveSettingsSectionViewModel(preferences),
            SettingsGroups.System,
            "IconArchive"));

        // The guide owns no platform state of its own, so it builds the notifier it probes with; the notification center keeps
        // its own instance, because a cancelled toast must not reach into the guide's probe. The wizard (wave W9) reads the
        // frozen interface, the section asks for the wider INotificationPermissionSection that also offers the test
        // notification settling the state.
        var guide = new SystemNotificationPermissionGuide(new SystemNotifier());
        composition.NotificationPermissionGuide = guide;
        SectionViewTemplate.Register<NotificationPermissionSettingsViewModel>(static () => new NotificationPermissionSettingsView());
        settings.Add(new SettingsSection(
            SettingsSectionIds.NotificationPermission,
            NotificationsStrings.PermissionSectionTitle,
            SettingsSectionIds.NotificationPermissionOrder,
            new NotificationPermissionSettingsViewModel(preferences, guide),
            SettingsGroups.System,
            "IconShieldCheck"));
    }
}
