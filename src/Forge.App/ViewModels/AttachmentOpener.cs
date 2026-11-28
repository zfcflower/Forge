using Forge.App.Logging;
using Forge.App.Services;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// An attachment of a sent chat message, as the thing a click opens: its bytes become a file on demand, and its kind says
/// whether FORGE's own preview renders it (an image, a text or source file) or the system's application does (a PDF, an audio
/// track, a video).
/// </summary>
internal interface IAttachmentFile
{
    /// <summary>What the attachment is, which decides who opens it.</summary>
    AttachmentKind Kind { get; }

    /// <summary>The file it becomes, or null when there is nothing to write. Runs on a pool thread.</summary>
    string? Materialize();
}

/// <summary>
/// Opens one attachment of a chat message: inside FORGE when its own preview can render it — a picture in the picture preview,
/// a text or source file in the editor — and in the system's default application otherwise, which is the only thing that can
/// show a PDF, an audio track or a video. A failure is traced, never thrown into the click.
/// </summary>
internal static class AttachmentOpener
{
    internal static async Task OpenAsync(IAttachmentFile attachment)
    {
        string? path;
        try
        {
            path = await Task.Run(attachment.Materialize).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"attachment could not be materialized: {exception.Message}");
            return;
        }

        if (path is null)
        {
            return;
        }

        if (attachment.Kind is AttachmentKind.Image or AttachmentKind.Text && EditorFileOpener.Handler is { } open)
        {
            open(path);
            return;
        }

        OpenWithSystem(path);
    }

    /// <summary>Hands the file to the OS's default application for its type: the one reader that knows a PDF or a video.</summary>
    private static void OpenWithSystem(string path)
    {
        if (SystemFileOpener.Handler is { } open)
        {
            open(path);
            return;
        }

        AppLogs.Session.ZLogWarning($"attachment not opened: this host has no system opener ({path})");
    }
}
