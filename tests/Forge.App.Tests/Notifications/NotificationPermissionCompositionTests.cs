using System.Reflection;
using Forge.App.Composition;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>
/// Pins the composition root's half of the notification permission guide: <see cref="AppComposition.NotificationPermission"/>
/// is one instance, and the <c>notification-permission</c> section is handed that same instance instead of building one of
/// its own - the state is what the last real test notification said, so the first-run wizard (which only reads the
/// composition) and the section must not disagree about it. The section itself is covered by
/// <see cref="NotificationPermissionSettingsViewModelTests"/> and the guide by <see cref="NotificationPermissionGuideTests"/>.
/// </summary>
public sealed class NotificationPermissionCompositionTests
{
    [Fact]
    public async Task The_section_reads_the_guide_the_composition_exposes()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-notification-permission-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;

                var section = Assert.IsType<NotificationPermissionSettingsViewModel>(
                    Assert.Single(composition.Settings.Items, IsPermissionSection).ViewModel);

                Assert.Same(composition.NotificationPermission, GuideOf(section));

                // The sentence the user sees is that guide's state, not a second copy that was taken at construction time.
                Assert.Equal(NotificationPermissionTexts.StateText(composition.NotificationPermission.State), section.StateText);
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

    private static bool IsPermissionSection(SettingsSection section) =>
        string.Equals(section.Id, SettingsSectionIds.NotificationPermission, StringComparison.Ordinal);

    /// <summary>
    /// The guide the section holds. It keeps it in a field and reaches it only through its two commands and the sentence, so
    /// the instance identity this test is about is read from that field.
    /// </summary>
    private static INotificationPermissionGuide GuideOf(NotificationPermissionSettingsViewModel section)
    {
        var field = typeof(NotificationPermissionSettingsViewModel).GetField(
            "guide",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(
            field is not null,
            "NotificationPermissionSettingsViewModel no longer has a 'guide' field; this test reads it to compare the instance.");
        return Assert.IsAssignableFrom<INotificationPermissionGuide>(field!.GetValue(section));
    }
}
