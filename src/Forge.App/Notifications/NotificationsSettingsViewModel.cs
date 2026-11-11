using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Notifications;

/// <summary>
/// The <c>notifications</c> settings section (<see cref="SettingsSectionIds.Notifications"/>): one switch for the whole
/// notification surface - the toasts in the window and the desktop notifications raised while the app is in the
/// background. It edits <see cref="AppPreferences"/> directly, so the next notification reads the new value and the
/// preferences store persists it after its debounce; nothing is cached here.
/// <para>The texts (label and hint) are <see cref="Loc"/> keys bound in XAML, so a language switch re-renders them without
/// this view model raising anything.</para>
/// </summary>
internal sealed class NotificationsSettingsViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    public NotificationsSettingsViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
    }

    public bool Enabled
    {
        get => preferences.Notifications;
        set
        {
            if (preferences.Notifications == value)
            {
                return;
            }

            preferences.Notifications = value;
            OnPropertyChanged();
        }
    }
}
