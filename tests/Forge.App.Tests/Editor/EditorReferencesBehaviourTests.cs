using System.Globalization;
using System.Text;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Drives <see cref="FileDocumentViewModel.RequestReferencesAsync"/> directly over the scripted language server, for the edges
/// the keyboard-level suite does not reach: a server that never declared <c>referencesProvider</c>, a request that is cancelled or
/// outdated by an edit while the server is still thinking, the list cap and the way the answer is folded and ordered.
/// </summary>
[Collection("forge-app-ui")]
public sealed class EditorReferencesBehaviourTests : IDisposable
{
    private const string Method = "textDocument/references";
    private const string Source = "class Alpha { }\nAlpha();\n";
    private const int Offset = 8;

    private readonly string root = TestTempFiles.NewDirectory("forge-references-edges-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    [Fact]
    public Task A_server_without_references_support_shows_the_unsupported_status_and_is_not_asked() => Ui(async () =>
    {
        const string Capabilities = """
            {"capabilities":{"textDocumentSync":{"openClose":true,"change":2,"save":true},"definitionProvider":true}}
            """;
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new() { [Method] = "[]" }, capabilities: Capabilities);

        var list = await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None);

        Assert.Null(list);
        Assert.Equal(
            new LocText(InspectorStrings.EditorLspUnsupported, new LocText(InspectorStrings.EditorLspFeatureReferences)).Resolve(),
            rig.Model.LspStatus);
        Assert.Equal(0, rig.Server.Count(Method));
    });

    [Fact]
    public Task Cancelling_mid_request_yields_null_and_a_later_request_gets_only_its_own_answer() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var program = Path.Combine(root, "Program.cs");
        await using var rig = await EditorLspRig.OpenAsync(
            root, Source, new() { [Method] = "[" + Location(program, 1, 0) + "]" }, new() { [Method] = hold });

        using var cts = new CancellationTokenSource();
        var first = rig.Model.RequestReferencesAsync(Offset, cts.Token);
        await rig.Server.WaitForAsync(Method);

        // The flyout closed (or a newer request replaced this one) while the server was still working.
        cts.Cancel();
        hold.SetResult();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(10)));

        // A fresh request is not contaminated by the cancelled one and sees exactly its own answer.
        var second = await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None);
        var answered = Assert.IsType<EditorLspReferenceList>(second);
        Assert.Equal("Alpha", answered.Symbol);
        Assert.Equal([(2, 1)], answered.Items.Select(static item => (item.Line, item.Column)).ToArray());
        Assert.False(answered.Truncated);
    });

    [Fact]
    public Task A_token_cancelled_before_the_call_returns_null_without_asking_the_server() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root, Source, new() { [Method] = "[" + Location(Path.Combine(root, "Program.cs"), 1, 0) + "]" });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Null(await rig.Model.RequestReferencesAsync(Offset, cts.Token));
        Assert.Equal(0, rig.Server.Count(Method));
    });

    [Fact]
    public Task An_answer_for_a_document_that_changed_during_the_request_is_discarded() => Ui(async () =>
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await EditorLspRig.OpenAsync(
            root, Source, new() { [Method] = "[" + Location(Path.Combine(root, "Program.cs"), 1, 0) + "]" }, new() { [Method] = hold });

        var pending = rig.Model.RequestReferencesAsync(Offset, CancellationToken.None);
        await rig.Server.WaitForAsync(Method);
        var version = rig.Model.EditorDocument!.Version;

        // The locations are positions in text that no longer exists, so showing them would open the wrong places.
        rig.Editor.Document.Insert(0, "// moved\n");
        await rig.Until(() => rig.Model.EditorDocument!.Version != version);
        hold.SetResult();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
    });

    [Fact]
    public Task More_locations_than_the_row_cap_are_cut_and_the_list_says_so() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        var program = Path.Combine(root, "Program.cs");
        var total = FileDocumentViewModel.MaxReferenceRows + 5;
        var locations = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            locations.Add(Location(other, i, 0));
        }

        // Last in the answer, first after sorting: the cap is applied after the own-file-first ordering, not to the raw answer.
        locations.Add(Location(program, 1, 0));
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new() { [Method] = "[" + string.Join(",", locations) + "]" });

        var list = Assert.IsType<EditorLspReferenceList>(await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None));

        Assert.Equal(FileDocumentViewModel.MaxReferenceRows, list.Items.Count);
        Assert.True(list.Truncated);
        Assert.Equal("Program.cs", Path.GetFileName(list.Items[0].Path));
        Assert.Equal("Other.cs", Path.GetFileName(list.Items[^1].Path));
        Assert.Equal(FileDocumentViewModel.MaxReferenceRows - 1, list.Items[^1].Line);

        var model = new ReferenceListViewModel(list, list.Symbol, static _ => { });
        Assert.True(model.IsTruncated);
        Assert.Equal(FileDocumentViewModel.MaxReferenceRows, model.Items.Count);
        Assert.Contains(InspectorStrings.ProblemsTruncatedRows(FileDocumentViewModel.MaxReferenceRows).Resolve(), model.Summary, StringComparison.Ordinal);
    });

    [Fact]
    public Task Exactly_the_row_cap_is_not_reported_as_truncated() => Ui(async () =>
    {
        var other = Path.Combine(root, "Other.cs");
        var locations = Enumerable.Range(0, FileDocumentViewModel.MaxReferenceRows).Select(i => Location(other, i, 0));
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new() { [Method] = "[" + string.Join(",", locations) + "]" });

        var list = Assert.IsType<EditorLspReferenceList>(await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None));

        Assert.Equal(FileDocumentViewModel.MaxReferenceRows, list.Items.Count);
        Assert.False(list.Truncated);
        var model = new ReferenceListViewModel(list, list.Symbol, static _ => { });
        Assert.False(model.IsTruncated);
        Assert.DoesNotContain(InspectorStrings.ProblemsTruncatedRows(FileDocumentViewModel.MaxReferenceRows).Resolve(), model.Summary, StringComparison.Ordinal);
    });

    [Fact]
    public Task Duplicates_are_folded_remote_uris_dropped_and_the_own_file_comes_first() => Ui(async () =>
    {
        var program = Path.Combine(root, "Program.cs");
        var other = Path.Combine(root, "Other.cs");
        var alpha = Path.Combine(root, "A.cs");
        await using var rig = await EditorLspRig.OpenAsync(root, Source, new()
        {
            [Method] = "[" + string.Join(",",
                Location(other, 5, 0),
                Location(program, 1, 3),
                Location(alpha, 0, 0),
                Location(program, 1, 3),
                RemoteLocation("https://example.com/Remote.cs", 0),
                Location(program, 0, 6),
                Location(other, 5, 0),
                Location(other, 2, 4),
                Location(program, 1, 3, endCharacter: 9)) + "]",
        });

        var list = Assert.IsType<EditorLspReferenceList>(await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None));

        // The own file first (by line, then column), then the other files by path; the same start reported twice (or with
        // another end) is one row, and the https location has no file to open so it is not a row at all.
        Assert.Equal(
            [("Program.cs", 1, 7), ("Program.cs", 2, 4), ("A.cs", 1, 1), ("Other.cs", 3, 5), ("Other.cs", 6, 1)],
            list.Items.Select(static item => (Path.GetFileName(item.Path), item.Line, item.Column)).ToArray());
        Assert.False(list.Truncated);
        Assert.All(list.Items, static item => Assert.Equal(item.Path, item.Target.Path));
    });

    [Fact]
    public Task Only_remote_locations_leave_an_empty_list_not_a_failure() => Ui(async () =>
    {
        await using var rig = await EditorLspRig.OpenAsync(
            root, Source, new() { [Method] = "[" + RemoteLocation("https://example.com/Remote.cs", 0) + "]" });

        var list = Assert.IsType<EditorLspReferenceList>(await rig.Model.RequestReferencesAsync(Offset, CancellationToken.None));

        Assert.Empty(list.Items);
        Assert.False(list.Truncated);
    });

    private static string Location(string path, int line, int character, int? endCharacter = null) =>
        Raw(LspUri.FromPath(path), line, character, endCharacter ?? character + 5);

    private static string RemoteLocation(string uri, int line) => Raw(uri, line, 0, 5);

    private static string Raw(string uri, int line, int character, int endCharacter)
    {
        var text = new StringBuilder();
        text.Append("{\"uri\":\"").Append(uri).Append("\",\"range\":{\"start\":{\"line\":")
            .Append(line.ToString(CultureInfo.InvariantCulture)).Append(",\"character\":").Append(character.ToString(CultureInfo.InvariantCulture))
            .Append("},\"end\":{\"line\":").Append(line.ToString(CultureInfo.InvariantCulture)).Append(",\"character\":")
            .Append(endCharacter.ToString(CultureInfo.InvariantCulture)).Append("}}}");
        return text.ToString();
    }

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);
}
