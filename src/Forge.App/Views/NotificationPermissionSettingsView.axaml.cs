using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The <c>notification-permission</c> section's view: the state sentence, the two actions and the reprompt switch. The
/// commands and the labels are the view model's, so the view needs no code beyond the compiled bindings.
/// </summary>
internal sealed partial class NotificationPermissionSettingsView : UserControl
{
    public NotificationPermissionSettingsView() => InitializeComponent();
}
