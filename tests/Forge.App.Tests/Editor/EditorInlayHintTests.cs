using System.Globalization;
using Avalonia.Headless;
using Avalonia.Input;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Inlay hints in a real editor: pulled on attach and after edits (debounced like the diagnostic pull), bucketed per line so
/// only the drawn lines are read, resolved when the server withholds a label, and switchable off.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorInlayHintTests : IDisposable
{
    private const string Method = "textDocument/inlayHint";

    private const string InlayCapabilities = """
        {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"inlayHintProvider":true}}
        """;

    private const string ResolvingCapabilities = """
        {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"inlayHintProvider":{"resolveProvider":true}}}
        """;

    private readonly string root = TestTempFiles.NewDirectory("forge-inlay-hints-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task Hints_are_pulled_on_attach_and_bucketed_per_line() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "Foo(a, b);\nbar();\n",
            new()
            {
                [Method] = "[" + Hint(0, 6, ": int", kind: 1) + "," + Hint(1, 3, ": string", kind: 1) + "]",
            },
            capabilities: InlayCapabilities);

        await rig.Until(() => rig.View!.InlayHintsView!.Index.HintCount == 2);
        var index = rig.View!.InlayHintsView!.Index;
        var first = Assert.Single(index.GetLine(0).ToArray());
        Assert.Equal(": int", first.Label);
        Assert.Equal(6, first.Offset);
        Assert.Equal(InlayHintKind.Type, first.Kind);
        var second = Assert.Single(index.GetLine(1).ToArray());
        Assert.Equal(": string", second.Label);
        Assert.Equal("Foo(a, b);\n".Length + 3, second.Offset);
        Assert.True(index.Version >= 0);

        Assert.Contains(rig.Editor.TextArea.TextView.ElementGenerators, generator => generator is EditorInlayHints);
        using var request = System.Text.Json.JsonDocument.Parse(await rig.Server.WaitForAsync(Method));
        var range = request.RootElement.GetProperty("params").GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
    });

    [Fact]
    public Task Two_quick_edits_collapse_into_one_debounced_request() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "Foo(a);\n",
            new() { [Method] = "[" + Hint(0, 4, ": int", kind: 1) + "]" },
            capabilities: InlayCapabilities);

        // Let the second pull after opening (the catch-up) pass, so the counts below only move because of the edits.
        await rig.Until(() => rig.View!.InlayHintsView!.Index.HintCount == 1);
        await rig.Pump(2_700);
        var before = rig.Server.Count(Method);

        rig.Editor.CaretOffset = rig.Editor.Document.TextLength;
        rig.Window.KeyTextInput("x");
        rig.Window.KeyTextInput("y");
        await rig.Until(() => rig.Server.Count(Method) >= before + 1);
        await rig.Pump(700);

        Assert.Equal(before + 1, rig.Server.Count(Method));
    });

    [Fact]
    public Task A_workspace_inlay_hint_refresh_re_asks_the_server() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "Foo(a);\n",
            new() { [Method] = "[" + Hint(0, 4, ": int", kind: 1) + "]" },
            capabilities: InlayCapabilities);
        await rig.Until(() => rig.View!.InlayHintsView!.Index.HintCount == 1);
        await rig.Pump(2_700);
        var before = rig.Server.Count(Method);

        rig.Server.Request("workspace/inlayHint/refresh");

        await rig.Until(() => rig.Server.Count(Method) >= before + 1);
    });

    [Fact]
    public Task A_hint_with_a_withheld_label_is_resolved_before_it_is_drawn() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "Foo(a);\n",
            new()
            {
                [Method] = "[{\"position\":" + Position(0, 4) + ",\"paddingRight\":true,\"data\":{\"id\":1}}]",
                ["inlayHint/resolve"] = "{\"position\":" + Position(0, 4) + ",\"label\":\"a:\",\"paddingRight\":true}",
            },
            capabilities: ResolvingCapabilities);

        await rig.Until(() => rig.View!.InlayHintsView!.Index.HintCount == 1);
        var hint = Assert.Single(rig.View!.InlayHintsView!.Index.GetLine(0).ToArray());
        Assert.Equal("a:", hint.Label);
        Assert.True(hint.PaddingRight);
        await rig.Server.WaitForAsync("inlayHint/resolve");
    });

    [Fact]
    public Task Turning_the_setting_off_hides_the_hints_without_dropping_the_answer() => Ui(async () =>
    {
        var settings = EditorSettings.Current;
        var original = settings.ShowInlayHints;
        try
        {
            settings.ShowInlayHints = false;
            await using var rig = await EditorLspRig.OpenAsync(
                root,
                "Foo(a);\n",
                new() { [Method] = "[" + Hint(0, 4, ": int", kind: 1) + "]" },
                capabilities: InlayCapabilities);

            await rig.Until(() => rig.View!.InlayHintsView!.Index.HintCount == 1);
            Assert.False(rig.View!.InlayHintsView!.Enabled);

            settings.ShowInlayHints = true;
            await rig.Until(() => rig.View!.InlayHintsView!.Enabled);
            Assert.Equal(1, rig.View!.InlayHintsView!.Index.HintCount);
        }
        finally
        {
            settings.ShowInlayHints = original;
        }
    });

    private static string Position(int line, int character) =>
        "{\"line\":" + line.ToString(CultureInfo.InvariantCulture) + ",\"character\":" +
        character.ToString(CultureInfo.InvariantCulture) + "}";

    private static string Hint(int line, int character, string label, int kind) =>
        "{\"position\":" + Position(line, character) + ",\"label\":\"" + label + "\",\"kind\":" +
        kind.ToString(CultureInfo.InvariantCulture) + ",\"paddingLeft\":true}";

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}
