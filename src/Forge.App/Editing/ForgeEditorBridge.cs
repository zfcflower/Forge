using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Indentation.CSharp;
using AvaloniaEdit.TextMate;
using Forge.App.Localization;
using Forge.App.ViewModels;
using TextMateSharp.Grammars;
using EditorDocument = AvaloniaEdit.Document.TextDocument;
using TextMateInstallation = AvaloniaEdit.TextMate.TextMate.Installation;

namespace Forge.App.Editing;

internal sealed class ForgeEditorBridge : IDisposable
{
    private const int HighlightLimit = 1 << 20;
    private readonly TextEditor editor;
    private readonly FileDocumentViewModel model;
    private readonly EditorDocument document;
    private readonly EditorSettings settings;
    private readonly EditorFoldingController folding;
    private readonly EditorDiagnosticAdornments adornments;
    private readonly EditorWhitespaceAdornments whitespace;
    private readonly EditorLinkRouter links;
    private RegistryOptions? registry;
    private TextMateInstallation? installation;
    private string? grammarScope;
    private bool noticeShown;
    private bool recheckPosted;
    private bool disposed;

    public ForgeEditorBridge(TextEditor editor, FileDocumentViewModel model, ForgeEditorMirror mirror, string path, EditorSettings settings)
    {
        this.editor = editor;
        this.model = model;
        this.settings = settings;
        document = mirror.Document;
        editor.Document = document;
        EditorOptionsApplier.ApplyOptions(editor, settings);
        EditorOptionsApplier.ApplyColors(editor);
        editor.TextArea.IndentationStrategy = FoldingSyntaxTable.IsCSharp(path)
            ? new CSharpIndentationStrategy(editor.Options)
            : new DefaultIndentationStrategy();

        var shape = FoldingSyntaxTable.ForPath(path);
        folding = new EditorFoldingController(
            editor,
            document,
            () => mirror.Snapshot,
            shape.Syntax,
            shape.Xml,
            model.RequestFoldingRangesAsync,
            model.ReportFoldingNotice);
        if (settings.AutoFolding)
        {
            folding.Enable();
        }

        // The diagnostic dots go after the folding placeholder generator the manager inserts at index 0.
        adornments = new EditorDiagnosticAdornments(editor, document);
        editor.TextArea.TextView.ElementGenerators.Add(adornments);

        // AvaloniaEdit 12 leaves its tab and end-of-line generators unwired, so Forge draws those two glyphs itself.
        whitespace = new EditorWhitespaceAdornments(editor, document);
        whitespace.Configure(settings.ShowTabs, settings.ShowEndOfLine);
        editor.TextArea.TextView.ElementGenerators.Add(whitespace);
        links = new EditorLinkRouter(editor, model.ReportEditorActionNotice);

        settings.PropertyChanged += OnSettingsChanged;
        InstallHighlighting(path);
        document.TextChanged += OnDocumentTextChanged;
    }

    /// <summary>The inline diagnostic dots of this document; the view feeds the latest diagnostic set in.</summary>
    public EditorDiagnosticAdornments Diagnostics => adornments;

    /// <summary>The end-of-line and tab glyphs of this document, drawn by Forge because 12.0.0 does not wire them.</summary>
    public EditorWhitespaceAdornments Whitespace => whitespace;

    /// <summary>Folding for this document; the view model drives the menu commands through it.</summary>
    public EditorFoldingController Folding => folding;

