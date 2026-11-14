using Forge.App.Composition;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Pins how the composition root registers the four runtime sections of wave W1 (<c>models-visibility</c>, <c>agents</c>,
/// <c>notification-permission</c>, <c>idle-archive</c>): each id appears exactly once, in the group whose column lists it, at
/// the order the shared id table allocates, with the view model its data template is keyed on - and the registry stays
/// sorted, so a late addition cannot land in the wrong place of the left column. The registry's own rules are covered by
/// <see cref="SettingsSectionsTests"/> and the rendered window by <see cref="SettingsPanelTests"/>.
/// </summary>
public sealed class SettingsSectionsRegistrationTests
{
    [Fact]
    public async Task The_runtime_sections_are_registered_where_the_panel_expects_them()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-settings-registration-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;
                var items = composition.Settings.Items;
                var orders = items.Select(static item => item.Order).ToList();

                // The left column draws the registry as it stands: ascending, stable for equal orders.
                Assert.Equal(orders.Order(), orders);

                AssertRegistered(items, SettingsSectionIds.ModelsVisibility, SettingsSectionIds.ModelsVisibilityOrder, SettingsGroups.Agent, typeof(ModelsVisibilitySectionViewModel));
                AssertRegistered(items, SettingsSectionIds.Agents, SettingsSectionIds.AgentsOrder, SettingsGroups.Agent, typeof(AgentsSettingsSectionViewModel));
                AssertRegistered(items, SettingsSectionIds.NotificationPermission, SettingsSectionIds.NotificationPermissionOrder, SettingsGroups.System, typeof(NotificationPermissionSettingsViewModel));
                AssertRegistered(items, SettingsSectionIds.IdleArchive, SettingsSectionIds.IdleArchiveOrder, SettingsGroups.System, typeof(IdleArchiveSettingsSectionViewModel));

                // The allocated orders are also the order the two pairs of the wave are listed in.
                var ids = items.Select(static item => item.Id).ToList();
                Assert.True(ids.IndexOf(SettingsSectionIds.ModelsVisibility) < ids.IndexOf(SettingsSectionIds.Agents));
                Assert.True(ids.IndexOf(SettingsSectionIds.NotificationPermission) < ids.IndexOf(SettingsSectionIds.IdleArchive));
            }
            finally
            {
                await composition.DisposeAsync();
            }
        }
        finally
        {
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    /// <summary>
    /// Asserts one registration: the id exactly once (a duplicate throws at registration, a second copy under another id
    /// would draw a second column), the allocated order and group, and the view model the app-level template matches.
    /// </summary>
    private static void AssertRegistered(
        IReadOnlyList<SettingsSection> items,
        string id,
        int order,
        string group,
        Type viewModel)
    {
        var section = Assert.Single(items, item => string.Equals(item.Id, id, StringComparison.Ordinal));

        Assert.Equal(order, section.Order);
        Assert.Equal(group, section.Group);
        Assert.Equal(viewModel, section.ViewModel.GetType());
    }
}
