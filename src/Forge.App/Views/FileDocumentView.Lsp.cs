using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Rendering;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Editor.Lsp;
using EditorDocument = AvaloniaEdit.Document.TextDocument;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

public partial class FileDocumentView
{
    private FileDocumentViewModel? lspModel;
    private EditorDocument? lspDocument;
    private EditorLspDiagnosticRenderer? diagnosticRenderer;
    private CompletionWindow? completionWindow;
    private CancellationTokenSource? completionCts;
    private CancellationTokenSource? hoverCts;
    private CancellationTokenSource? definitionCts;
    private int lspGeneration;
    private bool completionIncomplete;
    private int hoverPendingOffset = -1;
    private int hoverStart = -1;
    private int hoverEnd = -1;

    private void InitEditorLsp()
    {
        Editor.AddHandler(InputElement.KeyDownEvent, OnEditorLspKeyDown, RoutingStrategies.Tunnel);
        Editor.AddHandler(InputElement.PointerPressedEvent, OnEditorLspPointerPressed, RoutingStrategies.Tunnel);
    }

    private void AttachEditorLsp(FileDocumentViewModel viewModel)
    {
        DetachEditorLsp();
        if (Editor.Document is not { } document)
        {
            return;
        }

        lspModel = viewModel;
        lspDocument = document;
        lspGeneration++;
        var textView = Editor.TextArea.TextView;
        diagnosticRenderer = new EditorLspDiagnosticRenderer(document);
        textView.BackgroundRenderers.Add(diagnosticRenderer);
        viewModel.EditorDiagnosticsChanged += OnEditorDiagnosticsChanged;
        viewModel.RevealRequested += OnEditorRevealRequested;
        Editor.TextArea.TextEntered += OnEditorLspTextEntered;
        textView.PointerHover += OnEditorHover;
        textView.PointerHoverStopped += OnEditorHoverStopped;
        Editor.PointerExited += OnEditorPointerExited;
        viewModel.BeginEditorLsp();
        ApplyPendingReveal();
    }

    private void DetachEditorLsp()
    {
        lspGeneration++;
        CancelEditorLspWork();
        var textView = Editor.TextArea.TextView;
        Editor.TextArea.TextEntered -= OnEditorLspTextEntered;
        textView.PointerHover -= OnEditorHover;
        textView.PointerHoverStopped -= OnEditorHoverStopped;
        Editor.PointerExited -= OnEditorPointerExited;
        if (lspModel is { } viewModel)
        {
            viewModel.EditorDiagnosticsChanged -= OnEditorDiagnosticsChanged;
            viewModel.RevealRequested -= OnEditorRevealRequested;
            viewModel.EndEditorLsp();
            lspModel = null;
        }

        if (diagnosticRenderer is { } renderer)
        {
            textView.BackgroundRenderers.Remove(renderer);
            renderer.Dispose();
            diagnosticRenderer = null;
            textView.InvalidateLayer(KnownLayer.Selection);
        }

        lspDocument = null;
    }

    private bool IsLspCurrent(FileDocumentViewModel viewModel, int generation) =>
        ReferenceEquals(lspModel, viewModel) && generation == lspGeneration && ReferenceEquals(Editor.Document, lspDocument);

    private void ReportLspViewError(FileDocumentViewModel viewModel, int generation, Exception exception)
    {
        AppLogs.Editor.ZLogError(exception, $"language feature failed");
        if (IsLspCurrent(viewModel, generation))
        {
            viewModel.ReportEditorLspNotice(new LocText(InspectorStrings.FilesLspUnavailable, LocText.Literal(exception.Message)));
        }
    }

