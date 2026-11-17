using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Forge.App.Logging;
using Forge.App.Services;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// Opens what a transcript points at: a web address in the browser, a source file in the editor tab, and everything the
/// editor cannot show (PDF, HTML, pictures, office files) in the system's own application. One owner, so replies, tool rows
/// and images all behave the same way.
/// </summary>
internal static class LinkLauncher
{
    /// <summary>The workspace of the session <paramref name="origin"/> sits in, for resolving relative paths; null outside a session.</summary>
    internal static string? WorkDirOf(Control origin) =>
        origin.FindAncestorOfType<SessionView>()?.DataContext is SessionWorkspaceViewModel { WorkDir: { Length: > 0 } dir } ? dir : null;

    /// <summary>Whether <paramref name="url"/> is worth a hand cursor; cheap enough for every pointer move.</summary>
    internal static bool LooksOpenable(string url) => LinkTarget.LooksOpenable(url);

    private static readonly Dictionary<string, bool> ExistsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an inline code path points at a real file of this session. Asked on hover, so the answer is remembered: the disk
    /// is touched once per path, not on every pointer move. Remembered answers are dropped wholesale past a bound.
    /// </summary>
    internal static bool ExistsCached(Control origin, string url)
    {
        var workDir = WorkDirOf(origin);
        var key = workDir + "|" + url;
        if (ExistsCache.TryGetValue(key, out var known))
        {
            return known;
        }

        if (ExistsCache.Count >= 512)
        {
            ExistsCache.Clear();
        }

        var exists = LinkTarget.Classify(url, workDir).Kind is LinkTargetKind.EditorFile or LinkTargetKind.SystemFile;
        ExistsCache[key] = exists;
        return exists;
    }

    /// <summary>Opens <paramref name="url"/>; anything that does not resolve to something openable is ignored.</summary>
    internal static async Task OpenAsync(Control origin, string url)
    {
        try
        {
            var target = LinkTarget.Classify(url, WorkDirOf(origin));
            switch (target.Kind)
            {
                case LinkTargetKind.Web when TopLevel.GetTopLevel(origin) is { } top && Uri.TryCreate(target.Location, UriKind.Absolute, out var uri):
                    await top.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
                    break;
                case LinkTargetKind.EditorFile when EditorFileOpener.Handler is { } open:
                    open(target.Location);
                    break;
                case LinkTargetKind.EditorFile:
                case LinkTargetKind.SystemFile:
                    await OpenWithSystemAsync(origin, target.Location).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException
            or IOException or System.Runtime.InteropServices.ExternalException or UriFormatException)
        {
            AppLogs.Markdown.ZLogWarning($"link launch failed: {exception.Message}");
        }
    }

    /// <summary>Hands a file to the operating system's default application.</summary>
    internal static async Task OpenWithSystemAsync(Control origin, string path)
    {
        if (TopLevel.GetTopLevel(origin) is not { } top)
        {
            return;
        }

        try
        {
            if (await top.StorageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true) is { } file)
            {
                await top.Launcher.LaunchFileAsync(file).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException
            or IOException or System.Runtime.InteropServices.ExternalException)
        {
            AppLogs.Markdown.ZLogWarning($"open with system failed: {exception.Message}");
        }
    }
}
