using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Styling;
using Forge.App.Editing;
using Forge.App.Tests.Workbench;
using Forge.Editor.Highlighting;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Pins the semantic-token overlay of the editor against a scripted language server: the answer is decoded into the per-line
/// index the view paints from, the identifier the server classified is drawn in the editor's own syntax brush for that
/// category (not in a second palette), and an answer that belongs to text the user already changed is stamped with the old
/// version and asked for again instead of being kept as the current classification.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorSemanticTokenTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-semantic-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task The_answer_becomes_a_per_line_index_for_the_document() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(root, "alpha\n", new()
        {
            ["textDocument/semanticTokens/full"] = """{"data":[0,0,5,0,0]}""",
        });

        await rig.Server.WaitForAsync("textDocument/semanticTokens/full");
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);

        // deltaLine 0, deltaStartChar 0, length 5, type 0 = "variable" of the legend the fake server declared.
        Assert.Equal(
            [new SemanticToken(0, 5, TokenCategory.Variable)],
            rig.Model.EditorSemanticTokens.GetLine(0).ToArray());
        Assert.Empty(rig.Model.EditorSemanticTokens.GetLine(1).ToArray());
    });

    [Fact]
    public Task A_semantic_token_repaints_the_identifier_with_the_editor_syntax_brush() => Ui(async () =>
    {
        // The lexical layer already reads a bare identifier as a variable, so the server calls this one a function: the
        // overlay then has to win over the token that was painted underneath it.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "alpha\n",
            new() { ["textDocument/semanticTokens/full"] = """{"data":[0,0,5,1,0]}""" },
            new() { ["textDocument/semanticTokens/full"] = hold });

        // The overlay is held back, so this is the identifier as the lexical layer paints it.
        await rig.Server.WaitForAsync("textDocument/semanticTokens/full");
        Assert.True(rig.Model.EditorSemanticTokens.IsEmpty);
        var before = EditorUi.TextRunColorAt(rig.Editor, 0);

        hold.SetResult();
        await rig.Until(() => rig.Model.EditorSemanticTokens.TokenCount == 1);
        WorkbenchUi.Settle(rig.Window);

        // Type index 1 of the declared legend is "function".
        Assert.Equal(
            TokenCategory.Function,
            Assert.Single(rig.Model.EditorSemanticTokens.GetLine(0).ToArray()).Category);

        // The expected color is resolved from the same resource the colorizer reads, so the assertion is about which brush
        // was applied, not about a hex value this test happens to hard-code.
        var variant = EditorOptionsApplier.IsDarkTheme(rig.Editor, EditorSettings.Current) ? ThemeVariant.Dark : ThemeVariant.Light;
        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(
            EditorOptionsApplier.Brush(rig.Editor, variant, "SyntaxFunctionBrush")).Color;

        var after = EditorUi.TextRunColorAt(rig.Editor, 0);
        Assert.Equal(expected, after);
        Assert.NotEqual(before, after);
        Assert.Contains(expected, EditorUi.TokenColors(rig.Editor));
    });

    [Fact]
    public Task An_answer_that_arrives_after_the_document_changed_is_stamped_stale_and_asked_for_again() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await EditorLspRig.OpenAsync(
            root,
            "alpha\n",
            new() { ["textDocument/semanticTokens/full"] = """{"data":[0,0,5,0,0]}""" },
            new() { ["textDocument/semanticTokens/full"] = hold });

        // Every index the model published, in order, stamped with the document version it was mapped against.
        var published = new List<int>();
        rig.Model.SemanticTokensChanged += (_, e) => published.Add(e.Index.Version);

        await rig.Server.WaitForAsync("textDocument/semanticTokens/full");
        var versionAtRequest = Assert.IsAssignableFrom<Forge.Editor.Text.TextDocument>(rig.Model.EditorDocument).Version;

        // Keep typing while the classification of the old text is still on its way back.
        rig.Editor.CaretOffset = 5;
        rig.Window.KeyTextInput("x");
        await rig.Until(() => rig.Model.EditorDocument!.Version != versionAtRequest);
        var versionAfterEdit = rig.Model.EditorDocument!.Version;

        hold.SetResult();
        await rig.Until(() => published.Count >= 1);

        // The mapping ran against the snapshot the request was made with, so the index says which text it describes.
        Assert.Equal(versionAtRequest, published[0]);
        Assert.NotEqual(versionAfterEdit, published[0]);

        // The server classified text that no longer exists, so the pump asks again instead of leaving that overlay current.
        await rig.Until(() => rig.Server.Count("textDocument/semanticTokens/full") >= 2);
        await rig.Until(() => published.Contains(versionAfterEdit));
        Assert.Equal(versionAfterEdit, rig.Model.EditorSemanticTokens.Version);
    });

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}
