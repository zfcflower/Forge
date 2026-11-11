namespace Forge.App.Notifications;

/// <summary>
/// Severity of one notification. It picks the colour of the toast's level bar and how long the toast stays on screen
/// (<see cref="NotificationCenter"/> owns both tables), and it is the only thing a caller has to choose besides the text.
/// </summary>
internal enum NotificationLevel
{
    /// <summary>Something happened that nothing is required for.</summary>
    Info,

    /// <summary>Work finished the way the caller wanted.</summary>
    Success,

    /// <summary>Work finished, but the user may want to look at it.</summary>
    Warning,

    /// <summary>Work failed or was refused.</summary>
    Error,
}
