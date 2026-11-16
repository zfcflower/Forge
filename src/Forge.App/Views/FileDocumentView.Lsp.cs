using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Rendering;
using Forge.App.Editing;
using Forge.App.Input;
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
    private SemanticTokenColorizer? semanticColorizer;
    private CompletionWindow? completionWindow;
    private CancellationTokenSource? completionCts;
    private CancellationTokenSource? hoverCts;
    private CancellationTokenSource? definitionCts;
    private CancellationTokenSource? referencesCts;
    private Flyout? referenceFlyout;
    private EditorCodeActionMargin? codeActionMargin;
    private EditorInlayHints? inlayHints;
    private CancellationTokenSource? codeActionCts;
    private MenuFlyout? codeActionFlyout;
    private IReadOnlyList<LspCodeAction> codeActions = [];
    private int codeActionOffset = -1;
    private int codeActionVersion = -1;
    private CancellationTokenSource? renameCts;
    private Popup? renamePopup;
    private TextBox? renameBox;
    private int renameOffset = -1;
    private int lspGeneration;
    private bool completionIncomplete;
    private int hoverPendingOffset = -1;
    private int hoverStart = -1;
    private int hoverEnd = -1;

    /// <summary>Quiet period after the caret moved before the server is asked for the actions at its new position.</summary>
    private const int CodeActionDebounceMilliseconds = 300;

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

        // The semantic layer is a line transformer, not a renderer: it recolours existing text runs, and the library only
        // calls it for lines it is about to draw.
        semanticColorizer = new SemanticTokenColorizer(Editor, () => EditorOptionsApplier.IsDarkTheme(Editor, EditorSettings.Current));
        textView.LineTransformers.Add(semanticColorizer);
        semanticColorizer.SetIndex(viewModel.EditorSemanticTokens);
        if (bridge is { } attached)
        {
            attached.HighlightingInstalled += OnHighlightingInstalled;
        }
        viewModel.EditorDiagnosticsChanged += OnEditorDiagnosticsChanged;
        viewModel.SemanticTokensChanged += OnSemanticTokensChanged;
        viewModel.InlayHintsChanged += OnInlayHintsChanged;
        viewModel.CodeActionsRefreshRequested += OnCodeActionsRefreshRequested;
        viewModel.RevealRequested += OnEditorRevealRequested;

        inlayHints = new EditorInlayHints(Editor, document);
        inlayHints.SetEnabled(EditorSettings.Current.ShowInlayHints);
        inlayHints.SetIndex(viewModel.EditorInlayHints);
        textView.ElementGenerators.Add(inlayHints);
        EditorSettings.Current.PropertyChanged += OnEditorSettingsChangedForInlayHints;

        // The light bulb lives in the left margin, next to the line number and folding margins; it measures zero width
        // while the server has no actions for the caret, so it cannot take a click when there is nothing to offer.
        codeActionMargin = new EditorCodeActionMargin(Editor);
        codeActionMargin.SetCaretLine(Editor.TextArea.Caret.Line);
        codeActionMargin.Requested += OnCodeActionsRequested;
        Editor.TextArea.LeftMargins.Add(codeActionMargin);
        Editor.TextArea.Caret.PositionChanged += OnEditorCaretMoved;
        Editor.TextArea.TextEntered += OnEditorLspTextEntered;
        textView.PointerHover += OnEditorHover;
        textView.PointerHoverStopped += OnEditorHoverStopped;
        Editor.PointerExited += OnEditorPointerExited;
        viewModel.BeginEditorLsp();
        ApplyPendingReveal();
        ScheduleCodeActionRefresh();
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
            viewModel.SemanticTokensChanged -= OnSemanticTokensChanged;
            viewModel.InlayHintsChanged -= OnInlayHintsChanged;
            viewModel.CodeActionsRefreshRequested -= OnCodeActionsRefreshRequested;
            viewModel.RevealRequested -= OnEditorRevealRequested;
            viewModel.EndEditorLsp();
            lspModel = null;
        }

        EditorSettings.Current.PropertyChanged -= OnEditorSettingsChangedForInlayHints;
        Editor.TextArea.Caret.PositionChanged -= OnEditorCaretMoved;
        if (codeActionMargin is { } margin)
        {
            margin.Requested -= OnCodeActionsRequested;
            Editor.TextArea.LeftMargins.Remove(margin);
            codeActionMargin = null;
        }

        if (inlayHints is { } hints)
        {
            textView.ElementGenerators.Remove(hints);
            hints.Dispose();
            inlayHints = null;
        }

        CloseRenamePrompt();

        if (semanticColorizer is { } colorizer)
        {
            textView.LineTransformers.Remove(colorizer);
            colorizer.Clear();
            semanticColorizer = null;
        }

        if (bridge is { } attached)
        {
            attached.HighlightingInstalled -= OnHighlightingInstalled;
        }

        CloseReferenceList();

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

    private void OnSemanticTokensChanged(object? sender, EditorLspSemanticTokensEventArgs e) => semanticColorizer?.SetIndex(e.Index);

    /// <summary>
    /// TextMate just appended its own line transformer, which would paint over the semantic ranges; moving this one to the end
    /// of the list keeps the semantic classification on top, whatever order the two were installed in.
    /// </summary>
    private void OnHighlightingInstalled(object? sender, EventArgs e)
    {
        if (semanticColorizer is not { } colorizer)
        {
            return;
        }

        var transformers = Editor.TextArea.TextView.LineTransformers;
        if (transformers.Remove(colorizer))
        {
            transformers.Add(colorizer);
        }
    }

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
        if (KeymapApplier.Gesture(KeymapActionIds.LspCompletion).Matches(e))
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
        }
        else if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = GoToDefinitionAsync(Editor.CaretOffset);
        }
        else if (KeymapApplier.Gesture(KeymapActionIds.LspReferences).Matches(e))
        {
            e.Handled = true;
            _ = ShowReferencesAsync(Editor.CaretOffset);
        }
        else if (KeymapApplier.Gesture(KeymapActionIds.EditorCodeAction).Matches(e))
        {
            e.Handled = true;
            _ = ShowCodeActionsAsync();
        }
        else if (KeymapApplier.Gesture(KeymapActionIds.EditorRename).Matches(e))
        {
            e.Handled = true;
            _ = StartRenameAsync();
        }
        else if (referenceFlyout is not null && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseReferenceList();
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
    /// input handler, which in the real window did not move the selection (a headless run did not catch it because the
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

        // Typing invalidates the code action answer: the offsets the server computed no longer describe this text.
        codeActionOffset = -1;
        codeActionVersion = -1;
        codeActionMargin?.SetActions(false);

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

        // The margin container above the margins does not pass a hit through to them (its item container has no background),
        // so the press arrives here: a press inside the bulb's own bounds is the bulb.
        if (codeActionMargin is { HasActions: true } margin && IsInsideMargin(margin, e))
        {
            e.Handled = true;
            _ = ShowCodeActionsAsync();
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

    private static bool IsInsideMargin(Control margin, PointerEventArgs e) =>
        e.GetCurrentPoint(margin).Properties.IsLeftButtonPressed &&
        new Rect(margin.Bounds.Size).Contains(e.GetPosition(margin));

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

    /// <summary>
    /// "Find references" (Shift+F12): the server lists every place the symbol at the caret is used, and the list opens next
    /// to it. A request that outlived its document is dropped like every other editor request.
    /// </summary>
    private async Task ShowReferencesAsync(int offset)
    {
        if (lspModel is not { } viewModel || Editor.Document is not { } document)
        {
            return;
        }

        CloseReferenceList();
        var generation = lspGeneration;
        var cts = ReplaceCts(ref referencesCts);
        var versionBefore = document.Version;
        try
        {
            var list = await viewModel.RequestReferencesAsync(offset, cts.Token).ConfigureAwait(true);
            if (list is null || cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) ||
                !ReferenceEquals(Editor.Document, document) || document.Version.CompareAge(versionBefore) != 0)
            {
                return;
            }

            if (list.Items.Count == 0)
            {
                viewModel.ReportEditorLspStatus(new LocText(InspectorStrings.EditorLspReferencesNone));
                return;
            }

            OpenReferenceList(viewModel, list);
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
            if (ReferenceEquals(referencesCts, cts))
            {
                referencesCts = null;
            }

            cts.Dispose();
        }
    }

    private void OpenReferenceList(FileDocumentViewModel viewModel, EditorLspReferenceList list)
    {
        CloseReferenceList();
        var content = new ReferenceListView
        {
            DataContext = new ReferenceListViewModel(list, list.Symbol, reference => OpenReference(viewModel, reference)),
        };
        content.CloseRequested += (_, _) => CloseReferenceList();
        var flyout = new Flyout { Content = content, Placement = PlacementMode.Pointer };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(referenceFlyout, flyout))
            {
                referenceFlyout = null;
            }
        };
        referenceFlyout = flyout;
        flyout.ShowAt(Editor, true);
    }

    private void CloseReferenceList()
    {
        var flyout = referenceFlyout;
        referenceFlyout = null;
        referencesCts?.Cancel();
        flyout?.Hide();
    }

    private void OpenReference(FileDocumentViewModel viewModel, EditorLspReference reference)
    {
        if (viewModel.IsEditorPath(reference.Path))
        {
            viewModel.RevealEditorPosition(reference.Target);
        }
        else
        {
            viewModel.OpenDefinition(reference.Target);
        }
    }

    /// <summary>The light bulb, exposed to read what the server offered without driving the mouse.</summary>
    internal EditorCodeActionMargin? CodeActionBulb => codeActionMargin;

    internal IReadOnlyList<LspCodeAction> AvailableCodeActions => codeActions;

    /// <summary>The document version the action list was computed against; the apply path refuses a newer document.</summary>
    internal int CodeActionVersion => codeActionVersion;

    internal MenuFlyout? CodeActionMenu => codeActionFlyout;

    internal bool RenamePromptOpen => renamePopup is { IsOpen: true };

    internal string? RenamePromptText => renameBox?.Text;

    internal EditorInlayHints? InlayHintsView => inlayHints;

    private void OnEditorSettingsChangedForInlayHints(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorSettings.ShowInlayHints))
        {
            inlayHints?.SetEnabled(EditorSettings.Current.ShowInlayHints);
        }
    }

    private void OnInlayHintsChanged(object? sender, EditorLspInlayHintsEventArgs e) => inlayHints?.SetIndex(e.Index);

    private void OnCodeActionsRefreshRequested(object? sender, EventArgs e)
    {
        // A server refresh means the cached answer is stale even when the caret has not moved (Roslyn diagnostics arriving
        // after the first request is the common case): drop the cached offset/version so the schedule actually re-asks
        // instead of being eaten by the same-offset guard below.
        codeActionOffset = -1;
        codeActionVersion = -1;
        ScheduleCodeActionRefresh();
    }

    private void OnEditorCaretMoved(object? sender, EventArgs e)
    {
        codeActionMargin?.SetCaretLine(Editor.TextArea.Caret.Line);
        ScheduleCodeActionRefresh();
    }

    /// <summary>
    /// Asks for the actions at the caret after a quiet period, so moving through a file with the arrow keys does not fire one
    /// request per key. A server without <c>codeActionProvider</c> never gets asked and the bulb stays hidden.
    /// </summary>
    private void ScheduleCodeActionRefresh()
    {
        if (lspModel is not { SupportsCodeActions: true } || Editor.IsReadOnly)
        {
            codeActionMargin?.SetActions(false);
            return;
        }

        // The action list is per caret offset: a selection change that does not move the caret must not ask again. Typing
        // invalidates the list, so the next caret move (or Ctrl+.) asks for the new text.
        if (Editor.CaretOffset == codeActionOffset)
        {
            return;
        }

        var cts = ReplaceCts(ref codeActionCts);
        _ = RefreshCodeActionsAfterDelayAsync(cts);
    }

    private async Task RefreshCodeActionsAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(CodeActionDebounceMilliseconds, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RefreshCodeActionsAsync(cts.Token).ConfigureAwait(true);
    }

    /// <summary>
    /// One "<c>textDocument/codeAction</c> at the caret" round: remembers the list, the offset and the document version it was
    /// computed against, and shows or hides the bulb. False means there is nothing to open.
    /// </summary>
    private async Task<bool> RefreshCodeActionsAsync(CancellationToken ct)
    {
        if (lspModel is not { } viewModel)
        {
            return false;
        }

        var generation = lspGeneration;
        var offset = Editor.CaretOffset;
        FileDocumentViewModel.EditorLspCodeActionList? answer;
        try
        {
            answer = await viewModel.RequestCodeActionsAsync(offset, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
            return false;
        }

        if (ct.IsCancellationRequested || !IsLspCurrent(viewModel, generation) || Editor.CaretOffset != offset)
        {
            return false;
        }

        if (answer is null)
        {
            codeActions = [];
            codeActionOffset = offset;
            codeActionVersion = -1;
            CloseCodeActionMenu();
            codeActionMargin?.SetActions(false);
            return false;
        }

        codeActions = answer.Value.Result.Items;
        codeActionOffset = offset;
        codeActionVersion = answer.Value.Version;
        CloseCodeActionMenu();
        codeActionMargin?.SetActions(codeActions.Count > 0);
        return codeActions.Count > 0;
    }

    private void OnCodeActionsRequested(object? sender, EventArgs e) => _ = ShowCodeActionsAsync();

    /// <summary>
    /// Ctrl+. and the bulb: open the list the caret already has, or ask once when there is none. An empty answer says so on the
    /// status line instead of opening an empty menu.
    /// </summary>
    private async Task ShowCodeActionsAsync()
    {
        if (lspModel is not { } viewModel || Editor.IsReadOnly)
        {
            return;
        }

        CloseHover();
        var offset = Editor.CaretOffset;
        var cts = ReplaceCts(ref codeActionCts);
        if (codeActionOffset != offset || codeActions.Count == 0)
        {
            if (!await RefreshCodeActionsAsync(cts.Token).ConfigureAwait(true))
            {
                if (!cts.IsCancellationRequested && IsLspCurrent(viewModel, lspGeneration) && Editor.CaretOffset == offset)
                {
                    viewModel.ReportEditorLspStatus(new LocText(InspectorStrings.EditorLspCodeActionNone));
                }

                return;
            }
        }

        if (cts.IsCancellationRequested || !IsLspCurrent(viewModel, lspGeneration) || Editor.CaretOffset != offset)
        {
            return;
        }

        OpenCodeActionMenu(codeActions);
    }

    private void OpenCodeActionMenu(IReadOnlyList<LspCodeAction> actions)
    {
        CloseCodeActionMenu();
        var flyout = new MenuFlyout { Placement = PlacementMode.Pointer };
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var item = new MenuItem { Header = CodeActionHeader(action), IsEnabled = !action.Disabled };
            AutomationProperties.SetAutomationId(item, "code-action-" + i);
            if (action.DisabledReason is { Length: > 0 } reason)
            {
                ToolTip.SetTip(item, reason);
            }

            item.Click += (_, _) => _ = ApplyCodeActionAsync(action);
            flyout.Items.Add(item);
        }

        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(codeActionFlyout, flyout))
            {
                codeActionFlyout = null;
            }
        };
        codeActionFlyout = flyout;
        flyout.ShowAt(Editor, true);
    }

    /// <summary>The menu row: the action's title, with its kind underneath so a quick fix reads differently from a refactor.</summary>
    private static Control CodeActionHeader(LspCodeAction action)
    {
        var title = new TextBlock { Text = action.Title, TextWrapping = TextWrapping.NoWrap };
        if (action.Kind is not { Length: > 0 } kind)
        {
            return title;
        }

        var caption = new TextBlock { Text = kind, Opacity = 0.65, FontSize = 11 };
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(title);
        panel.Children.Add(caption);
        return panel;
    }

    private void CloseCodeActionMenu()
    {
        var flyout = codeActionFlyout;
        codeActionFlyout = null;
        flyout?.Hide();
    }

    private async Task ApplyCodeActionAsync(LspCodeAction action)
    {
        CloseCodeActionMenu();
        codeActionMargin?.SetActions(false);
        if (lspModel is not { } viewModel || Editor.Document is not { } document)
        {
            return;
        }

        var generation = lspGeneration;
        var version = codeActionVersion;
        // The edit is about to change the text, so the answer this list came from no longer describes it; the captured
        // version above is what the view model checks the edit against.
        codeActionOffset = -1;
        codeActionVersion = -1;
        var cts = ReplaceCts(ref codeActionCts);
        try
        {
            var result = await viewModel
                .ApplyCodeActionAsync(action, version, edits => ApplyLocalEdits(document, edits), cts.Token)
                .ConfigureAwait(true);
            if (result.Error is null && IsLspCurrent(viewModel, generation))
            {
                codeActionOffset = -1;
                codeActionVersion = -1;
                ScheduleCodeActionRefresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
        }
    }

    /// <summary>
    /// The open buffer's half of a workspace edit: one undo step, and the mirror reports the document as edited so the save
    /// command picks it up. Refuses (and lets the view model report it) when the offsets no longer fit the document.
    /// </summary>
    private static void ApplyLocalEdits(EditorDocument document, IReadOnlyList<EditorLspEdit> edits)
    {
        if (!EditorWorkspaceEditApplier.TryApply(document, edits))
        {
            throw new InvalidOperationException("The server edit no longer fits the document.");
        }
    }

    /// <summary>
    /// F2: ask the server whether this position can be renamed, then open the input box seeded with the server's placeholder (or
    /// the word at the caret). A server without <c>renameProvider</c> has no entry at all — F2 says why and opens nothing.
    /// </summary>
    private async Task StartRenameAsync()
    {
        if (lspModel is not { } viewModel || Editor.IsReadOnly)
        {
            return;
        }

        if (!viewModel.SupportsRename)
        {
            viewModel.ReportEditorLspStatus(
                new LocText(InspectorStrings.EditorLspUnsupported, new LocText(InspectorStrings.EditorLspFeatureRename)));
            return;
        }

        CloseCodeActionMenu();
        CloseHover();
        var generation = lspGeneration;
        var offset = Editor.CaretOffset;
        var cts = ReplaceCts(ref renameCts);
        EditorLspPrepareRename? preparation;
        try
        {
            preparation = await viewModel.PrepareRenameAsync(offset, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
            return;
        }

        if (cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) || Editor.CaretOffset != offset)
        {
            return;
        }

        if (preparation is not { Allowed: true })
        {
            if (preparation is not null)
            {
                viewModel.ReportEditorLspStatus(new LocText(InspectorStrings.EditorLspRenameNone));
            }

            return;
        }

        ShowRenamePrompt(offset, preparation.Placeholder ?? viewModel.SymbolAt(offset));
    }

    private void ShowRenamePrompt(int offset, string seed)
    {
        CloseRenamePrompt();
        var box = new TextBox
        {
            Text = seed,
            MinWidth = 180,
            PlaceholderText = Loc.Instance[InspectorStrings.EditorLspRenamePrompt],
        };
        AutomationProperties.SetAutomationId(box, "editor-rename-input");
        box.KeyDown += OnRenameBoxKeyDown;
        var hint = new TextBlock { Text = Loc.Instance[InspectorStrings.EditorLspRenameHint], Opacity = 0.65, FontSize = 11 };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(box);
        panel.Children.Add(hint);
        var card = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        if (EditorOptionsApplier.Brush(Editor, Editor.ActualThemeVariant, "CardBackgroundBrush") is { } background)
        {
            card.Background = background;
        }

        if (EditorOptionsApplier.Brush(Editor, Editor.ActualThemeVariant, "BorderBrush") is { } border)
        {
            card.BorderBrush = border;
        }

        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        var popup = new Popup
        {
            PlacementTarget = Editor,
            Placement = PlacementMode.AnchorAndGravity,
            HorizontalOffset = caret.X,
            VerticalOffset = caret.Bottom + 2,
            Child = card,
            IsLightDismissEnabled = true,
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(renamePopup, popup))
            {
                renamePopup = null;
                renameBox = null;
                renameOffset = -1;
            }
        };
        renameBox = box;
        renamePopup = popup;
        renameOffset = offset;
        popup.IsOpen = true;
        // The popup's root is created by opening it, so focus has to be posted once it exists.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(renameBox, box))
            {
                box.Focus();
                box.SelectAll();
            }
        });
    }

    private void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseRenamePrompt();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            var name = renameBox?.Text ?? string.Empty;
            var offset = renameOffset;
            CloseRenamePrompt();
            _ = ConfirmRenameAsync(offset, name.Trim());
        }
    }

    private void CloseRenamePrompt()
    {
        renameCts?.Cancel();
        var popup = renamePopup;
        renamePopup = null;
        renameBox = null;
        renameOffset = -1;
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    private async Task ConfirmRenameAsync(int offset, string newName)
    {
        if (newName.Length == 0 || lspModel is not { } viewModel || Editor.Document is not { } document || offset < 0)
        {
            return;
        }

        var generation = lspGeneration;
        var cts = ReplaceCts(ref renameCts);
        try
        {
            var rename = await viewModel.RequestRenameAsync(offset, newName, cts.Token).ConfigureAwait(true);
            if (rename is null || cts.IsCancellationRequested || !IsLspCurrent(viewModel, generation) ||
                !ReferenceEquals(Editor.Document, document))
            {
                return;
            }

            var result = await viewModel
                .ApplyWorkspaceEditAsync(rename.Value.Edit, rename.Value.Version, edits => ApplyLocalEdits(document, edits), cts.Token)
                .ConfigureAwait(true);
            if (result.Error is null && IsLspCurrent(viewModel, generation))
            {
                // The rename changed the text: the action list at this caret no longer describes it.
                codeActionOffset = -1;
                codeActionVersion = -1;
                ScheduleCodeActionRefresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportLspViewError(viewModel, generation, exception);
        }
    }

    private void CancelEditorLspWork()
    {
        completionCts?.Cancel();
        definitionCts?.Cancel();
        codeActionCts?.Cancel();
        renameCts?.Cancel();
        CloseHover();
        CloseCompletionWindow();
        CloseReferenceList();
        CloseCodeActionMenu();
        CloseRenamePrompt();
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
