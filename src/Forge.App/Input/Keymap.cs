using Avalonia.Input;
using Avalonia.Input.Platform;
using Forge.App.Localization;

namespace Forge.App.Input;

/// <summary>
/// Where a rebindable action runs: the window's key bindings, the editor's key handler or a view's key handler. The scope
/// groups the settings page and decides the conflict group through <see cref="KeymapGesture"/>: the editor and the view
/// actions are pressed in one chain (the file view handles a key while the editor has focus), a window gesture is a
/// separate chain.
/// </summary>
internal enum KeymapScope
{
    Window,
    Editor,
    View,
}

/// <summary>
/// One rebindable keyboard action: a stable id (the contract between the catalog and the code that applies it), the key of
/// its localized display name, the scope it belongs to and its default gesture. The default is what the action does when no
/// override is stored; the catalog is static data, so building it costs nothing.
/// </summary>
internal sealed record KeymapAction(string Id, string DisplayNameKey, KeymapScope Scope, KeyGesture DefaultGesture);

/// <summary>
/// The ids of <see cref="KeymapCatalog"/>. They are the contract between the catalog, the applier and the settings page, so a
/// row can never name an action nothing applies.
/// </summary>
internal static class KeymapActionIds
{
    public const string NewTerminal = "window.newTerminal";
    public const string OpenFilePalette = "window.openFilePalette";
    public const string OpenCommandPalette = "window.openCommandPalette";
    public const string OpenSearchPalette = "window.openSearchPalette";

    public const string EditorUndo = "editor.undo";
    public const string EditorRedo = "editor.redo";
    public const string EditorCut = "editor.cut";
    public const string EditorCopy = "editor.copy";
    public const string EditorPaste = "editor.paste";
    public const string EditorSelectAll = "editor.selectAll";
    public const string EditorToggleFold = "editor.toggleFold";
    public const string EditorFoldAll = "editor.foldAll";
    public const string EditorUnfoldAll = "editor.unfoldAll";
    public const string EditorFind = "editor.find";
    public const string EditorReplace = "editor.replace";
    public const string EditorCodeAction = "editor.codeAction";
    public const string EditorRename = "editor.rename";

    public const string SaveDocument = "view.saveDocument";
    public const string NextUnviewedChange = "view.nextUnviewedChange";
    public const string LspCompletion = "view.lspCompletion";
    public const string LspReferences = "view.lspReferences";
}