    private void OnEditorDiagnosticsChanged(object? sender, EditorLspDiagnosticsEventArgs e)
    {
        if (lspModel is not { IsEditorFaulted: false } || diagnosticRenderer is not { } renderer)
        {
            return;
        }

        renderer.SetDiagnostics(e.Set);
        bridge?.Diagnostics.SetDiagnostics(e.Set.Items);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void OnEditorRevealRequested(object? sender, EventArgs e) => ApplyPendingReveal();

    private void ApplyPendingReveal()
    {
        if (lspModel is not { } viewModel || Editor.Document is not { } document ||
            viewModel.TakePendingRevealOffset() is not { } offset || offset > document.TextLength)
        {
            return;
        }

        MoveCaretTo(document, offset);
    }

    private void MoveCaretTo(EditorDocument document, int offset)
    {
        Editor.TextArea.Caret.Offset = offset;
        Editor.TextArea.ClearSelection();
        var location = document.GetLocation(offset);
        Editor.ScrollTo(location.Line, location.Column);
        Editor.TextArea.Caret.BringCaretToView();
        Editor.TextArea.Focus();
    }

    private void OnEditorLspKeyDown(object? sender, KeyEventArgs e)
    {
        if (lspModel is null)
        {
            return;
        }

        CloseHover();
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
        }
        else if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = GoToDefinitionAsync(Editor.CaretOffset);
        }
        else if (completionWindow is { } popup && TryMoveCompletionSelection(popup.CompletionList, e))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            var hadWork = completionCts is not null || definitionCts is not null;
            CancelEditorLspWork();
            if (hadWork)
            {
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Moves the completion list's selection from the editor's key handler. The library forwards arrows through a stacked
    /// input handler, which in the real window did not move the selection (the headless tests did not catch it because the
    /// synthetic path works there); doing it here is deterministic, and marking the event handled keeps the library from
    /// stepping twice. Modifier-free keys only: Ctrl+Home and friends stay with the editor.
    /// </summary>
    private static bool TryMoveCompletionSelection(AvaloniaEdit.CodeCompletion.CompletionList list, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || list.ListBox is not { } box || box.ItemCount == 0)
        {
            return false;
        }

        var index = box.SelectedIndex;
        var target = e.Key switch
        {
            Key.Down => index + 1,
            Key.Up => index - 1,
            Key.PageDown => index + Math.Max(1, box.VisibleItemCount),
            Key.PageUp => index - Math.Max(1, box.VisibleItemCount),
            Key.Home => 0,
            Key.End => box.ItemCount - 1,
            _ => int.MinValue,
        };

        if (target == int.MinValue)
        {
            return false;
        }

        // Wrap around like the library does, so Up on the first item lands on the last one.
        var count = box.ItemCount;
        target = ((target % count) + count) % count;
        box.SelectIndex(target);
        return true;
    }

    private void OnEditorLspTextEntered(object? sender, TextInputEventArgs e)
    {
        if (lspModel is not { } viewModel || Editor.IsReadOnly || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        // A list the server marked incomplete is re-asked on the next character: its own filtering would show a shrinking
        // snapshot of what it already sent, not what the document now asks for.
        if (completionWindow is not null && completionIncomplete)
        {
            _ = ShowCompletionAsync();
            return;
        }

        foreach (var trigger in viewModel.CompletionTriggers)
        {
            if (string.Equals(e.Text, trigger, StringComparison.Ordinal))
            {
                _ = ShowCompletionAsync();
                return;
            }
        }
    }

    private void OnEditorLspPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (lspModel is null)
        {
            return;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Alt);
        if (modifiers is not (KeyModifiers.Control or KeyModifiers.Meta) ||
            !e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed ||
            !TryGetOffsetAt(e, out var offset))
        {
            return;
        }

        e.Handled = true;
        _ = GoToDefinitionAsync(offset);
    }

    private bool TryGetOffsetAt(PointerEventArgs e, out int offset)
    {
        offset = 0;
        var textView = Editor.TextArea.TextView;
        if (Editor.Document is not { } document || !textView.VisualLinesValid)
        {
            return false;
        }

        var local = e.GetPosition(textView);
        if (!new Rect(textView.Bounds.Size).Contains(local) ||
            textView.GetPositionFloor(local + textView.ScrollOffset) is not { } position)
        {
            return false;
        }

        if (position.Line < 1 || position.Line > document.LineCount || position.Column > document.GetLineByNumber(position.Line).Length)
        {
            return false;
        }

        offset = document.GetOffset(position.Location);
        return true;
    }

