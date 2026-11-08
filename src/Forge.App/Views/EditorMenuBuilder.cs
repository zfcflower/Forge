using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Builds the editor menu. One builder feeds two hosts — the editor's <see cref="ContextMenu"/> for right-click and the
/// toolbar button's <see cref="MenuFlyout"/> — because an Avalonia <see cref="ContextMenu"/> can only be opened on the
/// control it is attached to (<c>ContextMenu.Open</c> throws otherwise), so the toolbar cannot reuse the editor's menu.
/// <para>Items carry no bindings on purpose: every host gets its own instances, the state is read when the menu is built
/// (so a menu never shows a stale checkmark) and written back on click. That is also Native AOT safe — a string-path
/// binding would need reflection.</para>
/// </summary>
internal sealed class EditorMenuBuilder(FileDocumentView view)
{
    /// <summary>A fresh item tree; a control cannot live in two menus, so each host asks for its own.</summary>
    public IReadOnlyList<object> Build()
    {
        var settings = EditorSettings.Current;
        var items = new List<object>(24);
        items.Add(Command("editor-undo", EditorStrings.MenuUndo, new KeyGesture(Key.Z, KeyModifiers.Control), view.UndoFromMenu));
        items.Add(Command("editor-redo", EditorStrings.MenuRedo, new KeyGesture(Key.Y, KeyModifiers.Control), view.RedoFromMenu));
        items.Add(new Separator());
        items.Add(Command("editor-cut", EditorStrings.MenuCut, new KeyGesture(Key.X, KeyModifiers.Control), view.CutFromMenu));
        items.Add(Command("editor-copy", EditorStrings.MenuCopy, new KeyGesture(Key.C, KeyModifiers.Control), view.CopyFromMenu));
        items.Add(Command("editor-paste", EditorStrings.MenuPaste, new KeyGesture(Key.V, KeyModifiers.Control), view.PasteFromMenu));
        items.Add(Command("editor-select-all", EditorStrings.MenuSelectAll, new KeyGesture(Key.A, KeyModifiers.Control), view.SelectAllFromMenu));
        items.Add(new Separator());

        var display = Submenu("editor-menu-display", EditorStrings.MenuDisplay);
        display.Items.Add(Toggle("editor-toggle-line-numbers", EditorStrings.ShowLineNumbers,
            () => settings.ShowLineNumbers, value => settings.ShowLineNumbers = value));
        display.Items.Add(Toggle("editor-toggle-wrap", EditorStrings.WordWrap,
            () => view.WrapLines, value => view.WrapLines = value));
        display.Items.Add(Toggle("editor-toggle-spaces", EditorStrings.ShowSpaces,
            () => settings.ShowSpaces, value => settings.ShowSpaces = value));
        display.Items.Add(Toggle("editor-toggle-tabs", EditorStrings.ShowTabs,
            () => settings.ShowTabs, value => settings.ShowTabs = value));
        display.Items.Add(Toggle("editor-toggle-eol", EditorStrings.ShowEndOfLine,
            () => settings.ShowEndOfLine, value => settings.ShowEndOfLine = value));
        display.Items.Add(Toggle("editor-toggle-control", EditorStrings.ShowControlCharacters,
            () => settings.ShowControlCharacters, value => settings.ShowControlCharacters = value));
        display.Items.Add(Toggle("editor-toggle-current-line", EditorStrings.HighlightCurrentLine,
            () => settings.HighlightCurrentLine, value => settings.HighlightCurrentLine = value));
        display.Items.Add(Toggle("editor-toggle-scroll-below", EditorStrings.ScrollBelowDocument,
            () => settings.ScrollBelowDocument, value => settings.ScrollBelowDocument = value));
        display.Items.Add(Toggle("editor-toggle-hyperlinks", EditorStrings.Hyperlinks,
            () => settings.Hyperlinks, value => settings.Hyperlinks = value));
        display.Items.Add(Toggle("editor-toggle-folding", EditorStrings.AutoFolding,
            () => settings.AutoFolding, value => settings.AutoFolding = value));
        items.Add(display);

        var folding = Submenu("editor-menu-folding", EditorStrings.MenuFolding);
        folding.Items.Add(Command("editor-fold-toggle", EditorStrings.MenuToggleFold,
            new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift), view.ToggleFoldFromMenu));
        folding.Items.Add(Command("editor-fold-all", EditorStrings.MenuFoldAll,
            new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Alt), view.FoldAllFromMenu));
        folding.Items.Add(Command("editor-fold-none", EditorStrings.MenuUnfoldAll,
            new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt), view.UnfoldAllFromMenu));
        items.Add(folding);

        var transform = Submenu("editor-menu-transform", EditorStrings.MenuCommands);
        transform.Items.Add(Item("editor-transform-upper", Text(EditorStrings.MenuToUpper), view.ToUpperFromMenu));
        transform.Items.Add(Item("editor-transform-lower", Text(EditorStrings.MenuToLower), view.ToLowerFromMenu));
        transform.Items.Add(Item("editor-transform-title", Text(EditorStrings.MenuToTitle), view.ToTitleFromMenu));
        transform.Items.Add(Item("editor-transform-invert", Text(EditorStrings.MenuInvertCase), view.InvertCaseFromMenu));
        transform.Items.Add(new Separator());
        transform.Items.Add(Item("editor-transform-tabs-to-spaces", Text(EditorStrings.MenuLeadingTabsToSpaces), view.LeadingTabsToSpacesFromMenu));
        transform.Items.Add(Item("editor-transform-spaces-to-tabs", Text(EditorStrings.MenuLeadingSpacesToTabs), view.LeadingSpacesToTabsFromMenu));
        transform.Items.Add(Item("editor-transform-indent", Text(EditorStrings.MenuIndentSelection), view.IndentSelectionFromMenu));
        transform.Items.Add(Item("editor-transform-delete-line", Text(EditorStrings.MenuDeleteLine), view.DeleteLineFromMenu));
        items.Add(transform);

        items.Add(new Separator());
        items.Add(Command("editor-menu-find", EditorStrings.MenuFind, new KeyGesture(Key.F, KeyModifiers.Control), view.FindFromMenu));
        items.Add(Command("editor-menu-replace", EditorStrings.MenuReplace, new KeyGesture(Key.H, KeyModifiers.Control), view.ReplaceFromMenu));
        items.Add(new Separator());
        var columnSelection = Item("editor-menu-column-selection", Text(EditorStrings.MenuColumnSelection), null);
        columnSelection.IsEnabled = false;
        items.Add(columnSelection);
        return items;
    }

    private static string Text(string key) => Loc.Instance[key];

    private static MenuItem Item(string automationId, string header, Action? action)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetAutomationId(item, automationId);
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    private static MenuItem Command(string automationId, string headerKey, KeyGesture? gesture, Action action)
    {
        var item = Item(automationId, Text(headerKey), action);
        item.InputGesture = gesture;
        return item;
    }

    private static MenuItem Submenu(string automationId, string headerKey) =>
        Item(automationId, Text(headerKey), null);

    private static MenuItem Toggle(string automationId, string headerKey, Func<bool> read, Action<bool> write)
    {
        var item = new MenuItem
        {
            Header = Text(headerKey),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = read(),
        };

        AutomationProperties.SetAutomationId(item, automationId);
        // The item's own IsChecked flips before this handler runs, so the new value is derived from the settings instead
        // of from the control, and the control is then re-read from the settings to stay authoritative.
        item.Click += (_, _) =>
        {
            write(!read());
            item.IsChecked = read();
        };
        return item;
    }
}