/// <summary>
/// Every rebindable action, the single source of truth the applier and the settings page both read. The set is frozen to the
/// global actions only: terminal VT pass-through, in-editor text navigation and panel list context keys (session suggestion
/// lists, palette navigation) are not global actions and are deliberately absent.
/// </summary>
internal static class KeymapCatalog
{
    private static readonly KeymapAction[] All =
    [
        // Window commands.
        new(KeymapActionIds.NewTerminal, KeymapStrings.ActionNewTerminalKey, KeymapScope.Window,
            new KeyGesture(Key.OemTilde, KeyModifiers.Control)),
        new(KeymapActionIds.OpenFilePalette, KeymapStrings.ActionOpenFilePaletteKey, KeymapScope.Window,
            new KeyGesture(Key.P, KeyModifiers.Control)),
        new(KeymapActionIds.OpenCommandPalette, KeymapStrings.ActionOpenCommandPaletteKey, KeymapScope.Window,
            new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new(KeymapActionIds.OpenSearchPalette, KeymapStrings.ActionOpenSearchPaletteKey, KeymapScope.Window,
            new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift)),

        // Editor menu commands: the menu shows these gestures and FileDocumentView runs every one of them from the catalog
        // (undo, redo, cut, copy, paste, select all, find, replace and folding).
        new(KeymapActionIds.EditorUndo, KeymapStrings.ActionEditorUndoKey, KeymapScope.Editor,
            new KeyGesture(Key.Z, KeyModifiers.Control)),
        new(KeymapActionIds.EditorRedo, KeymapStrings.ActionEditorRedoKey, KeymapScope.Editor,
            new KeyGesture(Key.Y, KeyModifiers.Control)),
        new(KeymapActionIds.EditorCut, KeymapStrings.ActionEditorCutKey, KeymapScope.Editor,
            new KeyGesture(Key.X, KeyModifiers.Control)),
        new(KeymapActionIds.EditorCopy, KeymapStrings.ActionEditorCopyKey, KeymapScope.Editor,
            new KeyGesture(Key.C, KeyModifiers.Control)),
        new(KeymapActionIds.EditorPaste, KeymapStrings.ActionEditorPasteKey, KeymapScope.Editor,
            new KeyGesture(Key.V, KeyModifiers.Control)),
        new(KeymapActionIds.EditorSelectAll, KeymapStrings.ActionEditorSelectAllKey, KeymapScope.Editor,
            new KeyGesture(Key.A, KeyModifiers.Control)),
        new(KeymapActionIds.EditorToggleFold, KeymapStrings.ActionEditorToggleFoldKey, KeymapScope.Editor,
            new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift)),
        new(KeymapActionIds.EditorFoldAll, KeymapStrings.ActionEditorFoldAllKey, KeymapScope.Editor,
            new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Alt)),
        new(KeymapActionIds.EditorUnfoldAll, KeymapStrings.ActionEditorUnfoldAllKey, KeymapScope.Editor,
            new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt)),
        new(KeymapActionIds.EditorFind, KeymapStrings.ActionEditorFindKey, KeymapScope.Editor,
            new KeyGesture(Key.F, KeyModifiers.Control)),
        new(KeymapActionIds.EditorReplace, KeymapStrings.ActionEditorReplaceKey, KeymapScope.Editor,
            new KeyGesture(Key.H, KeyModifiers.Control)),
        // Language server actions (W17c): the bulb and F2 are the mouse/entry points, these are the rebindable keys.
        new(KeymapActionIds.EditorCodeAction, KeymapStrings.ActionEditorCodeActionKey, KeymapScope.Editor,
            new KeyGesture(Key.OemPeriod, KeyModifiers.Control)),
        new(KeymapActionIds.EditorRename, KeymapStrings.ActionEditorRenameKey, KeymapScope.Editor,
            new KeyGesture(Key.F2, KeyModifiers.None)),

        // View commands (applied as code key bindings or key handlers, never XAML).
        new(KeymapActionIds.SaveDocument, KeymapStrings.ActionSaveDocumentKey, KeymapScope.View,
            new KeyGesture(Key.S, KeyModifiers.Control)),
        new(KeymapActionIds.NextUnviewedChange, KeymapStrings.ActionNextUnviewedChangeKey, KeymapScope.View,
            new KeyGesture(Key.J, KeyModifiers.None)),
        new(KeymapActionIds.LspCompletion, KeymapStrings.ActionLspCompletionKey, KeymapScope.View,
            new KeyGesture(Key.Space, KeyModifiers.Control)),
        new(KeymapActionIds.LspReferences, KeymapStrings.ActionLspReferencesKey, KeymapScope.View,
            new KeyGesture(Key.F12, KeyModifiers.Shift)),
    ];

    /// <summary>The catalog in display order; a caller may hold the array (it is immutable).</summary>
    public static IReadOnlyList<KeymapAction> Actions => All;

    /// <summary>The action with <paramref name="id"/>, or null when the id is not in the catalog.</summary>
    public static KeymapAction? Find(string? id) =>
        id is null ? null : Array.Find(All, action => action.Id == id);

    /// <summary>The actions of one <see cref="KeymapScope"/>, in catalog order.</summary>
    public static IEnumerable<KeymapAction> InScope(KeymapScope scope)
    {
        foreach (var action in All)
        {
            if (action.Scope == scope)
            {
                yield return action;
            }
        }
    }

    /// <summary>The default gesture of <paramref name="id"/>; an unknown id returns an empty gesture rather than throwing.</summary>
    public static KeyGesture DefaultGesture(string id) => Find(id)?.DefaultGesture ?? new KeyGesture(Key.None);
}

