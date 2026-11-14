using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.Editor.Highlighting;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// The lifecycle edges of the semantic-token overlay around <see cref="EditorSemanticTokenTests"/>: a server that does not
/// offer full classification is never asked, a server-side refresh and a delta-capable server drive new requests, an edit that
/// lands while an answer is in flight ends with the newer text classified and painted, a document past the highlight ceiling
/// loses the overlay and is not asked again, and a view that leaves the visual tree unsubscribes from the model and the bridge.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorSemanticTokenLifecycleTests : IDisposable
{
    private const string Method = "textDocument/semanticTokens/full";
    private const string Legend = "\"legend\":{\"tokenTypes\":[\"variable\",\"function\",\"class\"],\"tokenModifiers\":[]}";

    private readonly string root = TestTempFiles.NewDirectory("forge-semantic-life-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private static string Capabilities(string semanticProvider) =>
        """{"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"referencesProvider":true""" +
        (semanticProvider.Length == 0 ? string.Empty : ",\"semanticTokensProvider\":{" + semanticProvider + "}") + "}}";

    [Theory]
    [InlineData("")]
    [InlineData("\"legend\":{\"tokenTypes\":[\"variable\"],\"tokenModifiers\":[]},\"full\":false,\"range\":true")]
    public Task A_server_without_full_classification_is_never_asked_and_nothing_is_painted(string provider) => Ui(async () =>
    {
        // The provider string is either absent or range-only.
        var semantic = provider;
        await using var rig = await EditorLspRig.OpenAsync(
            root, "alpha\n", new() { [Method] = """{"data":[0,0,5,0,0]}""" }, capabilities: Capabilities(semantic));

        await rig.Server.WaitForAsync("textDocument/didOpen");
        rig.Editor.CaretOffset = 5;
        rig.Window.KeyTextInput("x");
        // Past the 400 ms debounce and the 2.5 s catch-up request a capable server would have received.
        await rig.Pump(2900);

        Assert.Equal(0, rig.Server.Count(Method));
        Assert.Equal(0, rig.Server.Count("textDocument/semanticTokens/range"));
        Assert.True(rig.Model.EditorSemanticTokens.IsEmpty);
        Assert.Equal(0, rig.Model.EditorSemanticTokens.TokenCount);
    });

    [Fact]
    public Task A_delta_capable_server_gets_a_delta_request_after_an_edit_and_the_edits_are_applied() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "alpha\n",
            new()
            {
                [Method] = """{"resultId":"r1","data":[0,0,5,0,0]}""",
                ["textDocument/semanticTokens/full/delta"] = """{"resultId":"r2","edits":[{"start":0,"deleteCount":5,"data":[0,0,6,1,0]}]}""",
            },
            capabilities: Capabilities(Legend + ",\"full\":{\"delta\":true}"));

        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);
        Assert.Equal(0, rig.Server.Count("textDocument/semanticTokens/full/delta"));

        rig.Editor.CaretOffset = 5;
        rig.Window.KeyTextInput("x");

        using var request = JsonDocument.Parse(await rig.Server.WaitForAsync("textDocument/semanticTokens/full/delta"));
        Assert.Equal("r1", request.RootElement.GetProperty("params").GetProperty("previousResultId").GetString());

        // The delta replaced the five integers of the first answer with one token of length 6 (the identifier plus "x").
        await rig.Until(() => rig.Model.EditorSemanticTokens.GetLine(0).ToArray() is [{ Length: 6, Category: TokenCategory.Function }]);
    });

    [Fact]
    public Task A_refresh_request_from_the_server_asks_again_and_the_new_classification_replaces_the_old() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root, "alpha\n", new() { [Method] = """{"data":[0,0,5,0,0]}""" });
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);
        Assert.Equal(TokenCategory.Variable, Assert.Single(rig.Model.EditorSemanticTokens.GetLine(0).ToArray()).Category);
        var before = rig.Server.Count(Method);

        // The analysis moved on the server: its answer is now "class", and it says so instead of the client polling.
        rig.Server.SetResult(Method, """{"data":[0,0,5,2,0]}""");
        rig.Server.Request("workspace/semanticTokens/refresh");

        await rig.Until(() => rig.Server.Count(Method) > before);
        await rig.Until(() => rig.Model.EditorSemanticTokens.GetLine(0).ToArray() is [{ Category: TokenCategory.Class }]);
    });

    [Fact]
    public Task An_edit_during_an_in_flight_request_ends_with_the_newer_text_classified_and_painted() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "alpha\n",
            new() { [Method] = """{"data":[0,0,5,0,0]}""" },
            new() { [Method] = hold });

        var published = new List<(int Version, int Length)>();
        rig.Model.SemanticTokensChanged += (_, e) =>
        {
            var line = e.Index.GetLine(0).ToArray();
            published.Add((e.Index.Version, line.Length == 0 ? 0 : line[0].Length));
        };

        await rig.Server.WaitForAsync(Method);
        rig.Editor.CaretOffset = 5;
        rig.Window.KeyTextInput("x");
        await rig.Until(() => rig.Editor.Document.Text.StartsWith("alphax", StringComparison.Ordinal) && rig.Model.EditorDocument!.Snapshot.Length == 7);
        var newVersion = rig.Model.EditorDocument!.Version;

        hold.SetResult();
        await rig.Until(() => published.Count >= 1);

        // The first answer describes the old text; from now on the server knows the new text and classifies "alphax" as a function.
        rig.Server.SetResult(Method, """{"data":[0,0,6,1,0]}""");

        await rig.Until(() => rig.Model.EditorSemanticTokens.Version == newVersion);
        Assert.Equal(newVersion, rig.Model.EditorDocument!.Version);
        var token = Assert.Single(rig.Model.EditorSemanticTokens.GetLine(0).ToArray());
        Assert.Equal((6, TokenCategory.Function), (token.Length, token.Category));
        Assert.True(rig.Server.Count(Method) >= 2);

        // And it is what is on screen: the identifier is drawn with the function brush of the editor's own palette.
        WorkbenchUi.Settle(rig.Window);
        var variant = EditorOptionsApplier.IsDarkTheme(rig.Editor, EditorSettings.Current) ? ThemeVariant.Dark : ThemeVariant.Light;
        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(
            EditorOptionsApplier.Brush(rig.Editor, variant, "SyntaxFunctionBrush")).Color;
        Assert.Equal(expected, EditorUi.TextRunColorAt(rig.Editor, 0));
        Assert.Equal(expected, EditorUi.TextRunColorAt(rig.Editor, 5));
    });

    [Fact]
    public Task A_document_over_the_highlight_limit_clears_the_index_and_is_not_asked_again() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root, "alpha\n", new() { [Method] = """{"data":[0,0,5,0,0]}""" });
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);
        var cleared = false;
        rig.Model.SemanticTokensChanged += (_, e) => cleared |= e.Index.IsEmpty;

        Grow(rig, ForgeEditorBridge.HighlightLimit + 1);
        Assert.True(rig.Editor.Document.TextLength > ForgeEditorBridge.HighlightLimit);

        await rig.Until(() => rig.Model.EditorSemanticTokens.IsEmpty);
        Assert.True(cleared, "SemanticTokensChanged announced the empty index so the colorizer dropped its ranges.");
        var asked = rig.Server.Count(Method);

        // Further edits past the ceiling are still not classified: no request, no resurrected index.
        rig.Editor.Document.Insert(rig.Editor.Document.TextLength, "x");
        await rig.Pump(1200);
        Assert.Equal(asked, rig.Server.Count(Method));
        Assert.True(rig.Model.EditorSemanticTokens.IsEmpty);

        // Shrinking back under the ceiling brings the overlay back.
        rig.Editor.Document.Remove("alpha\n".Length, rig.Editor.Document.TextLength - "alpha\n".Length);
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);
    });

    [Fact]
    public Task Detaching_the_view_unsubscribes_it_from_the_model_and_the_bridge_and_stops_asking() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root, "alpha\n", new() { [Method] = """{"data":[0,0,5,0,0]}""" });
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);

        var view = rig.View!;
        var bridge = typeof(Forge.App.Views.FileDocumentView)
            .GetField("bridge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.Contains(view, Subscribers(rig.Model, nameof(FileDocumentViewModel.SemanticTokensChanged)));
        Assert.Contains(view, Subscribers(bridge, nameof(ForgeEditorBridge.HighlightingInstalled)));

        rig.Window.Content = null;
        WorkbenchUi.Settle(rig.Window);

        Assert.DoesNotContain(view, Subscribers(rig.Model, nameof(FileDocumentViewModel.SemanticTokensChanged)));
        Assert.DoesNotContain(view, Subscribers(bridge, nameof(ForgeEditorBridge.HighlightingInstalled)));
        Assert.True(rig.Model.EditorSemanticTokens.IsEmpty, "the model drops the overlay with the view that painted it");

        var asked = rig.Server.Count(Method);
        await rig.Pump(900);
        Assert.Equal(asked, rig.Server.Count(Method));
    });

    /// <summary>The objects subscribed to a field-like event (the backing field carries the event's name).</summary>
    private static List<object?> Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(eventName + " is no longer a field-like event; update this helper.");
        return ((MulticastDelegate?)field.GetValue(owner))?.GetInvocationList().Select(static d => d.Target).ToList() ?? [];
    }

    private static void Grow(EditorLspRig rig, int length)
    {
        var missing = length - rig.Editor.Document.TextLength;
        var text = new StringBuilder(missing);
        for (; missing >= 10; missing -= 10)
        {
            text.Append("xxxxxxxxx\n");
        }

        text.Append('x', missing);
        rig.Editor.Document.Insert(rig.Editor.Document.TextLength, text.ToString());
    }

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}
