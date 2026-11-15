using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.Localization;
using Forge.App.Tests.Workbench;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// The code action and rename surface of the editor, driven through a real window: the light bulb in the left margin, the
/// menu Ctrl+. opens, the workspace edit applied to the open buffer and to another file on disk, and the F2 rename box seeded
/// by the server.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorCodeActionRenameTests : IDisposable
{
    private const string CodeActionCapabilities = """
        {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"codeActionProvider":{"resolveProvider":true}}}
        """;

    private const string RenameCapabilities = """
        {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"renameProvider":{"prepareProvider":true}}}
        """;

    private readonly string root = TestTempFiles.NewDirectory("forge-code-action-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task A_workspace_edit_skips_a_file_open_in_another_tab_instead_of_writing_through_it() => Ui(async () =>
    {
        // Wave W17c review: a file open in a second tab must be skipped and counted, never written to disk behind that
        // buffer - the tab would show the old text and its next save would silently roll the edit back.
        var bufferPath = Path.Combine(root, "Buffer.cs");
        await File.WriteAllTextAsync(bufferPath, "var a = 1;\n");
        var bufferUri = LspUri.FromPath(bufferPath);
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[" + Action("Rename there", "quickfix", Changes(bufferUri, Edit(0, 0, 3, "let"))) + "]" },
            capabilities: CodeActionCapabilities);

        // Stand in for the shell: this path is already open in another tab.
        rig.Model.FindOpenDocument = path => string.Equals(path, bufferPath, StringComparison.OrdinalIgnoreCase) ? rig.Model : null;

        rig.Editor.CaretOffset = 1;
        await rig.Until(() => rig.View!.CodeActionBulb is { HasActions: true });
        rig.Window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
        await rig.Until(() => rig.View!.CodeActionMenu is not null);
        var item = Assert.IsType<MenuItem>(rig.View!.CodeActionMenu!.Items[0]);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        // The disk file keeps its bytes and the status line says something went unapplied.
        await rig.Until(() => rig.Model.LspStatus is { Length: > 0 });
        Assert.Equal("var a = 1;\n", await File.ReadAllTextAsync(bufferPath));
    });

    [Fact]
    public Task Ctrl_period_opens_the_actions_the_bulb_announced_and_applies_the_edit_to_the_buffer() => Ui(async () =>
    {
        var uri = LspUri.FromPath(Path.Combine(root, "Program.cs"));
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[" + Action("Use let", "quickfix", Changes(uri, Edit(0, 0, 3, "let"))) + "]" },
            capabilities: CodeActionCapabilities);

        rig.Editor.CaretOffset = 1;
        await rig.Until(() => rig.View!.CodeActionBulb is { HasActions: true });

        rig.Window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
        await rig.Until(() => rig.View!.CodeActionMenu is not null);
        var action = Assert.Single(rig.View!.AvailableCodeActions);
        Assert.Equal("Use let", action.Title);
        Assert.Equal("quickfix", action.Kind);

        var item = Assert.IsType<MenuItem>(rig.View!.CodeActionMenu!.Items[0]);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await rig.Until(() => rig.Editor.Document.Text == "let a = 1;\n");

        var applied = Loc.Instance.Format(InspectorStrings.EditorLspEditApplied, TextFormat.Number(1), TextFormat.Number(1));
        await rig.Until(() => rig.Model.LspStatus == applied);
        Assert.Null(rig.View!.CodeActionMenu);
    });

    [Fact]
    public Task Clicking_the_bulb_in_the_margin_opens_the_list() => Ui(async () =>
    {
        var uri = LspUri.FromPath(Path.Combine(root, "Program.cs"));
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[" + Action("Use let", "quickfix", Changes(uri, Edit(0, 0, 3, "let"))) + "]" },
            capabilities: CodeActionCapabilities);

        rig.Editor.CaretOffset = 1;
        await rig.Until(() => rig.View!.CodeActionBulb is { HasActions: true });
        WorkbenchUi.Settle(rig.Window);
        var margin = rig.View!.CodeActionBulb!;
        var center = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, Math.Min(margin.Bounds.Height / 2, 20)), rig.Window);
        Assert.NotNull(center);

        rig.Window.MouseDown(center.Value, MouseButton.Left);
        rig.Window.MouseUp(center.Value, MouseButton.Left);

        await rig.Until(() => rig.View!.CodeActionMenu is not null);
        Assert.Equal("Use let", Assert.Single(rig.View!.AvailableCodeActions).Title);
    });

    [Fact]
    public Task A_workspace_edit_reaching_another_file_writes_that_file_to_disk() => Ui(async () =>
    {
        var program = Path.Combine(root, "Program.cs");
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "alpha\n");
        var edit = new ChangesBuilder()
            .File(LspUri.FromPath(program), Edit(0, 0, 3, "let"))
            .File(LspUri.FromPath(other), Edit(0, 0, 5, "beta"))
            .Build();
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[" + Action("Rename the world", "refactor", edit) + "]" },
            capabilities: CodeActionCapabilities);

        rig.Editor.CaretOffset = 1;
        await rig.Until(() => rig.View!.CodeActionBulb is { HasActions: true });
        var action = Assert.Single(rig.View!.AvailableCodeActions);
        var result = await rig.Model.ApplyCodeActionAsync(
            action,
            rig.View!.CodeActionVersion,
            edits => ApplyToBuffer(rig, edits),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Files);
        Assert.Equal(2, result.Edits);
        Assert.Equal(1, result.OtherFiles);
        Assert.Equal(0, result.Dropped);
        Assert.Equal("let a = 1;\n", rig.Editor.Document.Text);
        Assert.Equal("beta\n", await File.ReadAllTextAsync(other));
    });

    [Fact]
    public Task F2_seeds_the_box_with_the_server_placeholder_and_applies_the_rename() => Ui(async () =>
    {
        var program = Path.Combine(root, "Program.cs");
        var other = Path.Combine(root, "Other.cs");
        await File.WriteAllTextAsync(other, "alpha\n");
        var edit = new ChangesBuilder()
            .File(LspUri.FromPath(program), Edit(0, 0, 5, "beta"))
            .File(LspUri.FromPath(other), Edit(0, 0, 5, "beta"))
            .Build();
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "alpha = 1;\n",
            new()
            {
                ["textDocument/prepareRename"] = "{\"range\":" + Range(0, 0, 5) + ",\"placeholder\":\"alpha\"}",
                ["textDocument/rename"] = edit,
            },
            capabilities: RenameCapabilities);

        rig.Editor.CaretOffset = 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        await rig.Until(() => rig.View!.RenamePromptOpen);
        Assert.Equal("alpha", rig.View!.RenamePromptText);

        using (var request = System.Text.Json.JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/prepareRename")))
        {
            Assert.Equal(0, request.RootElement.GetProperty("params").GetProperty("position").GetProperty("line").GetInt32());
            Assert.Equal(2, request.RootElement.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32());
        }

        rig.View!.SubmitRenameForTest("beta");
        await rig.Until(() => rig.Editor.Document.Text == "beta = 1;\n");
        await rig.Until(() => File.ReadAllText(other) == "beta\n");
        using var rename = System.Text.Json.JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/rename"));
        Assert.Equal("beta", rename.RootElement.GetProperty("params").GetProperty("newName").GetString());
    });

    [Fact]
    public Task A_server_that_cannot_rename_has_no_entry_and_never_opens_a_box() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "alpha = 1;\n", new() { ["textDocument/rename"] = "null" });
        var unsupported = new LocText(
            InspectorStrings.EditorLspUnsupported, new LocText(InspectorStrings.EditorLspFeatureRename)).Resolve();

        rig.Editor.CaretOffset = 2;
        rig.Window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        await rig.Until(() => rig.Model.LspStatus == unsupported);

        Assert.False(rig.View!.RenamePromptOpen);
        Assert.Equal(0, rig.Server.Count("textDocument/prepareRename"));
        Assert.Equal(0, rig.Server.Count("textDocument/rename"));
    });

    [Fact]
    public Task An_empty_answer_leaves_the_bulb_hidden_and_Ctrl_period_says_so() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[]" },
            capabilities: CodeActionCapabilities);

        rig.Editor.CaretOffset = 1;
        await rig.Server.WaitForAsync("textDocument/codeAction");
        await rig.Pump(100);
        Assert.False(rig.View!.CodeActionBulb!.HasActions);

        rig.Window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
        var none = Loc.Instance[InspectorStrings.EditorLspCodeActionNone];
        await rig.Until(() => rig.Model.LspStatus == none);
        Assert.Null(rig.View!.CodeActionMenu);
    });

    [Fact]
    public Task An_edit_that_no_longer_fits_the_document_is_reported_instead_of_applied() => Ui(async () =>
    {
        var uri = LspUri.FromPath(Path.Combine(root, "Program.cs"));
        // Line 9 does not exist in the document: the mapping refuses the file, so nothing is written and the status line says
        // how many edits were dropped.
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "var a = 1;\n",
            new() { ["textDocument/codeAction"] = "[" + Action("Impossible", "quickfix", Changes(uri, Edit(9, 0, 3, "x"))) + "]" },
            capabilities: CodeActionCapabilities);

        rig.Editor.CaretOffset = 1;
        await rig.Until(() => rig.View!.CodeActionBulb is { HasActions: true });
        rig.Window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
        await rig.Until(() => rig.View!.CodeActionMenu is not null);
        Assert.IsType<MenuItem>(rig.View!.CodeActionMenu!.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        var partial = Loc.Instance.Format(InspectorStrings.EditorLspEditPartial, TextFormat.Number(1));
        await rig.Until(() => rig.Model.LspStatus == partial);
        Assert.Equal("var a = 1;\n", rig.Editor.Document.Text);
    });

    private static string Position(int line, int character) =>
        "{\"line\":" + line.ToString(CultureInfo.InvariantCulture) + ",\"character\":" +
        character.ToString(CultureInfo.InvariantCulture) + "}";

    private static string Range(int line, int start, int end) =>
        "{\"start\":" + Position(line, start) + ",\"end\":" + Position(line, end) + "}";

    private static string Edit(int line, int start, int end, string text) =>
        "{\"range\":" + Range(line, start, end) + ",\"newText\":\"" + text + "\"}";

    private static string Changes(string uri, params string[] edits) =>
        "{\"changes\":{\"" + uri + "\":[" + string.Join(",", edits) + "]}}";

    private static string Action(string title, string kind, string edit) =>
        "{\"title\":\"" + title + "\",\"kind\":\"" + kind + "\",\"isPreferred\":true,\"edit\":" + edit + "}";

    /// <summary>The open buffer's half of the edit, the same call the view makes from the menu handler.</summary>
    private static void ApplyToBuffer(EditorLspRig rig, IReadOnlyList<Forge.App.Editing.EditorLspEdit> edits)
    {
        Assert.True(Forge.App.Editing.EditorWorkspaceEditApplier.TryApply(rig.Editor.Document, edits));
    }

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

    /// <summary>Builds the <c>changes</c> map of a workspace edit with one entry per file.</summary>
    private sealed class ChangesBuilder
    {
        private readonly List<string> entries = [];

        public ChangesBuilder File(string uri, params string[] edits)
        {
            entries.Add("\"" + uri + "\":[" + string.Join(",", edits) + "]");
            return this;
        }

        public string Build() => "{\"changes\":{" + string.Join(",", entries) + "}}";
    }
}
