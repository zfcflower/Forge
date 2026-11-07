using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
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
    private RegistryOptions? registry;
    private TextMateInstallation? installation;
    private string? grammarScope;
    private bool noticeShown;
    private bool recheckPosted;
    private bool disposed;

    public ForgeEditorBridge(TextEditor editor, FileDocumentViewModel model, ForgeEditorMirror mirror, string path)
    {
        this.editor = editor;
        this.model = model;
        document = mirror.Document;
        editor.Document = document;
        InstallHighlighting(path);
        document.TextChanged += OnDocumentTextChanged;
    }

    public void ApplyTheme()
    {
        if (installation is null || registry is null || disposed)
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
        document.TextChanged -= OnDocumentTextChanged;
        DisposeInstallation();
        editor.Document = new EditorDocument();
    }

    private ThemeName CurrentThemeName() =>
        editor.ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

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

        if (editor.TryFindResource("SurfaceBrush", editor.ActualThemeVariant, out var surface) && surface is Avalonia.Media.IBrush surfaceBrush)
        {
            editor.Background = surfaceBrush;
        }

        if (editor.TryFindResource("TextPrimaryBrush", editor.ActualThemeVariant, out var text) && text is Avalonia.Media.IBrush textBrush)
        {
            editor.Foreground = textBrush;
        }

        if (editor.TryFindResource("TextMutedBrush", editor.ActualThemeVariant, out var muted) && muted is Avalonia.Media.IBrush mutedBrush)
        {
            editor.LineNumbersForeground = mutedBrush;
        }
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
