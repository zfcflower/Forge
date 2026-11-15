using Forge.Editor.Lsp;
using Forge.Editor.Tests.Support;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Lsp;

/// <summary>
/// The workspace-edit parser's shape rules (W17c review): <c>documentChanges</c> wins when both wire shapes are present
/// (reading both would apply the same file twice, the second pass against already-modified text), and repeated entries
/// for one URI merge into a single file edit so the applier's overlap check spans them and the disk path reads once.
/// </summary>
public class WorkspaceEditShapeTests
{
    [Fact]
    public async Task Document_changes_win_when_both_shapes_are_present()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"renameProvider":true}""");
        var document = new TextDocument("abc\n", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var call = client.RequestRenameAsync(document, 0, "zed");

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/rename");
        await server.RespondAsync(request.RootElement.GetProperty("id"), $$$"""
            {"changes":{"{{{uri}}}":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"fromChanges"}]},
             "documentChanges":[
              {"textDocument":{"uri":"{{{uri}}}","version":1},"edits":[
                {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"fromDocumentChanges"}]}]}
            """);

        var edit = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(edit);
        Assert.Equal(1, edit!.FileCount);
        Assert.Equal(1, edit.EditCount);
        Assert.Equal("fromDocumentChanges", edit.Files[0].Edits[0].NewText);
    }

    [Fact]
    public async Task Repeated_document_changes_for_one_uri_merge_into_a_single_file_edit()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"renameProvider":true}""");
        var document = new TextDocument("abc abc\n", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var call = client.RequestRenameAsync(document, 0, "zed");

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/rename");
        await server.RespondAsync(request.RootElement.GetProperty("id"), $$$"""
            {"documentChanges":[
              {"textDocument":{"uri":"{{{uri}}}","version":1},"edits":[
                {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"zed"}]},
              {"textDocument":{"uri":"{{{uri}}}","version":1},"edits":[
                {"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":7}},"newText":"zed"}]}]}
            """);

        var edit = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(edit);
        Assert.Equal(1, edit!.FileCount);
        Assert.Equal(2, edit.EditCount);
        Assert.Equal(0, edit.UnsupportedOperations);
    }
}