/// <summary>
/// The chain a key press travels: <see cref="KeymapScope.Window"/> gestures are matched by the window's key bindings, while
/// <see cref="KeymapScope.Editor"/> and <see cref="KeymapScope.View"/> gestures are matched by the focused view's own handler
/// first (the editor and the view are one chain: the same key reaches the editor's handler and then the view's).
/// </summary>
internal enum KeymapConflictGroup
{
    Window,
    EditorAndView,
}

/// <summary>
/// Pure gesture helpers: parse a stored override back into a <see cref="KeyGesture"/>, normalize a gesture to the canonical
/// stored form, format it for display, and find a conflict inside the same key chain. All of it is static data in, static
/// data out, so the parsing and conflict rules are provable without a window or a preference store.
/// </summary>
internal static class KeymapGesture
{
    /// <summary>The chain a scope's keys are pressed in; two actions of one group may collide, two groups never do.</summary>
    public static KeymapConflictGroup GroupOf(KeymapScope scope) => scope == KeymapScope.Window
        ? KeymapConflictGroup.Window
        : KeymapConflictGroup.EditorAndView;

    /// <summary>
    /// The display formatter: it keeps Avalonia's common key overrides (OemTilde → <c>`</c>, OemOpenBrackets → <c>[</c>, …)
    /// while leaving letters, digits and F-keys as their enum names, so the setting page shows <c>Ctrl+`</c> and not
    /// <c>Ctrl+OemTilde</c>. The empty override dictionary opts in to the common overrides (a null one means "invariant").
    /// </summary>
    private static readonly KeyGestureFormatInfo DisplayFormat = new(
        new Dictionary<Key, string>(),
        meta: "Meta",
        ctrl: "Ctrl",
        alt: "Alt",
        shift: "Shift");

    /// <summary>
    /// Parses an override gesture. <see cref="KeyGesture.Parse"/> never throws on a missing key (it yields Key.None) and
    /// only throws when an unknown modifier is spelled; both mean "not a gesture" here, so the caller falls back to the
    /// default. A lone modifier (<c>Ctrl</c>) is not a complete gesture either.
    /// </summary>
    public static bool TryParse(string? text, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        KeyGesture parsed;
        try
        {
            parsed = KeyGesture.Parse(text.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }

        if (parsed.Key == Key.None)
        {
            return false;
        }

        gesture = parsed;
        return true;
    }

    /// <summary>The canonical stored form (<c>Ctrl+OemTilde</c>); it is what <see cref="TryParse"/> round-trips.</summary>
    public static string Normalize(KeyGesture gesture) => gesture.ToString();

    /// <summary>The human-friendly form (<c>Ctrl+`</c>, <c>Ctrl+Shift+[</c>), used everywhere a gesture is displayed.</summary>
    public static string Format(KeyGesture gesture) => gesture.ToString("p", DisplayFormat);

    /// <summary>
    /// The id of another action pressed in the same key chain as <paramref name="actionId"/> whose resolved gesture equals
    /// <paramref name="candidate"/>, or null. <paramref name="resolved"/> is the fully resolved map (override or default) for
    /// every action, so the check is against what the keys would actually do, not against the raw default table.
    /// </summary>
    public static string? FindConflict(string actionId, KeyGesture candidate, IReadOnlyDictionary<string, KeyGesture> resolved)
    {
        var action = KeymapCatalog.Find(actionId);
        if (action is null)
        {
            return null;
        }

        var group = GroupOf(action.Scope);
        foreach (var other in KeymapCatalog.Actions)
        {
            if (string.Equals(other.Id, actionId, StringComparison.Ordinal) || GroupOf(other.Scope) != group)
            {
                continue;
            }

            if (resolved.TryGetValue(other.Id, out var otherGesture) && candidate.Equals(otherGesture))
            {
                return other.Id;
            }
        }

        return null;
    }
}
