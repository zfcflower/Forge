using Forge.App.Localization;

namespace Forge.App.Notifications;

/// <summary>
/// The one way a feature tells the user something happened: <see cref="NotificationCenter"/> decides whether that becomes
/// a toast in the window or a desktop notification, and it is the only place either of them is raised from.
/// <para>No implementation throws except for cancellation. A notification that cannot be shown (switched off in the
/// settings, no notification tool on this machine, a system notification that failed or hung) is skipped after a trace
/// line: notifying the user must never be able to break the work that wanted to notify.</para>
/// <para>Callable from any thread: the toast list belongs to the UI thread and the implementation marshals.
/// <see cref="Task"/> completes when the desktop notification is over (a toast on the UI thread returns at once), so a
/// caller that has to know the user was told can await it - the wait is bounded by the system notifier's own timeout.</para>
/// </summary>
internal interface INotifications
{
    /// <param name="level">Severity: colour, and how long a toast stays.</param>
    /// <param name="title">The bold first line. Resolved when shown, so a language switch re-renders it.</param>
    /// <param name="body">The text under the title.</param>
    /// <param name="key">
    /// Identity for de-duplication: two notifications with the same key are one toast that counts its repeats. Defaults to
    /// the level and the resolved title and body, so an identical sentence collapses without the caller doing anything.
    /// </param>
    /// <param name="ct">Cancels the desktop notification; a cancelled call shows nothing.</param>
    Task NotifyAsync(
        NotificationLevel level,
        LocText title,
        LocText body,
        string? key = null,
        CancellationToken ct = default);
}
