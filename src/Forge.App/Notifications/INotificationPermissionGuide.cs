namespace Forge.App.Notifications;

/// <summary>Whether the operating system lets the app show desktop notifications.</summary>
internal enum NotificationPermissionState
{
    /// <summary>The platform offers no way to ask.</summary>
    Unknown = 0,

    Granted = 1,

    Denied = 2,

    /// <summary>The platform has no notification permission at all (nothing to fix).</summary>
    NotApplicable = 3,
}

/// <summary>
/// Reads the OS notification permission and walks the user to where it is fixed. Declared by wave W0; wave W1 implements it
/// (and owns <c>Notifications/**</c>), the first-run wizard (W9) only calls it.
/// </summary>
internal interface INotificationPermissionGuide
{
    /// <summary>The current permission; never throws, an unreadable state is <see cref="NotificationPermissionState.Unknown"/>.</summary>
    Task<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the place the permission is granted (the OS settings page). False when nothing could be opened.</summary>
    Task<bool> OpenSettingsAsync(CancellationToken cancellationToken = default);
}