    private async Task ShowCompletionAsync()
    {
        if (lspModel is not { } viewModel || Editor.IsReadOnly || Editor.Document is not { } document)
        {
            return;
        }

        CloseCompletionWindow();
        var generation = lspGeneration;
        var offset = Editor.CaretOffset;
        var cts = ReplaceCts(ref completionCts);
        var versionBefore = document.Version;
        try
        {
            var result = await viewModel.RequestCompletionAsync(offset, cts.Token).ConfigureAwait(true);
            if (result is null)
            {
                // No session, no server completion, or a server that returned nothing: the words of the document are still
                // something to offer, as long as this request is not a cancelled or superseded one.
                if (cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) ||
                    !ReferenceEquals(Editor.Document, document) || Editor.CaretOffset != offset ||
                    document.Version.CompareAge(versionBefore) != 0)
                {
                    return;
                }

                result = EditorWordCompletion.Build(document, offset);
            }

            if (result is null || cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) ||
                !ReferenceEquals(Editor.Document, document) || Editor.CaretOffset != result.RequestOffset ||
                document.Version.CompareAge(versionBefore) != 0)
            {
                return;
            }

            ShowCompletionWindow(viewModel, document, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
        }
        finally
        {
            if (ReferenceEquals(completionCts, cts))
            {
                completionCts = null;
            }

            cts.Dispose();
        }
    }

    private void ShowCompletionWindow(FileDocumentViewModel viewModel, EditorDocument document, EditorLspCompletion result)
    {
        var caret = Editor.CaretOffset;
        if (result.PrefixStart > caret)
        {
            return;
        }

        var prefix = document.GetText(result.PrefixStart, caret - result.PrefixStart);
        if (!result.Items.Any(item => item.FilterText.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var log = new EditorLspChangeLog(document);
        try
        {
            completionIncomplete = result.IsIncomplete;
            var window = new CompletionWindow(Editor.TextArea) { StartOffset = result.PrefixStart };
            window.CompletionList.IsFiltering = true;
            // The workbench's row lists blank out ListBoxItem backgrounds globally; this class gives the popup back a
            // visible selected row (see Resources/Controls.axaml).
            window.CompletionList.Classes.Add("completion");
            void ApplyFailed() => viewModel.ReportEditorLspNotice(new LocText(InspectorStrings.EditorLspApplyFailed));
            for (var rank = 0; rank < result.Items.Count; rank++)
            {
                window.CompletionList.CompletionData.Add(new EditorLspCompletionData(result.Items[rank], rank, log, ApplyFailed));
            }

            window.Closed += (_, _) =>
            {
                log.Dispose();
                if (ReferenceEquals(completionWindow, window))
                {
                    completionWindow = null;
                    completionIncomplete = false;
                }
            };
            completionWindow = window;
            window.Show();
            window.CompletionList.SelectItem(prefix);
        }
        catch
        {
            log.Dispose();
            completionWindow = null;
            throw;
        }
    }

    private void CloseCompletionWindow()
    {
        var window = completionWindow;
        completionWindow = null;
        completionIncomplete = false;
        window?.Hide();
    }

    private void OnEditorHover(object? sender, PointerEventArgs e)
    {
        if (lspModel is not { } viewModel || completionWindow is not null || Editor.Document is not { } document)
        {
            return;
        }

        if (!TryGetOffsetAt(e, out var offset))
        {
            CloseHover();
            return;
        }

        if ((ToolTip.GetIsOpen(Editor) && offset >= hoverStart && offset <= hoverEnd) ||
            (hoverCts is not null && hoverPendingOffset == offset))
        {
            return;
        }

        _ = ShowHoverAsync(viewModel, document, offset);
    }

    private void OnEditorHoverStopped(object? sender, PointerEventArgs e)
    {
        if (TryGetOffsetAt(e, out var offset) && ToolTip.GetIsOpen(Editor) && offset >= hoverStart && offset <= hoverEnd)
        {
            return;
        }

        CloseHover();
    }

    private void OnEditorPointerExited(object? sender, PointerEventArgs e) => CloseHover();

    private async Task ShowHoverAsync(FileDocumentViewModel viewModel, EditorDocument document, int offset)
    {
        var generation = lspGeneration;
        var cts = ReplaceCts(ref hoverCts);
        hoverPendingOffset = offset;
        var versionBefore = document.Version;
        try
        {
            var hover = await viewModel.RequestHoverAsync(offset, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) || !ReferenceEquals(Editor.Document, document) ||
                document.Version.CompareAge(versionBefore) != 0)
            {
                return;
            }

            var parts = new List<string>();
            var start = hover?.Start ?? offset;
            var end = hover?.End ?? offset;
            if (diagnosticRenderer is { } renderer)
            {
                // The end-of-line dot belongs to the line, not to a squiggle, so it explains its own messages.
                var covering = renderer.FindAt(offset);
                if (covering.Count == 0 && document.GetLineByOffset(Math.Min(offset, document.TextLength)) is { } line &&
                    bridge?.Diagnostics.LineHasDiagnostics(line.EndOffset) == true)
                {
                    covering = renderer.FindOnLine(line.LineNumber);
                }

                foreach (var diagnostic in covering)
                {
                    parts.Add(Loc.Instance[SeverityKey(diagnostic.Severity)] + ": " + diagnostic.Message +
                        (string.IsNullOrEmpty(diagnostic.Code) ? string.Empty : " [" + diagnostic.Code + "]"));
                    if (hover is null)
                    {
                        start = diagnostic.Start;
                        end = diagnostic.End;
                    }
                }
            }

            if (hover is not null)
            {
                parts.Add(hover.Text);
            }

            if (parts.Count == 0)
            {
                CloseHover();
                return;
            }

            var content = new TextBlock { Text = string.Join("\n\n", parts), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 560 };
            ToolTip.SetPlacement(Editor, PlacementMode.Pointer);
            ToolTip.SetTip(Editor, content);
            ToolTip.SetIsOpen(Editor, true);
            hoverStart = start;
            hoverEnd = end;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
        }
        finally
        {
            if (ReferenceEquals(hoverCts, cts))
            {
                hoverCts = null;
                hoverPendingOffset = -1;
            }

            cts.Dispose();
        }
    }

    private static string SeverityKey(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => InspectorStrings.EditorLspSeverityError,
        DiagnosticSeverity.Warning => InspectorStrings.EditorLspSeverityWarning,
        DiagnosticSeverity.Information => InspectorStrings.EditorLspSeverityInformation,
        _ => InspectorStrings.EditorLspSeverityHint,
    };

    private void CloseHover()
    {
        hoverCts?.Cancel();
        if (ToolTip.GetIsOpen(Editor))
        {
            ToolTip.SetIsOpen(Editor, false);
        }

        ToolTip.SetTip(Editor, null);
        hoverStart = -1;
        hoverEnd = -1;
    }

    private async Task GoToDefinitionAsync(int offset)
    {
        if (lspModel is not { } viewModel || Editor.Document is not { } document)
        {
            return;
        }

        var generation = lspGeneration;
        var cts = ReplaceCts(ref definitionCts);
        var versionBefore = document.Version;
        try
        {
            var result = await viewModel.RequestDefinitionAsync(offset, cts.Token).ConfigureAwait(true);
            if (result is null || cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) ||
                !ReferenceEquals(Editor.Document, document) || document.Version.CompareAge(versionBefore) != 0)
            {
                return;
            }

            if (result.Kind == EditorLspDefinitionKind.SameFile)
            {
                if (result.Offset <= document.TextLength)
                {
                    MoveCaretTo(document, result.Offset);
                }
            }
            else
            {
                viewModel.OpenDefinition(result.Target);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
        }
        finally
        {
            if (ReferenceEquals(definitionCts, cts))
            {
                definitionCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelEditorLspWork()
    {
        completionCts?.Cancel();
        definitionCts?.Cancel();
        CloseHover();
        CloseCompletionWindow();
    }

    private static CancellationTokenSource ReplaceCts(ref CancellationTokenSource? field)
    {
        var previous = field;
        var next = new CancellationTokenSource();
        field = next;
        previous?.Cancel();
        return next;
    }
}
