using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

[Collection("forge-app-ui")]
public sealed class EditorLspWindowTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-lsp-window-");
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public Task Ctrl_space_applies_the_selected_completion_as_one_undo_step_and_syncs_the_server() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "foo\n", new() { ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar","additionalTextEdits":[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":0}},"newText":"// added\n"}]}]}""" });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await rig.Until(() => rig.Editor.Document.Text == "foobar\n// added\n");
        await rig.Until(() => !rig.CompletionShown);
        var changes = new StringBuilder();
        for (var i = 0; i < 3 && !(changes.ToString().Contains("foobar", StringComparison.Ordinal) && changes.ToString().Contains("// added", StringComparison.Ordinal)); i++)
        {
            using var change = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/didChange", i + 1));
            changes.Append(change.RootElement.GetProperty("params").GetRawText());
        }
        Assert.Contains("foobar", changes.ToString(), StringComparison.Ordinal);
        Assert.Contains("// added", changes.ToString(), StringComparison.Ordinal);
        ((System.Windows.Input.ICommand)rig.Model.SaveCommand).Execute(null);
        await rig.Until(() => File.ReadAllText(rig.Model.Path) == "foobar\n// added\n");
        rig.Window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await rig.Until(() => rig.Editor.Document.Text == "foo\n");
    });

    [Fact]
    public Task Typing_a_dot_requests_completion_at_the_exact_caret_position() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "foo\n");
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyTextInput(".");
        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/completion"));
        var position = request.RootElement.GetProperty("params").GetProperty("position");
        Assert.Equal(0, position.GetProperty("line").GetInt32());
        Assert.Equal(4, position.GetProperty("character").GetInt32());
        Assert.Equal("foo.\n", rig.Editor.Document.Text);
    });

    [Fact]
    public Task A_completion_answered_after_the_user_kept_typing_is_dropped() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await EditorLspRig.OpenAsync(root, "foo\n",
            new() { ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar"}]}""" },
            new() { ["textDocument/completion"] = hold });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        rig.Window.KeyTextInput("x");
        hold.SetResult();
        await rig.Server.WaitForAsync("textDocument/didChange");
        await rig.Pump(400);
        Assert.False(rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await rig.Pump(100);
        Assert.Equal("foox\n\n", rig.Editor.Document.Text);
    });

    [Fact]
    public Task F12_on_a_same_file_definition_moves_the_caret_without_reloading() => Ui(async () =>
    {
        var path = Path.Combine(root, "Program.cs");
        var uri = LspUri.FromPath(path);
        await using var rig = await EditorLspRig.OpenAsync(root, "class A { }\nclass B : A { }\n", new()
        {
            ["textDocument/definition"] = $$$$"""[{"uri":"{{{{uri}}}}","range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}}}]""",
        });
        var document = rig.Editor.Document;
        rig.Editor.CaretOffset = "class A { }\n".Length + 10;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/definition"));
        Assert.Equal(1, request.RootElement.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
        await rig.Until(() => rig.Editor.CaretOffset == 6);
        Assert.Same(document, rig.Editor.Document);
        Assert.Equal(1, rig.Server.Count("textDocument/didOpen"));
        Assert.False(rig.Model.IsDirty);
    });

    [Fact]
    public Task F12_on_another_file_raises_a_navigation_request_and_leaves_this_document_untouched() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "class Z { }\n");
        await using var rig = await EditorLspRig.OpenAsync(root, "class B { Z z; }\n", new()
        {
            ["textDocument/definition"] = $$$$"""[{"uri":"{{{{LspUri.FromPath(other)}}}}","range":{"start":{"line":2,"character":3},"end":{"line":2,"character":4}}}]""",
        });
        EditorLspDefinitionTarget? target = null;
        rig.Model.DefinitionRequested += (_, t) => target = t;
        rig.Editor.CaretOffset = 10;
        rig.Window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
        await rig.Until(() => target is not null);
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(target!.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, target.Position.Line);
        Assert.Equal(3, target.Position.Character);
        Assert.Equal("class B { Z z; }\n", rig.Editor.Document.Text);
        Assert.Equal(10, rig.Editor.CaretOffset);
    });

    [Fact]
    public Task Arrow_keys_move_the_selection_in_the_completion_list() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "foo" + ((char)10).ToString(), new()
        {
            ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"foobar"},{"label":"foobaz"},{"label":"fooqux"}]}""",
        });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.Control);
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.View?.CompletionPopup is not null);

        var list = rig.View!.CompletionPopup!.CompletionList;
        await rig.Until(() => list.ListBox?.ItemCount == 3);
        var selected = list.ListBox!.SelectedIndex;

        // Moving the selection is only half of it: the selected row has to look selected. The app's workbench list styles
        // blank out ListBoxItem backgrounds globally, which used to leave the popup's selection invisible.
        var background = SelectedItemBackground(list);
        Assert.NotNull(background);
        Assert.InRange(background!.Value.A, 1, 255);

        rig.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        WorkbenchUi.Settle(rig.Window);
        Assert.Equal(selected + 1, list.ListBox.SelectedIndex);

        rig.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
        WorkbenchUi.Settle(rig.Window);
        Assert.Equal(selected, list.ListBox.SelectedIndex);
    });

    [Fact]
    public Task A_discovered_server_is_named_on_the_editor_status_line() => Ui(async () =>
    {
        var registry = LanguageRegistry.Default.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "csharp",
                [".cs", ".csx"],
                [],
                new LanguageServerConfig("/opt/vscode/roslyn-language-server", ["--stdio"], new Dictionary<string, string>()),
                "VS Code"),
        ]);
        var newline = ((char)10).ToString();
        await using var rig = await EditorLspRig.OpenAsync(root, "class A { }" + newline, registry: registry);

        await rig.Until(() => rig.Model.HasLspStatus);
        Assert.Contains("roslyn-language-server", rig.Model.LspStatus!, StringComparison.Ordinal);
        Assert.Contains("VS Code", rig.Model.LspStatus!, StringComparison.Ordinal);
    });

    [Fact]
    public Task Pulled_diagnostics_reach_the_editor_without_a_push_notification() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        const string Capabilities = """
            {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"diagnosticProvider":{"interFileDependencies":true}}}
            """;
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "class A { }" + newline,
            new()
            {
                ["textDocument/diagnostic"] = """{"kind":"full","items":[{"range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}},"severity":1,"message":"pulled","code":"CS9"}]}""",
            },
            capabilities: Capabilities);

        var renderer = rig.Editor.TextArea.TextView.BackgroundRenderers.OfType<EditorLspDiagnosticRenderer>().Single();
        await rig.Server.WaitForAsync("textDocument/diagnostic");
        // The server never publishes anything here: the editor has to ask, and the answer has to reach the view.
        await rig.Until(() => rig.Model.EditorDiagnostics.Items.Count == 1);
        Assert.True(rig.Server.Count("textDocument/diagnostic") > 0, "the editor pulled the diagnostics");

        await rig.Until(() => renderer.FindAt(6).Count == 1);
        Assert.Equal("pulled", renderer.FindAt(6)[0].Message);
        Assert.Equal("CS9", renderer.FindAt(6)[0].Code);
    });

    [Fact]
    public Task A_declared_trigger_character_opens_the_completion_list() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        await using var rig = await EditorLspRig.OpenAsync(root, "foo" + newline, new()
        {
            ["textDocument/completion"] = """{"isIncomplete":false,"items":[{"label":"beta"}]}""",
        });
        rig.Editor.CaretOffset = 3;
        rig.Window.KeyTextInput(":");
        await rig.Server.WaitForAsync("textDocument/completion");
        await rig.Until(() => rig.CompletionShown);
        rig.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await rig.Until(() => !rig.CompletionShown);
    });

    [Fact]
    public Task Folding_ranges_from_the_server_are_used_instead_of_the_heuristic() => Ui(async () =>
    {
        var newline = ((char)10).ToString();
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "// alpha" + newline + "// beta" + newline + "// gamma" + newline,
            new()
            {
                ["textDocument/foldingRange"] = """[{"startLine":0,"endLine":2,"kind":"comment"}]""",
            });
        var view = rig.Window.Content as global::Forge.App.Views.FileDocumentView;
        Assert.NotNull(view);
        Assert.NotNull(view!.Folding);
        await rig.Until(() => view.Folding!.HasFoldings);
        Assert.Contains(
            rig.Editor.TextArea.LeftMargins,
            margin => margin is global::AvaloniaEdit.Folding.FoldingMargin);
        view.Folding!.FoldAll();
        Assert.True(view.Folding.AnyFolded);
        await rig.Server.WaitForAsync("textDocument/foldingRange");
    });

    [Fact]
    public Task Published_diagnostics_are_rendered_and_cleared_on_the_real_text_view() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "class A { }\n");
        var renderer = rig.Editor.TextArea.TextView.BackgroundRenderers.OfType<EditorLspDiagnosticRenderer>().Single();
        await rig.Server.WaitForAsync("textDocument/didOpen");
        rig.Server.Notify("textDocument/publishDiagnostics",
            $$$"""{"uri":"{{{LspUri.FromPath(rig.Path)}}}","diagnostics":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":5}},"severity":1,"message":"boom","code":"E1"}]}""");
        await rig.Until(() => renderer.FindAt(2).Count == 1);
        var diagnostic = Assert.Single(renderer.FindAt(2));
        Assert.Equal("boom", diagnostic.Message);
        Assert.Equal((0, 5), (diagnostic.Start, diagnostic.End));
        Assert.Empty(renderer.FindAt(8));
        rig.Server.Notify("textDocument/publishDiagnostics", $$"""{"uri":"{{LspUri.FromPath(rig.Path)}}","diagnostics":[]}""");
        await rig.Until(() => renderer.FindAt(2).Count == 0);
    });

    /// <summary>The background brush of the selected completion row's content presenter, or null when it has none.</summary>
    private static Avalonia.Media.Color? SelectedItemBackground(AvaloniaEdit.CodeCompletion.CompletionList list)
    {
        if (list.ListBox is not { } box || box.SelectedIndex < 0)
        {
            return null;
        }

        if (box.ContainerFromIndex(box.SelectedIndex) is not Control container)
        {
            return null;
        }

        var presenter = container.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .FirstOrDefault(static candidate => candidate.Name == "PART_ContentPresenter");
        return (presenter?.Background as Avalonia.Media.ISolidColorBrush)?.Color;
    }

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}
