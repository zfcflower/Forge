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
using Forge.App.Input;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Views;

public partial class FileDocumentView : UserControl
{
    /// <summary>
    /// The combinations AvaloniaEdit binds to its own commands (its copy, paste, undo, find, … handlers). They are the keys
    /// a rebind has to kill: once an action moves off one of them, the library would otherwise still do the action, or the
    /// replacement key would sit beside a live old one.
    /// </summary>
    private static readonly KeyGesture[] EditorBuiltInGestures =
    [
        new(Key.Z, KeyModifiers.Control),
        new(Key.Y, KeyModifiers.Control),
        new(Key.X, KeyModifiers.Control),
        new(Key.C, KeyModifiers.Control),
        new(Key.V, KeyModifiers.Control),
        new(Key.A, KeyModifiers.Control),
        new(Key.F, KeyModifiers.Control),
        new(Key.H, KeyModifiers.Control),
    ];

    private FileDocumentViewModel? model;
    private ForgeEditorBridge? bridge;
    private EditorMenuBuilder? builder;
    private ContextMenu contextMenu = null!;
    private MenuFlyout menuFlyout = null!;
    private ReviewOverlay? review;
    private bool attached;

    /// <summary>The thread this view was attached on; a language notification from another one is not ours.</summary>
    private int attachedThread;

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
        attachedThread = Environment.CurrentManagedThreadId;
        Editor.ActualThemeVariantChanged += OnThemeChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        KeymapApplier.Changed += OnKeymapChanged;
        Connect();

        // A switch made while this tab was off screen was not seen: catch up on the way back.
        model?.RefreshText();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Editor.ActualThemeVariantChanged -= OnThemeChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        KeymapApplier.Changed -= OnKeymapChanged;
        Disconnect();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Folding for the open document, or null before a document is bound.</summary>
    internal EditorFoldingController? Folding => bridge?.Folding;

    /// <summary>The review overlay of this view; its own language handler can be raised, which the view does not forward.</summary>
    internal ReviewOverlay? Review => review;

    /// <summary>The editor options this view's toolbar and menus edit.</summary>
    internal EditorSettings Settings => EditorSettings.Current;

    /// <summary>The inline diagnostic dots of the bound document, or null before one is bound.</summary>
    internal EditorDiagnosticAdornments? Diagnostics => bridge?.Diagnostics;

    /// <summary>The end-of-line and tab glyphs of the bound document, or null before one is bound.</summary>
    internal EditorWhitespaceAdornments? Whitespace => bridge?.Whitespace;

    /// <summary>The open completion popup, or null; so the list's state can be inspected.</summary>
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
        ApplyKeymap();
    }

    private void Disconnect()
    {
        KeyBindings.Clear();
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

    /// <summary>Rebuilds the save key binding from the catalog and the live overrides; the save command is lazy but never null.</summary>
    private void ApplyKeymap()
    {
        KeyBindings.Clear();
        if (model is not null)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = KeymapApplier.Gesture(KeymapActionIds.SaveDocument),
                Command = model.SaveCommand,
            });
        }
    }

    private void OnKeymapChanged() => ApplyKeymap();

    /// <summary>
    /// The document's own texts (tab title, open label, notices, preview captions) follow the language. The view holds
    /// the subscription only while it is on screen, so a closed tab is never kept alive by the process-wide table.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A view of a window whose session is gone still sits in the subscriber list of the process-wide
        // table; refreshing the document's tab title and notices from another thread would raise them into bindings that
        // live on the thread that attached this view.
        if (Environment.CurrentManagedThreadId == attachedThread)
        {
            model?.RefreshText();
        }
    }

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

    /// <summary>Rebuilds both hosts from the settings as they are now; exposed so a click's work can be replayed.</summary>
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
        // The search box lives inside this control and is a text input of its own: a key pressed there (paste, undo, select
        // all) belongs to the box, not to the document the panel sits over.
        if (e.Source is TextBox)
        {
            return;
        }

        if (RunCatalogAction(e))
        {
            return;
        }

        // AvaloniaEdit's own combinations stay wired to its command bindings, and this tunnel handler runs before them. The
        // action branches above would have taken a key the catalog still owns, so a key reaching this point is a library
        // default whose action the catalog has moved elsewhere: swallow it, or the old combination keeps working next to
        // the new one and the settings page is a lie.
        foreach (var builtIn in EditorBuiltInGestures)
        {
            if (builtIn.Matches(e))
            {
                e.Handled = true;
                return;
            }
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorToggleFold).Matches(e))
        {
            e.Handled = true;
            Folding?.ToggleAtCaret();
        }
        else if (KeymapApplier.Gesture(KeymapActionIds.EditorFoldAll).Matches(e))
        {
            e.Handled = true;
            Folding?.FoldAll();
        }
        else if (KeymapApplier.Gesture(KeymapActionIds.EditorUnfoldAll).Matches(e))
        {
            e.Handled = true;
            Folding?.UnfoldAll();
        }
    }

    /// <summary>
    /// Runs one of the eight editor-scope actions the catalog owns when the pressed key is the gesture it currently
    /// resolves to, through the same <c>*FromMenu</c> method the menu item calls. Copy keeps the view's own clipboard
    /// write (it reports a failure into the document instead of losing an exception); with nothing selected it hands the
    /// key back, which is what this view did before the catalog took the key.
    /// </summary>
    private bool RunCatalogAction(KeyEventArgs e)
    {
        if (KeymapApplier.Gesture(KeymapActionIds.EditorUndo).Matches(e))
        {
            e.Handled = true;
            UndoFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorRedo).Matches(e))
        {
            e.Handled = true;
            RedoFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorCut).Matches(e))
        {
            e.Handled = true;
            CutFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorCopy).Matches(e))
        {
            if (Editor.TextArea.Selection is SimpleSelection { IsEmpty: false })
            {
                e.Handled = true;
                CopyFromMenu();
            }

            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorPaste).Matches(e))
        {
            e.Handled = true;
            PasteFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorSelectAll).Matches(e))
        {
            e.Handled = true;
            SelectAllFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorFind).Matches(e))
        {
            e.Handled = true;
            FindFromMenu();
            return true;
        }

        if (KeymapApplier.Gesture(KeymapActionIds.EditorReplace).Matches(e))
        {
            e.Handled = true;
            ReplaceFromMenu();
            return true;
        }

        return false;
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
