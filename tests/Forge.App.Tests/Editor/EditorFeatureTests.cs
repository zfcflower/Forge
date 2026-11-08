using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Indentation.CSharp;
using AvaloniaEdit.Rendering;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Drives every editor capability the AvaloniaEdit feature list promises through a real (headless) window: folding,
/// whitespace and end-of-line glyphs, line numbers, scroll-below, current-line highlight, hyperlinks, the inline
/// diagnostic adornment, the library search panel, the transform commands, indentation and the column-selection stand-in
/// for multi-caret editing.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorFeatureTests : IDisposable
{
    private readonly List<string> roots = [];

    public EditorFeatureTests()
    {
        // The editor options are a process-wide projection of the preferences; every test starts and ends on a fresh
        // default instance so a mutation (or a failed assertion) can never leak into the next test.
        EditorSettings.Current = new EditorSettings();
    }

    public void Dispose()
    {
        EditorSettings.Current = new EditorSettings();
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public Task Folding_produces_a_margin_and_the_commands_collapse_and_expand() => Ui(async () =>
    {
        var path = Write("blocks.cs", "class A\n{\n    void M()\n    {\n        var x = 1;\n    }\n}\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        var folding = host.View.Folding;
        Assert.NotNull(folding);
        await WaitUntil(() => folding!.HasFoldings);
        Assert.Contains(host.Editor.TextArea.LeftMargins, margin => margin is FoldingMargin);

        host.Editor.CaretOffset = host.Editor.Document.GetOffset(5, 1);
        Assert.True(folding!.ToggleAtCaret());
        Assert.True(folding.AnyFolded);

        Assert.True(folding.ToggleAtCaret());
        Assert.False(folding.AnyFolded);

        folding.FoldAll();
        Assert.True(folding.AnyFolded);
        folding.UnfoldAll();
        Assert.False(folding.AnyFolded);
        host.Window.Close();
    });

    [Fact]
    public Task Turning_automatic_folding_off_removes_the_margin_and_turning_it_on_restores_it() => Ui(async () =>
    {
        var path = Write("toggle.cs", "class A\n{\n}\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);
        await WaitUntil(() => host.View.Folding!.HasFoldings);
        Assert.Contains(host.Editor.TextArea.LeftMargins, margin => margin is FoldingMargin);

        EditorSettings.Current.AutoFolding = false;
        WorkbenchUi.Settle(host.Window);
        Assert.DoesNotContain(host.Editor.TextArea.LeftMargins, margin => margin is FoldingMargin);

        EditorSettings.Current.AutoFolding = true;
        WorkbenchUi.Settle(host.Window);
        Assert.Contains(host.Editor.TextArea.LeftMargins, margin => margin is FoldingMargin);
        await WaitUntil(() => host.View.Folding!.HasFoldings);
        host.Window.Close();
    });

    [Fact]
    public Task A_document_above_the_folding_cap_says_so_instead_of_folding() => Ui(async () =>
    {
        var lines = new StringBuilder();
        for (var i = 0; i < Forge.Editor.Folding.StructuralFolding.MaxLines + 10; i++)
        {
            lines.Append("line ").Append(i).Append('\n');
        }

        var path = Write("huge.txt", lines.ToString());
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        await WaitUntil(() => model.HasFoldingNotice);
        Assert.False(host.View.Folding!.HasFoldings);
        host.Window.Close();
    });

    [Fact]
    public Task Whitespace_tab_and_end_of_line_glyphs_are_drawn_when_their_options_are_on() => Ui(async () =>
    {
        var path = Write("glyphs.txt", "a b\tc\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);
        var options = host.Editor.Options;

        Assert.False(options.ShowSpaces);
        var whitespace = host.View.Whitespace;
        Assert.NotNull(whitespace);
        Assert.False(whitespace!.ShowTabs);
        Assert.False(whitespace.ShowEndOfLine);
        Assert.Empty(InlineGlyphs(host.Editor));

        EditorSettings.Current.ShowSpaces = true;
        EditorSettings.Current.ShowTabs = true;
        EditorSettings.Current.ShowEndOfLine = true;
        WorkbenchUi.Settle(host.Window);

        Assert.True(options.ShowSpaces);
        Assert.True(whitespace.ShowTabs);
        Assert.True(whitespace.ShowEndOfLine);

        // The tab arrow sits at the tab's own column and the pilcrow after the last character; both are drawn as
        // zero-length elements, so the document text is untouched.
        var glyphs = InlineGlyphs(host.Editor);
        Assert.Equal(2, glyphs.Count);
        Assert.Equal(3, glyphs[0]);
        Assert.True(glyphs[1] > glyphs[0], "the pilcrow follows the last character of the line");
        Assert.Equal("a b\tc\n", host.Editor.Document.Text);
        host.Window.Close();
    });

    [Fact]
    public Task Line_numbers_scroll_below_current_line_and_rulers_follow_the_options() => Ui(async () =>
    {
        var path = Write("options.txt", "alpha\nbeta\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);
        var options = host.Editor.Options;

        Assert.True(host.Editor.ShowLineNumbers);
        Assert.True(options.AllowScrollBelowDocument);
        Assert.True(options.HighlightCurrentLine);
        Assert.True(options.EnableRectangularSelection);
        Assert.False(options.ShowColumnRulers);

        EditorSettings.Current.ColumnRulers = "120, 80 ,80, 999";
        Assert.Equal("120, 80 ,80, 999", EditorSettings.Current.ColumnRulers);
        EditorSettings.Current.ShowLineNumbers = false;
        EditorSettings.Current.ScrollBelowDocument = false;
        EditorSettings.Current.HighlightCurrentLine = false;
        WorkbenchUi.Settle(host.Window);

        Assert.False(host.Editor.ShowLineNumbers);
        Assert.DoesNotContain(host.Editor.TextArea.LeftMargins, margin => margin is LineNumberMargin);
        Assert.False(options.AllowScrollBelowDocument);
        Assert.False(options.HighlightCurrentLine);
        Assert.True(options.ShowColumnRulers);
        Assert.Equal([80, 120], options.ColumnRulerPositions);
        host.Window.Close();
    });

    [Fact]
    public Task Typing_uses_the_indentation_strategy_of_the_language() => Ui(async () =>
    {
        var csharp = Write("indent.cs", "class A\n{\n}\n");
        var text = Write("indent.txt", "alpha\n");
        foreach (var (path, expected) in new[] { (csharp, true), (text, false) })
        {
            var model = FileDocumentViewModel.Create();
            await model.LoadAsync(path, CancellationToken.None);
            var host = Show(model);
            Assert.Equal(expected, host.Editor.TextArea.IndentationStrategy is CSharpIndentationStrategy);
            Assert.True(EditorSettings.Current.TabsToSpaces);
            Assert.Equal(4, host.Editor.Options.IndentationSize);
            host.Window.Close();
        }
    });

    [Fact]
    public Task A_clicked_hyperlink_is_routed_through_the_launcher_and_a_failure_is_reported() => Ui(async () =>
    {
        var path = Write("links.txt", "see https://example.com/docs for details\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);
        Assert.False(model.HasEditorActionNotice);

        var args = new OpenUriRoutedEventArgs(new Uri("https://example.com/docs")) { RoutedEvent = VisualLineLinkText.OpenUriEvent };
        host.Editor.TextArea.TextView.RaiseEvent(args);
        await WaitUntil(() => model.HasEditorActionNotice);

        Assert.True(args.Handled);
        Assert.Contains("https://example.com/docs", model.EditorActionNotice!, StringComparison.Ordinal);
        host.Window.Close();
    });

    [Fact]
    public Task The_hyperlink_option_can_turn_links_off() => Ui(async () =>
    {
        var path = Write("nolinks.txt", "see https://example.com for details\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        EditorSettings.Current.Hyperlinks = false;
        WorkbenchUi.Settle(host.Window);
        Assert.False(host.Editor.Options.EnableHyperlinks);
        Assert.False(host.Editor.Options.EnableEmailHyperlinks);
        Assert.True(host.Editor.Options.RequireControlModifierForHyperlinkClick);
        host.Window.Close();
    });

    [Fact]
    public Task The_selection_tints_rather_than_covers_the_text() => Ui(async () =>
    {
        var path = Write("selection.cs", "public abstract class AdminController" + ((char)10) + "{" + ((char)10) + "}" + ((char)10));
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        var area = host.Editor.TextArea;
        Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(area.SelectionBrush);
        var colour = ((Avalonia.Media.ISolidColorBrush)area.SelectionBrush!).Color;
        Assert.InRange(colour.A, 1, 0x80);
        Assert.True(colour.B > colour.R, "the selection stays blue");
        // The text keeps its own colour: a dark selection with a light foreground is what made the code unreadable.
        Assert.Null(area.SelectionForeground);
        Assert.Null(area.SelectionBorder);

        host.Editor.Select(0, 21);
        WorkbenchUi.Settle(host.Window);
        Assert.Equal("public abstract class", host.Editor.SelectedText);
        host.Window.Close();
    });

    [Fact]
    public Task The_inline_adornment_marks_only_the_lines_with_diagnostics() => Ui(async () =>
    {
        var path = Write("diag.cs", "var a = 1;\nvar b = 2;\nvar c = 3;\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);
        var adornments = host.View.Diagnostics;
        Assert.NotNull(adornments);
        Assert.Equal(-1, adornments!.GetFirstInterestedOffset(0));

        var document = host.Editor.Document;
        adornments.SetDiagnostics(
        [
            new EditorLspDiagnostic(document.GetOffset(2, 1), document.GetOffset(2, 5), Forge.Editor.Lsp.DiagnosticSeverity.Warning, "careful", null, null),
        ]);
        WorkbenchUi.Settle(host.Window);

        var lineTwoEnd = document.GetLineByNumber(2).EndOffset;
        Assert.Equal(lineTwoEnd, adornments.GetFirstInterestedOffset(0));
        Assert.True(adornments.LineHasDiagnostics(lineTwoEnd));
        Assert.False(adornments.LineHasDiagnostics(document.GetLineByNumber(1).EndOffset));
        Assert.NotNull(adornments.ConstructElement(lineTwoEnd));
        Assert.Null(adornments.ConstructElement(lineTwoEnd + 1));
        host.Window.Close();
    });

    [Fact]
    public Task Ctrl_f_opens_the_library_search_panel_and_finds_the_next_match() => Ui(async () =>
    {
        var path = Write("search.txt", "alpha\nbeta\ngamma\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        host.Editor.TextArea.Focus();
        host.Editor.CaretOffset = 0;
        host.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        WorkbenchUi.Settle(host.Window);

        var panel = host.Editor.SearchPanel;
        Assert.NotNull(panel);
        Assert.True(panel!.IsOpened);

        panel.SearchPattern = "beta";
        panel.FindNext(0);
        WorkbenchUi.Settle(host.Window);
        Assert.Equal("beta", host.Editor.SelectedText);

        panel.Close();
        WorkbenchUi.Settle(host.Window);
        Assert.True(panel.IsClosed);
        host.Window.Close();
    });

    [Fact]
    public Task The_transform_commands_change_the_selection_and_the_line_commands_delete_a_line() => Ui(async () =>
    {
        var path = Write("transform.txt", "alpha\nbeta\ngamma\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        host.Editor.Select(0, 5);
        AvaloniaEditCommands.ConvertToUppercase.Execute(null, host.Editor.TextArea);
        WorkbenchUi.Settle(host.Window);
        Assert.StartsWith("ALPHA", host.Editor.Document.Text, StringComparison.Ordinal);

        host.Editor.CaretOffset = host.Editor.Document.GetOffset(2, 1);
        host.Editor.Select(host.Editor.Document.GetOffset(2, 1), 4);
        host.Editor.SelectAll();
        AvaloniaEditCommands.ConvertToLowercase.Execute(null, host.Editor.TextArea);
        WorkbenchUi.Settle(host.Window);
        Assert.Equal("alpha\nbeta\ngamma\n", host.Editor.Document.Text);

        host.Editor.TextArea.ClearSelection();
        host.Editor.CaretOffset = host.Editor.Document.GetOffset(2, 1);
        AvaloniaEditCommands.DeleteLine.Execute(null, host.Editor.TextArea);
        WorkbenchUi.Settle(host.Window);
        Assert.Equal("alpha\ngamma\n", host.Editor.Document.Text);
        host.Window.Close();
    });

    [Fact]
    public Task The_context_menu_offers_every_display_toggle_and_its_checkboxes_drive_the_options() => Ui(async () =>
    {
        var path = Write("menu.txt", "alpha beta\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        var menu = host.Editor.ContextMenu;
        Assert.NotNull(menu);
        // The items are built when the menu opens, so the editor is asked for them the way a right click would.
        menu!.Open(host.Editor);
        WorkbenchUi.Settle(host.Window);
        Assert.True(menu.Items.Count > 0, "the menu was not built");
        foreach (var id in new[]
                 {
                     "editor-menu-display", "editor-menu-folding", "editor-menu-transform", "editor-menu-find",
                     "editor-menu-replace", "editor-menu-column-selection", "editor-toggle-line-numbers", "editor-toggle-wrap",
                     "editor-toggle-spaces", "editor-toggle-tabs", "editor-toggle-eol", "editor-toggle-control",
                     "editor-toggle-current-line", "editor-toggle-scroll-below", "editor-toggle-hyperlinks",
                     "editor-toggle-folding", "editor-fold-toggle", "editor-fold-all", "editor-fold-none",
                     "editor-transform-upper", "editor-transform-lower", "editor-transform-title", "editor-transform-invert",
                     "editor-transform-tabs-to-spaces", "editor-transform-spaces-to-tabs", "editor-transform-indent",
                     "editor-transform-delete-line", "editor-undo", "editor-redo", "editor-cut", "editor-copy",
                     "editor-paste", "editor-select-all",
                 })
        {
            Assert.NotNull(FindMenuItem(menu, id));
        }

        var spaces = FindMenuItem(menu, "editor-toggle-spaces")!;
        Assert.False(spaces.IsChecked);
        spaces.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WorkbenchUi.Settle(host.Window);
        Assert.True(EditorSettings.Current.ShowSpaces);
        Assert.True(host.Editor.Options.ShowSpaces);
        Assert.True(spaces.IsChecked);

        // A second click flips it back: the menu item state follows the settings, never the other way around.
        spaces.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WorkbenchUi.Settle(host.Window);
        Assert.False(EditorSettings.Current.ShowSpaces);
        Assert.False(host.Editor.Options.ShowSpaces);

        spaces.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WorkbenchUi.Settle(host.Window);
        Assert.True(host.Editor.Options.ShowSpaces);

        var wrap = FindMenuItem(menu, "editor-toggle-wrap")!;
        wrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        WorkbenchUi.Settle(host.Window);
        Assert.True(model.WrapLines);
        Assert.True(host.Editor.WordWrap);
        host.Window.Close();
    });

    [Fact]
    public Task The_toolbar_ellipsis_opens_its_own_menu_without_touching_the_editor_one() => Ui(async () =>
    {
        var path = Write("toolbar.txt", "alpha\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        var button = host.View.GetVisualDescendants().OfType<Control>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "file-editor-menu");
        Assert.NotNull(button);
        var flyout = Assert.IsType<MenuFlyout>(((Button)button).Flyout);
        Assert.False(flyout.IsOpen);
        Assert.False(host.Editor.ContextMenu!.IsOpen);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WorkbenchUi.Settle(host.Window);

        Assert.True(flyout.IsOpen);
        Assert.False(host.Editor.ContextMenu.IsOpen);
        Assert.Equal("editor-undo", AutomationProperties.GetAutomationId(Assert.IsAssignableFrom<Control>(flyout.Items[0])));
        flyout.Hide();
        WorkbenchUi.Settle(host.Window);
        Assert.False(flyout.IsOpen);
        host.Window.Close();
    });

    [Fact]
    public Task Settings_changes_reach_an_already_open_editor() => Ui(async () =>
    {
        var path = Write("live.txt", "alpha\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        Assert.False(host.Editor.Options.ShowBoxForControlCharacters);
        EditorSettings.Current.ShowControlCharacters = true;
        EditorSettings.Current.TabsToSpaces = false;
        EditorSettings.Current.IndentationSize = 2;
        WorkbenchUi.Settle(host.Window);

        Assert.True(host.Editor.Options.ShowBoxForControlCharacters);
        Assert.False(host.Editor.Options.ConvertTabsToSpaces);
        Assert.Equal(2, host.Editor.Options.IndentationSize);
        Assert.Equal("\t", host.Editor.Options.IndentationString);
        EditorSettings.Current.TabsToSpaces = true;
        WorkbenchUi.Settle(host.Window);
        Assert.Equal("  ", host.Editor.Options.IndentationString);
        host.Window.Close();
    });

    [Fact]
    public Task The_word_wrap_default_comes_from_the_settings_for_a_new_document() => Ui(async () =>
    {
        EditorSettings.Current.WordWrap = true;
        var path = Write("wrap.txt", "alpha\n");
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        var host = Show(model);

        Assert.True(model.WrapLines);
        Assert.True(host.Editor.WordWrap);
        host.Window.Close();
    });

    /// <summary>The visual columns of the zero-length inline glyphs (end-of-line and tab marks) on the first visual line.</summary>
    private static List<int> InlineGlyphs(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        var columns = new List<int>();
        var line = view.VisualLines.FirstOrDefault(static candidate => candidate.FirstDocumentLine.LineNumber == 1);
        if (line is null)
        {
            return columns;
        }

        foreach (var element in line.Elements)
        {
            if (element.DocumentLength == 0 && element.VisualLength == 1)
            {
                columns.Add(element.VisualColumn);
            }
        }

        return columns;
    }

    private static MenuItem? FindMenuItem(ItemsControl parent, string automationId)
    {
        foreach (var item in parent.Items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            if (AutomationProperties.GetAutomationId(menuItem) == automationId)
            {
                return menuItem;
            }

            if (FindMenuItem(menuItem, automationId) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private sealed record EditorHost(Window Window, TextEditor Editor, FileDocumentView View);

    private static EditorHost Show(FileDocumentViewModel model)
    {
        var view = new FileDocumentView { DataContext = model };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        WorkbenchUi.Settle(window);
        return new EditorHost(window, view.GetVisualDescendants().OfType<TextEditor>().Single(), view);
    }

    private static async Task Ui(Func<Task> body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await SharedUi.Session.Dispatch(async () => { await body(); return 0; }, budget.Token);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20);
        }
    }

    private string Write(string name, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-editor-features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        return path;
    }
}