    public void ApplyTheme()
    {
        if (disposed)
        {
            return;
        }

        // The theme variant reaches Forge's own brushes whether or not a TextMate installation exists (plain text files
        // have none), so the colours and the glyph brushes are refreshed first.
        EditorOptionsApplier.ApplyColors(editor);
        whitespace.Configure(settings.ShowTabs, settings.ShowEndOfLine);
        if (installation is null || registry is null)
        {
            return;
        }

        try
        {
            installation.SetTheme(registry.LoadTheme(CurrentThemeName()));
        }
        catch (Exception exception) when (IsAssetFailure(exception))
        {
            ReportHighlightFailure(exception);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        settings.PropertyChanged -= OnSettingsChanged;
        document.TextChanged -= OnDocumentTextChanged;
        links.Dispose();
        editor.TextArea.TextView.ElementGenerators.Remove(whitespace);
        whitespace.Dispose();
        editor.TextArea.TextView.ElementGenerators.Remove(adornments);
        adornments.Dispose();
        folding.Dispose();
        DisposeInstallation();
        editor.Document = new EditorDocument();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        EditorOptionsApplier.ApplyOptions(editor, settings);
        EditorOptionsApplier.ApplyColors(editor);
        whitespace.Configure(settings.ShowTabs, settings.ShowEndOfLine);
        if (e.PropertyName is nameof(EditorSettings.AutoFolding))
        {
            if (settings.AutoFolding)
            {
                folding.Enable();
            }
            else
            {
                folding.Disable();
            }
        }
        else if (e.PropertyName is nameof(EditorSettings.ShowLineNumbers) or nameof(EditorSettings.IndentationSize) or nameof(EditorSettings.TabsToSpaces))
        {
            // The indentation strategy caches the options it was built with.
            RebindIndentation();
        }

        if (e.PropertyName is nameof(EditorSettings.Theme))
        {
            ApplyTheme();
        }

        editor.TextArea.TextView.Redraw();
    }

    private void RebindIndentation()
    {
        if (editor.TextArea.IndentationStrategy is CSharpIndentationStrategy)
        {
            editor.TextArea.IndentationStrategy = new CSharpIndentationStrategy(editor.Options);
        }
    }

    private ThemeName CurrentThemeName() =>
        EditorOptionsApplier.IsDarkTheme(editor, settings) ? ThemeName.DarkPlus : ThemeName.LightPlus;

    private void InstallHighlighting(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return;
        }

        try
        {
            grammarScope = new RegistryOptions(CurrentThemeName()).GetScopeByExtension(extension);
            if (grammarScope is not null)
            {
                Reevaluate();
            }
        }
        catch (Exception exception) when (IsAssetFailure(exception))
        {
            DisposeInstallation();
            grammarScope = null;
            ReportHighlightFailure(exception);
        }
    }

    private void Reevaluate()
    {
        if (disposed || grammarScope is null)
        {
            return;
        }
        if (document.TextLength > HighlightLimit)
        {
            DisposeInstallation();
            if (!noticeShown)
            {
                noticeShown = true;
                model.ReportEditorNotice(new LocText(InspectorStrings.FilesEditorHighlightTooLarge));
            }
            return;
        }
        if (noticeShown)
        {
            noticeShown = false;
            if (model.EditorNotice == new LocText(InspectorStrings.FilesEditorHighlightTooLarge).Resolve())
            {
                model.ReportEditorNotice(default);
            }
        }
        if (installation is not null)
        {
            return;
        }
        try
        {
            var options = new RegistryOptions(CurrentThemeName());
            registry = options;
            installation = editor.InstallTextMate(options, true, OnTextMateError);
            installation.AppliedTheme += OnThemeApplied;
            installation.SetGrammar(grammarScope);
            RestoreForgeBrushes();
        }
        catch (Exception exception) when (IsAssetFailure(exception))
        {
            DisposeInstallation();
            grammarScope = null;
            ReportHighlightFailure(exception);
        }
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        if (disposed || grammarScope is null || recheckPosted)
        {
            return;
        }
        if ((document.TextLength > HighlightLimit) != (installation is null))
        {
            recheckPosted = true;
            Dispatcher.UIThread.Post(RecheckHighlighting, DispatcherPriority.Background);
        }
    }

    private void RecheckHighlighting()
    {
        recheckPosted = false;
        Reevaluate();
    }

    private void DisposeInstallation()
    {
        if (installation is not null)
        {
            installation.AppliedTheme -= OnThemeApplied;
            installation.Dispose();
            installation = null;
        }

        registry = null;
    }

    private static bool IsAssetFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or KeyNotFoundException or NotSupportedException or FormatException
            or System.Text.Json.JsonException;

    private void OnThemeApplied(object? sender, TextMateInstallation e) => RestoreForgeBrushes();

    // TextMate theme colors overwrite the editor's local brushes; keep Forge's own surface palette.
    private void RestoreForgeBrushes()
    {
        if (disposed)
        {
            return;
        }

        EditorOptionsApplier.ApplyColors(editor);
        EditorOptionsApplier.ApplyOptions(editor, settings);
    }

    private void OnTextMateError(Exception exception)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ReportHighlightFailure(exception);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ReportHighlightFailure(exception));
        }
    }

    private void ReportHighlightFailure(Exception exception)
    {
        System.Diagnostics.Trace.WriteLine("[editor] highlighting failed: " + exception);
        if (!disposed)
        {
            model.ReportEditorNotice(new LocText(InspectorStrings.FilesEditorHighlightFailed, LocText.Literal(exception.Message)));
        }
    }

}
