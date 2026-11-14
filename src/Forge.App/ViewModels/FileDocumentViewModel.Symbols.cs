using Forge.Editor.Lsp;

namespace Forge.App.ViewModels;

/// <summary>
/// The one language-server feature the palette needs from the editor: the document's symbol tree. It lives in its own part
/// for the same reason the other LSP requests do - it reuses the editor's request gate
/// (<c>PrepareEditorRequestAsync</c>), so it waits for the server to open, refuses a document that moved on, and reports a
/// failure in the editor's own status line - and it is separate from
/// <c>FileDocumentViewModel.Lsp.cs</c> so the editor's own features stay untouched.
/// </summary>
internal sealed partial class FileDocumentViewModel
{
    /// <summary>
    /// The active document's symbols, or null when there is nothing to ask: no document, no server for the language, a
    /// request already superseded, or a failed call (which the editor reports itself).
    /// </summary>
    internal async Task<IReadOnlyList<DocumentSymbol>?> RequestDocumentSymbolsAsync(CancellationToken ct)
    {
        if (await PrepareEditorRequestAsync(0, ct, silent: true).ConfigureAwait(true) is not { } request)
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, request.Token);
        try
        {
            var symbols = await request.Session.RequestDocumentSymbolsAsync(request.Document, linked.Token)
                .ConfigureAwait(true);
            return IsEditorRequestCurrent(request) ? symbols : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportEditorLspFailure(exception, request, linked.Token);
            return null;
        }
    }
}
