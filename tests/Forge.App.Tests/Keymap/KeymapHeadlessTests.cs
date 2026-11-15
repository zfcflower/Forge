using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Forge.App.Git;
using Forge.App.Input;
using Forge.App.Preferences;
using Forge.App.Tests.Editor;
using Forge.App.Tests.Git;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The applied keymap (W10e) against real headless windows: the four window gestures fire their commands, the file view's
/// save and fold gestures reach their handlers, the changes view's next-unviewed gesture navigates and its hint names the key,
/// the editor menu shows the catalog gesture, and completion asks the language server only for the bound combination. Each also
/// proves that a rebind swaps the gesture immediately: the new combination fires and the old one is dead.
/// </summary>
[Collection("forge-app-ui")]
public sealed class KeymapHeadlessTests
{
    [Fact]
    public Task The_four_window_gestures_trigger_their_commands_and_a_rebind_swaps_one() => SharedUi.RunAsync(() =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        var window = new Window { Width = 480, Height = 360 };
        try
        {
            var terminal = 0;
            var files = 0;
            var commands = 0;
            var search = 0;

            void Apply()
            {
                window.KeyBindings.Clear();
                foreach (var binding in KeymapApplier.WindowKeyBindings(
                             new RelayCommand(() => terminal++),
                             new RelayCommand(() => files++),
                             new RelayCommand(() => commands++),
                             new RelayCommand(() => search++)))
                {
                    window.KeyBindings.Add(binding);
                }
            }

            Apply();
            window.Show();
            window.Focus();
            SharedUi.Settle(window);

            window.KeyPressQwerty(PhysicalKey.Backquote, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control | RawInputModifiers.Shift);
            SharedUi.Settle(window);
            Assert.Equal(1, terminal);
            Assert.Equal(1, files);
            Assert.Equal(1, commands);
            Assert.Equal(1, search);

            // Rebind the file palette to Ctrl+Shift+O and rebuild the window's collection the way MainWindow does.
            preferences.KeymapOverrides = ["window.openFilePalette|Ctrl+Shift+O"];
            Assert.Equal("Ctrl+Shift+O", KeymapApplier.DisplayGesture(KeymapActionIds.OpenFilePalette));
            Apply();
            Assert.Equal(4, window.KeyBindings.Count);
            SharedUi.Settle(window);

            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            SharedUi.Settle(window);
            Assert.True(files == 1, "the old gesture must be dead after a rebind");

            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control | RawInputModifiers.Shift);
            SharedUi.Settle(window);
            Assert.Equal(2, files);
        }
        finally
        {
            KeymapApplier.Unbind();
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Ctrl_S_saves_and_a_rebind_swaps_the_save_gesture() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var path = Write("save.txt", "alpha\n");
            var model = await TestDocuments.LoadAsync(path);
            host = EditorUi.Show(model);
            model.IsEditing = true;
            WorkbenchUi.Settle(host.Window);

            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;
            host.Window.KeyTextInput("X");
            WorkbenchUi.Settle(host.Window);
            Assert.True(model.IsDirty);

            host.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await WaitUntilAsync(() => !model.IsDirty && !model.IsSaving);
            Assert.Equal("Xalpha\n", await File.ReadAllTextAsync(path));

            // Rebind save to Ctrl+U: the view rebuilds its key binding from the applier change.
            preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
            WorkbenchUi.Settle(host.Window);

            host.Editor.TextArea.Focus();
            host.Window.KeyTextInput("Y");
            WorkbenchUi.Settle(host.Window);
            Assert.True(model.IsDirty);

            host.Window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            SharedUi.Settle(host.Window);
            await Task.Delay(50);
            Assert.True(model.IsDirty, "Ctrl+S must no longer save after the rebind");

            host.Window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control);
            await WaitUntilAsync(() => !model.IsDirty && !model.IsSaving);
            Assert.Equal("XYalpha\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            KeymapApplier.Unbind();
            if (host is not null)
            {
                host.Window.Close();
            }
        }
    });

    [Fact]
    public Task J_selects_the_next_unviewed_change() => SharedUi.RunAsync(async () =>
    {
        var sandbox = GitTestSandbox.Create();
        var window = new Window { Width = 960, Height = 640 };
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            var dir = sandbox.CloneTracked("session");
            File.WriteAllText(Path.Combine(dir, "file.txt"), "v2\n", new UTF8Encoding(false));

            var vm = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
            vm.Attach(new PanelContext("session", dir, null));
            await vm.LoadAsync(CancellationToken.None);
            Assert.Single(vm.Files);

            FileDiffViewModel? focused = null;
            vm.FocusRequested += file => focused = file;

            var view = new ChangesView { DataContext = vm };
            window.Content = view;
            window.Show();
            WorkbenchUi.Settle(window);

            // The footer hint is read from the catalog, so it names the gesture that actually works.
            var hint = view.FindControl<TextBlock>("NextUnviewedGestureHint");
            Assert.NotNull(hint);
            Assert.Equal("J", hint.Text);

            view.Focus();
            window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.None);
            WorkbenchUi.Settle(window);
            Assert.NotNull(focused);

            // A rebind moves both the hint and the handler; the old key is dead.
            preferences.KeymapOverrides = ["view.nextUnviewedChange|Shift+J"];
            WorkbenchUi.Settle(window);
            Assert.Equal("Shift+J", hint.Text);

            focused = null;
            view.Focus();
            window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.None);
            WorkbenchUi.Settle(window);
            Assert.Null(focused);

            window.KeyPressQwerty(PhysicalKey.J, RawInputModifiers.Shift);
            WorkbenchUi.Settle(window);
            Assert.NotNull(focused);
        }
        finally
        {
            KeymapApplier.Unbind();
            window.Close();
            sandbox.Dispose();
        }
    });

    /// <summary>
    /// The editor scope's three folding actions are the ones with a real key handler in <see cref="FileDocumentView"/> (the
    /// other editor menu actions only display their gesture), so they are what proves an editor-scope override reaches a
    /// handler: the default combinations fold and unfold, and after rebinding "fold all" the old combination is dead.
    /// </summary>
    [Fact]
    public Task The_fold_gestures_come_from_the_catalog_and_follow_a_rebind() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var path = Write("blocks.cs", "class A\n{\n    void M()\n    {\n        var x = 1;\n    }\n}\n");
            var model = await TestDocuments.LoadAsync(path);
            host = EditorUi.Show(model);
            var folding = host.View.Folding;
            Assert.NotNull(folding);
            await WaitUntilAsync(() => folding!.HasFoldings);

            host.Editor.TextArea.Focus();
            host.Window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.Control | RawInputModifiers.Alt);
            WorkbenchUi.Settle(host.Window);
            Assert.True(folding!.AnyFolded, "Ctrl+Alt+[ must fold all");

            host.Window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.Control | RawInputModifiers.Alt);
            WorkbenchUi.Settle(host.Window);
            Assert.False(folding.AnyFolded, "Ctrl+Alt+] must unfold all");

            // Ctrl+Shift+[ toggles the block at the caret instead of folding everything.
            host.Editor.CaretOffset = host.Editor.Document.GetOffset(5, 1);
            host.Window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.Control | RawInputModifiers.Shift);
            WorkbenchUi.Settle(host.Window);
            Assert.True(folding.AnyFolded, "Ctrl+Shift+[ must toggle the block at the caret");
            folding.UnfoldAll();

            preferences.KeymapOverrides = ["editor.foldAll|Ctrl+Alt+Z"];
            WorkbenchUi.Settle(host.Window);

            host.Editor.TextArea.Focus();
            host.Window.KeyPressQwerty(PhysicalKey.BracketLeft, RawInputModifiers.Control | RawInputModifiers.Alt);
            WorkbenchUi.Settle(host.Window);
            Assert.False(folding.AnyFolded, "Ctrl+Alt+[ must not fold all after the rebind");

            host.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Alt);
            WorkbenchUi.Settle(host.Window);
            Assert.True(folding.AnyFolded, "Ctrl+Alt+Z must fold all after the rebind");
        }
        finally
        {
            KeymapApplier.Unbind();
            host?.Window.Close();
        }
    });

    /// <summary>
    /// The editor menu's shown gestures come from the catalog, one per editor action, and a rebind has to reach the rebuilt
    /// menu: a menu that keeps showing <c>Ctrl+Z</c> after the user rebound Undo sends the user to the wrong key.
    /// </summary>
    [Fact]
    public Task The_editor_menu_shows_the_catalog_gesture_for_every_editor_action_and_follows_a_rebind() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var model = await TestDocuments.LoadAsync(Write("menu.txt", "alpha\n"));
            host = EditorUi.Show(model);

            var gestures = MenuGestures(host.Editor.ContextMenu?.Items);
            Assert.Equal(11, gestures.Count);
            Assert.Equal(new KeyGesture(Key.Z, KeyModifiers.Control), gestures["editor-undo"]);
            Assert.Equal(new KeyGesture(Key.Y, KeyModifiers.Control), gestures["editor-redo"]);
            Assert.Equal(new KeyGesture(Key.X, KeyModifiers.Control), gestures["editor-cut"]);
            Assert.Equal(new KeyGesture(Key.C, KeyModifiers.Control), gestures["editor-copy"]);
            Assert.Equal(new KeyGesture(Key.V, KeyModifiers.Control), gestures["editor-paste"]);
            Assert.Equal(new KeyGesture(Key.A, KeyModifiers.Control), gestures["editor-select-all"]);
            Assert.Equal(new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift), gestures["editor-fold-toggle"]);
            Assert.Equal(new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Alt), gestures["editor-fold-all"]);
            Assert.Equal(new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt), gestures["editor-fold-none"]);
            Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Control), gestures["editor-menu-find"]);
            Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Control), gestures["editor-menu-replace"]);

            preferences.KeymapOverrides = ["editor.undo|Ctrl+Alt+Z", "editor.find|Ctrl+K"];
            WorkbenchUi.Settle(host.Window);
            host.View.RefreshEditorMenu();

            var rebound = MenuGestures(host.Editor.ContextMenu?.Items);
            Assert.Equal(new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Alt), rebound["editor-undo"]);
            Assert.Equal(new KeyGesture(Key.K, KeyModifiers.Control), rebound["editor-menu-find"]);
            Assert.Equal(new KeyGesture(Key.Y, KeyModifiers.Control), rebound["editor-redo"]);
        }
        finally
        {
            KeymapApplier.Unbind();
            host?.Window.Close();
        }
    });

    /// <summary>
    /// The completion gesture is a view action resolved through the applier on every key press, so rebinding it has to move the
    /// request: the old combination must not ask the server any more, the new one must.
    /// </summary>
    [Fact]
    public Task Rebinding_completion_moves_the_key_that_asks_the_server() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        var root = TestTempFiles.NewDirectory("forge-keymap-lsp-");
        try
        {
            await using var rig = await EditorLspRig.OpenAsync(
                root,
                "foo\n",
                new() { ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar"}]}""" });

            rig.Editor.CaretOffset = 3;
            rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
            await rig.Until(() => rig.CompletionShown);
            rig.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await rig.Until(() => !rig.CompletionShown);

            preferences.KeymapOverrides = ["view.lspCompletion|Ctrl+Alt+Space"];
            WorkbenchUi.Settle(rig.Window);

            var before = rig.Server.Count("textDocument/completion");
            rig.Editor.CaretOffset = 3;
            rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
            await rig.Pump(200);
            Assert.Equal(before, rig.Server.Count("textDocument/completion"));
            Assert.False(rig.CompletionShown);

            rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control | RawInputModifiers.Alt);
            await rig.Until(() => rig.CompletionShown);
            Assert.True(rig.Server.Count("textDocument/completion") > before);
        }
        finally
        {
            KeymapApplier.Unbind();
            TestTempFiles.DeleteTree(root);
        }
    });

    /// <summary>
    /// The eight editor-scope actions AvaloniaEdit also binds itself (undo, redo, cut, copy, paste, select all, find, replace)
    /// are the ones a rebind used to move on the menu label only: the library's own command bindings kept the real keys. The
    /// view now runs them from the catalog, so the rebound combination does the action and the default one is swallowed. Undo
    /// is proven here with a live document; the other seven are the theory below.
    /// </summary>
    [Fact]
    public Task Rebinding_undo_moves_the_key_that_undoes() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var model = await TestDocuments.LoadAsync(Write("undo.txt", "alpha\n"));
            host = EditorUi.Show(model);
            model.IsEditing = true;
            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;
            host.Window.KeyTextInput("X");
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Xalpha\n", host.Editor.Document.Text);

            preferences.KeymapOverrides = ["editor.undo|Ctrl+U"];
            WorkbenchUi.Settle(host.Window);

            host.Window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("alpha\n", host.Editor.Document.Text);

            // The old key has to be dead, not merely empty-handed: type another change and press Ctrl+Z. A live library
            // binding would undo this one, so the text is what proves the key does nothing at all.
            host.Editor.TextArea.Focus();
            host.Window.KeyTextInput("Y");
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Yalpha\n", host.Editor.Document.Text);

            host.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("Yalpha\n", host.Editor.Document.Text);

            // And the rebound key still works after that.
            host.Window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            Assert.Equal("alpha\n", host.Editor.Document.Text);
        }
        finally
        {
            KeymapApplier.Unbind();
            host?.Window.Close();
        }
    });

    /// <summary>
    /// The remaining seven editor actions, each measured by the state it leaves behind: the shipped combination produces it, the
    /// same combination does nothing after the action is rebound, and the rebound combination produces exactly the same state.
    /// That is the whole contract of a rebind — the key moves, the behaviour does not and the old key dies.
    /// </summary>
    [Theory]
    [InlineData(KeymapActionIds.EditorRedo, PhysicalKey.Y)]
    [InlineData(KeymapActionIds.EditorCut, PhysicalKey.X)]
    [InlineData(KeymapActionIds.EditorCopy, PhysicalKey.C)]
    [InlineData(KeymapActionIds.EditorPaste, PhysicalKey.V)]
    [InlineData(KeymapActionIds.EditorSelectAll, PhysicalKey.A)]
    [InlineData(KeymapActionIds.EditorFind, PhysicalKey.F)]
    [InlineData(KeymapActionIds.EditorReplace, PhysicalKey.H)]
    public Task An_editor_action_runs_on_its_rebound_key_and_its_default_key_dies(string actionId, PhysicalKey defaultKey) =>
        SharedUi.RunAsync(async () =>
        {
            const string Rebound = "Ctrl+Alt+L";

            var shipped = KeymapCatalog.Find(actionId)!.DefaultGesture;
            Assert.Equal(defaultKey, Enum.Parse<PhysicalKey>(shipped.Key.ToString()));
            var modifiers = (RawInputModifiers)shipped.KeyModifiers;

            var untouched = await EditorOutcomeAsync(actionId, overrideTo: null, PhysicalKey.None, RawInputModifiers.None);
            var shippedKey = await EditorOutcomeAsync(actionId, overrideTo: null, defaultKey, modifiers);
            var afterRebind = await EditorOutcomeAsync(actionId, Rebound, defaultKey, modifiers);
            var reboundKey = await EditorOutcomeAsync(actionId, Rebound, PhysicalKey.L, RawInputModifiers.Control | RawInputModifiers.Alt);

            Assert.NotEqual(untouched, shippedKey);
            Assert.Equal(untouched, afterRebind);
            Assert.Equal(shippedKey, reboundKey);
        });

    /// <summary>
    /// Prepares the document for <paramref name="actionId"/>, presses <paramref name="pressed"/> (nothing when it is
    /// <see cref="PhysicalKey.None"/>) and reads back the state the action is visible in.
    /// </summary>
    private static async Task<string> EditorOutcomeAsync(
        string actionId, string? overrideTo, PhysicalKey pressed, RawInputModifiers modifiers)
    {
        var preferences = new AppPreferences();
        if (overrideTo is not null)
        {
            preferences.KeymapOverrides = [actionId + "|" + overrideTo];
        }

        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var model = await TestDocuments.LoadAsync(Write("outcome.txt", "alpha\n"));
            host = EditorUi.Show(model);
            model.IsEditing = true;
            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;
            if (host.Window.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync("PRE");
            }

            switch (actionId)
            {
                case KeymapActionIds.EditorRedo:
                    host.Window.KeyTextInput("X");
                    WorkbenchUi.Settle(host.Window);
                    host.Editor.Undo();
                    break;
                case KeymapActionIds.EditorCut:
                case KeymapActionIds.EditorCopy:
                    host.Editor.SelectAll();
                    break;
                case KeymapActionIds.EditorPaste:
                    if (host.Window.Clipboard is { } pasteBoard)
                    {
                        await pasteBoard.SetTextAsync("Z");
                    }

                    break;
            }

            WorkbenchUi.Settle(host.Window);
            if (pressed != PhysicalKey.None)
            {
                host.Window.KeyPressQwerty(pressed, modifiers);
                WorkbenchUi.Settle(host.Window);
            }

            var text = host.Editor.Document.Text;
            var clipboardText = host.Window.Clipboard is { } read ? await read.TryGetTextAsync() ?? string.Empty : string.Empty;
            return actionId switch
            {
                KeymapActionIds.EditorRedo or KeymapActionIds.EditorPaste => text,
                KeymapActionIds.EditorCut => text + "|" + clipboardText,
                KeymapActionIds.EditorCopy => clipboardText,
                KeymapActionIds.EditorSelectAll => host.Editor.SelectionLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => host.Editor.SearchPanel switch
                {
                    null => "no-search-panel",
                    { } panel => (panel.IsOpened ? "open" : "closed") + "/" + panel.IsReplaceMode,
                },
            };
        }
        finally
        {
            KeymapApplier.Unbind();
            host?.Window.Close();
        }
    }

    /// <summary>
    /// The editor-scope handler is a tunnel handler on the whole editor control, and the library's search box lives inside it.
    /// A key pressed there belongs to that box: pasting has to reach the search pattern, not the document the panel sits over.
    /// </summary>
    [Fact]
    public Task A_key_pressed_in_the_editors_search_box_stays_in_the_box() => SharedUi.RunAsync(async () =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        EditorHost? host = null;
        try
        {
            var model = await TestDocuments.LoadAsync(Write("search-box.txt", "alpha\n"));
            host = EditorUi.Show(model);
            model.IsEditing = true;
            host.Editor.TextArea.Focus();
            host.Editor.CaretOffset = 0;

            // Ctrl+F is the catalog's find gesture, so the panel opens through the view's own handler.
            host.Window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);
            var panel = host.Editor.SearchPanel;
            Assert.NotNull(panel);
            Assert.True(panel!.IsOpened);

            var box = panel.GetVisualDescendants().OfType<TextBox>().First();
            await host.Window.Clipboard!.SetTextAsync("beta");
            box.Focus();
            WorkbenchUi.Settle(host.Window);
            Assert.True(box.IsFocused, "the search box has to hold the focus for a key press to be its own");
            host.Window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
            WorkbenchUi.Settle(host.Window);

            Assert.Equal("alpha\n", host.Editor.Document.Text);
            Assert.Equal("beta", panel.SearchPattern);
        }
        finally
        {
            KeymapApplier.Unbind();
            host?.Window.Close();
        }
    });

    /// <summary>Every <see cref="MenuItem"/> in the editor menu (the folding actions sit in a submenu), by automation id.</summary>
    private static Dictionary<string, KeyGesture?> MenuGestures(object? root)
    {
        var gestures = new Dictionary<string, KeyGesture?>(StringComparer.Ordinal);
        if (root is not ItemCollection items)
        {
            return gestures;
        }

        foreach (var item in items)
        {
            if (item is not MenuItem menu)
            {
                continue;
            }

            var id = AutomationProperties.GetAutomationId(menu);
            if (!string.IsNullOrEmpty(id) && menu.InputGesture is not null)
            {
                gestures[id] = menu.InputGesture;
            }

            foreach (var nested in MenuGestures(menu.Items))
            {
                gestures[nested.Key] = nested.Value;
            }
        }

        return gestures;
    }

    private static string Write(string name, string content)
    {
        var root = TestTempFiles.NewDirectory("forge-keymap-headless-");
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(20);
        }
    }
}
