using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Notifications;

/// <summary>
/// The one-line form of <see cref="INotifications.NotifyAsync"/> for the shell's own call sites: a feature that has just
/// finished something does not wait for the desktop notification to be over, and a notification that fails must never
/// reach it as an exception.
/// <para>The receiver is the nullable seam every feature holds (<c>INotifications?</c>), so a shell built without the
/// notification wiring - a test, a panel constructed on its own - simply says nothing.</para>
/// </summary>
internal static class NotificationExtensions
{
    /// <summary>
    /// Shows <paramref name="title"/> and <paramref name="body"/> at <paramref name="level"/>, or counts them onto the
    /// toast already carrying the same <paramref name="key"/>; never throws and never blocks the caller.
    /// </summary>
    public static void Notify(
        this INotifications? notifications,
        NotificationLevel level,
        LocText title,
        LocText body,
        string? key = null)
    {
        if (notifications is null)
        {
            return;
        }

        _ = SendAsync(notifications, level, title, body, key);
    }

    private static async Task SendAsync(
        INotifications notifications,
        NotificationLevel level,
        LocText title,
        LocText body,
        string? key)
    {
        try
        {
            await notifications.NotifyAsync(level, title, body, key).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nobody is waiting for this notification any more.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Notify.ZLogWarning($"the notification failed: {exception.Message}");
        }
    }
}
