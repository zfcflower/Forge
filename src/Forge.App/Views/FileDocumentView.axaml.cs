using System.ComponentModel;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

public partial class FileDocumentView : UserControl
{
    private FileDocumentViewModel? model;
    private ForgeEditorBridge? bridge;
    private EditorMenuBuilder? builder;
    private ContextMenu contextMenu = null!;
    private MenuFlyout menuFlyout = null!;
    private ReviewOverlay? review;
    private bool attached;

    public FileDocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        InitEditorMenu();
        InitEditorLsp();
        review = new ReviewOverlay(Editor, new ReviewOverlayParts
        {
            Layer = ReviewLayer,
            Icon = ReviewIconButton,
            Composer = ReviewComposer,
            RangeText = ReviewRangeText,
            BodyBox = ReviewBodyBox,
            ComposerStatus = ReviewComposerStatus,
            SaveButton = ReviewSaveButton,
            SendButton = ReviewSendButton,
            DeleteButton = ReviewDeleteButton,
            CancelButton = ReviewCancelButton,
            SendAllButton = ReviewSendAll,
            StatusText = ReviewStatus,
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Editor.ActualThemeVariantChanged += OnThemeChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Connect();

        // A switch made while this tab was off screen was not seen: catch up on the way back.
        model?.RefreshText();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Editor.ActualThemeVariantChanged -= OnThemeChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Disconnect();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Folding for the open document, or null before a document is bound.</summary>
    internal EditorFoldingController? Folding => bridge?.Folding;

    /// <summary>The editor options this view's toolbar and menus edit.</summary>
    internal EditorSettings Settings => EditorSettings.Current;

    /// <summary>The inline diagnostic dots of the bound document, or null before one is bound.</summary>
    internal EditorDiagnosticAdornments? Diagnostics => bridge?.Diagnostics;

    /// <summary>The end-of-line and tab glyphs of the bound document, or null before one is bound.</summary>
    internal EditorWhitespaceAdornments? Whitespace => bridge?.Whitespace;

    /// <summary>The open completion popup, or null; tests need it to look at what the list is doing.</summary>
    internal AvaloniaEdit.CodeCompletion.CompletionWindow? CompletionPopup => completionWindow;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (attached)
        {
            Connect();
        }
    }

    private void Connect()
    {
        Disconnect();
        model = DataContext as FileDocumentViewModel;
        if (model is null)
        {
            return;
        }

        model.PropertyChanged += OnModelPropertyChanged;


        review?.Bind(model.ReviewController);
        Rebind();
    }

    private void Disconnect()
    {
        DetachEditorLsp();
        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model = null;
        }

        review?.Bind(null);
        bridge?.Dispose();
        bridge = null;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileDocumentViewModel.EditorDocument))
        {
            Rebind();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => bridge?.ApplyTheme();

    /// <summary>
    /// The document's own texts (tab title, open label, notices, preview captions) follow the language. The view holds
    /// the subscription only while it is on screen, so a closed tab is never kept alive by the process-wide table.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => model?.RefreshText();

    private void Rebind()
    {
        DetachEditorLsp();
        bridge?.Dispose();
        bridge = null;
        if (model?.EditorMirror is not { } mirror)
        {
            review?.Refresh();
            return;
        }

        bridge = new ForgeEditorBridge(Editor, model, mirror, model.Path, EditorSettings.Current);
        AttachEditorLsp(model);

        // The folding controller is built before the language session is attached to this view, so its first pass used
        // the structural heuristic; ask again now that the server can answer.
        bridge.Folding.Refresh();
        review?.Refresh();
    }

    /// <summary>Word wrap is per tab; the menu edits the view model that the editor binds to.</summary>
    internal bool WrapLines
    {
        get => model?.WrapLines ?? false;
        set
        {
            if (model is { } current)
            {
                current.WrapLines = value;
            }
        }
    }

    /// <summary>
    /// Builds the editor menu into both hosts. The toolbar button gets its own <see cref="MenuFlyout"/>: opening the
    /// editor's <see cref="ContextMenu"/> on another control throws in Avalonia, which is what used to end the process
    /// when the ellipsis was clicked.
    /// </summary>
    private void InitEditorMenu()
    {
        builder = new EditorMenuBuilder(this);
        contextMenu = new ContextMenu();
        Editor.ContextMenu = contextMenu;

        menuFlyout = new MenuFlyout();
        EditorMenuButton.Flyout = menuFlyout;
        AutomationProperties.SetAutomationId(EditorMenuButton, "file-editor-menu");

        // Rebuilt on every use rather than bound: a checkmark must not lie after the settings page changed an option, and
        // a right click has to refresh before Avalonia opens the menu.
        Editor.AddHandler(InputElement.PointerPressedEvent, OnEditorPointerPressedForMenu, RoutingStrategies.Tunnel);
        RefreshEditorMenu();
    }

    /// <summary>Rebuilds both hosts from the settings as they are now; exposed so a test can do what a click does.</summary>
    internal void RefreshEditorMenu()
    {
        if (builder is null)
        {
            return;
        }

        Fill(contextMenu.Items);
        Fill(menuFlyout.Items);
    }

    private void Fill(ItemCollection items)
    {
        items.Clear();
        foreach (var item in builder!.Build())
        {
            items.Add(item);
        }
    }

    private void OnEditorPointerPressedForMenu(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed)
        {
            RefreshEditorMenu();
        }
    }

    private void OnEditorMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Flyout: { } flyout } target)
        {
            return;
        }

        RefreshEditorMenu();
        flyout.ShowAt(target);
    }

    internal void UndoFromMenu() => Editor.Undo();

    internal void RedoFromMenu() => Editor.Redo();

    internal void CutFromMenu() => Editor.Cut();

    internal void CopyFromMenu() => _ = CopyAsync();

    internal void PasteFromMenu() => Editor.Paste();

    internal void SelectAllFromMenu() => Editor.SelectAll();

    internal void ToggleFoldFromMenu() => Folding?.ToggleAtCaret();

    internal void FoldAllFromMenu() => Folding?.FoldAll();

    internal void UnfoldAllFromMenu() => Folding?.UnfoldAll();

    internal void FindFromMenu() => OpenSearch(replace: false);

    internal void ReplaceFromMenu() => OpenSearch(replace: true);

    private void OpenSearch(bool replace)
    {
        if (Editor.SearchPanel is not { } panel)
        {
            return;
        }

        panel.IsReplaceMode = replace && !Editor.IsReadOnly;
        panel.Open();
        panel.Reactivate();
    }

    internal void ToUpperFromMenu() =>
        AvaloniaEditCommands.ConvertToUppercase.Execute(null, Editor.TextArea);

    internal void ToLowerFromMenu() =>
        AvaloniaEditCommands.ConvertToLowercase.Execute(null, Editor.TextArea);

    internal void ToTitleFromMenu() =>
        AvaloniaEditCommands.ConvertToTitleCase.Execute(null, Editor.TextArea);

    internal void InvertCaseFromMenu() =>
        AvaloniaEditCommands.InvertCase.Execute(null, Editor.TextArea);

    internal void LeadingTabsToSpacesFromMenu() =>
        AvaloniaEditCommands.ConvertLeadingTabsToSpaces.Execute(null, Editor.TextArea);

    internal void LeadingSpacesToTabsFromMenu() =>
        AvaloniaEditCommands.ConvertLeadingSpacesToTabs.Execute(null, Editor.TextArea);

    internal void IndentSelectionFromMenu() =>
        AvaloniaEditCommands.IndentSelection.Execute(null, Editor.TextArea);

    internal void DeleteLineFromMenu() =>
        AvaloniaEditCommands.DeleteLine.Execute(null, Editor.TextArea);

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var copy = e.Key == Key.C && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta
            || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Control;
        if (copy && Editor.TextArea.Selection is SimpleSelection { IsEmpty: false })
        {
            e.Handled = true;
            _ = CopyAsync();
            return;
        }

        if (e.Key == Key.OemOpenBrackets && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            Folding?.ToggleAtCaret();
        }
        else if (e.Key == Key.OemOpenBrackets && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            e.Handled = true;
            Folding?.FoldAll();
        }
        else if (e.Key == Key.OemCloseBrackets && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            e.Handled = true;
            Folding?.UnfoldAll();
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (DataContext is not FileDocumentViewModel model)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            model.ReportCopyError("clipboard unavailable");
            return;
        }

        var text = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Document?.Text ?? string.Empty;
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            model.ReportCopyError(string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            AppLogs.File.ZLogWarning($"copy failed: {exception.Message}");
            model.ReportCopyError(exception.Message);
        }
    }

}
