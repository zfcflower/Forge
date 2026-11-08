using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Forge.App.Localization;

namespace Forge.App.Editing;

/// <summary>
/// Opens what the user clicks in the editor: <c>http</c>, <c>https</c> and <c>mailto</c> text routed to the operating
/// system's default handler through Avalonia's <see cref="Launcher"/>, and the <c>forge-diag:</c> links the inline
/// diagnostic badges raise.
/// <para>AvaloniaEdit's own handler opens the URI with <c>Process.Start</c> from a window class handler and swallows every
/// failure. This router runs first, marks the event handled, and reports a failed launch on the editor's notice line
/// instead of doing nothing.</para>
/// </summary>
internal sealed class EditorLinkRouter : IDisposable
{
    private readonly TextEditor editor;
    private readonly Action<LocText> notice;
    private bool disposed;

    public EditorLinkRouter(TextEditor editor, Action<LocText> notice)
    {
        this.editor = editor;
        this.notice = notice;
        editor.AddHandler(VisualLineLinkText.OpenUriEvent, OnOpenUri);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        editor.RemoveHandler(VisualLineLinkText.OpenUriEvent, OnOpenUri);
    }

    private void OnOpenUri(object? sender, OpenUriRoutedEventArgs e)
    {
        e.Handled = true;
        if (disposed)
        {
            return;
        }

        if (!IsSupportedLink(e.Uri))
        {
            return;
        }

        _ = LaunchAsync(e.Uri);
    }

    private async Task LaunchAsync(Uri uri)
    {
        try
        {
            if (TopLevel.GetTopLevel(editor) is not { } topLevel)
            {
                notice(EditorStrings.LinkFailedText(uri.ToString()));
                return;
            }

            var opened = await topLevel.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
            notice(opened ? default : EditorStrings.LinkFailedText(uri.ToString()));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.ExternalException or UriFormatException)
        {
            System.Diagnostics.Trace.WriteLine("[editor] link launch failed: " + exception.Message);
            notice(EditorStrings.LinkFailedText(exception.Message));
        }
    }

    /// <summary>Marks an event handled without doing anything, so a disabled option stays silent instead of falling through.</summary>
    internal static bool IsSupportedLink(Uri uri) => uri.Scheme is "http" or "https" or "mailto";
}
