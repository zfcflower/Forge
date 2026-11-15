using System.Text.Json;
using Forge.Editor.Lsp;
using Forge.Editor.Tests.Support;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Lsp;

/// <summary>
/// The wire contract of the three editor features added after diagnostics/completion: <c>textDocument/codeAction</c> (with
/// <c>codeAction/resolve</c>), <c>textDocument/rename</c> (with <c>textDocument/prepareRename</c>) and
/// <c>textDocument/inlayHint</c> (with <c>inlayHint/resolve</c>), plus the session that routes them.
/// </summary>
public class EditorFeatureLspTests
{
    private const string FakeServerCommand = "forge-editor-tests-fake-server";

    private const string EditorProviders = """
        {"codeActionProvider":{"resolveProvider":true,"codeActionKinds":["quickfix"]},
         "renameProvider":{"prepareProvider":true},
         "inlayHintProvider":{"resolveProvider":true}}
        """;

    [Fact]
    public async Task Initialize_parses_the_editor_providers_in_both_wire_shapes()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, EditorProviders);

        Assert.True(client.Capabilities.SupportsCodeActions);
        Assert.True(client.Capabilities.SupportsCodeActionResolve);
        Assert.True(client.Capabilities.SupportsRename);
        Assert.True(client.Capabilities.SupportsPrepareRename);
        Assert.True(client.Capabilities.SupportsInlayHints);
        Assert.True(client.Capabilities.SupportsInlayHintResolve);

        await using var plainServer = new FakeLanguageServer();
        await using var plain = await LspHarness.ConnectAsync(
            plainServer,
            """{"codeActionProvider":true,"renameProvider":true,"inlayHintProvider":true}""");

        Assert.True(plain.Capabilities.SupportsCodeActions);
        Assert.False(plain.Capabilities.SupportsCodeActionResolve);
        Assert.True(plain.Capabilities.SupportsRename);
        Assert.False(plain.Capabilities.SupportsPrepareRename);
        Assert.True(plain.Capabilities.SupportsInlayHints);
        Assert.False(plain.Capabilities.SupportsInlayHintResolve);
    }

    [Fact]
    public async Task Client_advertises_literal_code_actions_rename_preparation_and_inlay_hint_refresh()
    {
        await using var server = new FakeLanguageServer();
        var process = new LanguageServerProcess(new LanguageServerLaunch("fake"));
        var connecting = LspClient.ConnectAsync(process, server.Streams);
        using var initialize = await server.ReceiveAsync();
        var capabilities = initialize.RootElement.GetProperty("params").GetProperty("capabilities");

        var codeAction = capabilities.GetProperty("textDocument").GetProperty("codeAction");
        Assert.Equal(JsonValueKind.Object, codeAction.ValueKind);
        var kinds = codeAction.GetProperty("codeActionLiteralSupport").GetProperty("codeActionKind").GetProperty("valueSet");
        Assert.Contains(kinds.EnumerateArray(), kind => kind.GetString() == "quickfix");
        Assert.True(codeAction.GetProperty("dataSupport").GetBoolean());
        Assert.Contains(
            codeAction.GetProperty("resolveSupport").GetProperty("properties").EnumerateArray(),
            property => property.GetString() == "edit");

        var rename = capabilities.GetProperty("textDocument").GetProperty("rename");
        Assert.True(rename.GetProperty("prepareSupport").GetBoolean());
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("textDocument").GetProperty("inlayHint").ValueKind);
        Assert.True(capabilities.GetProperty("workspace").GetProperty("inlayHint").GetProperty("refreshSupport").GetBoolean());
        Assert.True(capabilities.GetProperty("workspace").GetProperty("codeAction").GetProperty("refreshSupport").GetBoolean());

        await server.RespondAsync(initialize.RootElement.GetProperty("id"), "{\"capabilities\":{}}");
        using var initialized = await server.ReceiveAsync();
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());
        await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Code_action_request_carries_the_range_and_the_diagnostic_context()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"codeActionProvider":true}""");
        var document = new TextDocument("abc", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 3)),
            DiagnosticSeverity.Warning,
            "CS1",
            "csharp",
            "unused");
        var call = client.RequestCodeActionsAsync(
            document,
            new LspRange(new LspPosition(0, 2), new LspPosition(0, 5)),
            [diagnostic]);

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/codeAction");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal(uri, parameters.GetProperty("textDocument").GetProperty("uri").GetString());
        var range = parameters.GetProperty("range");
        Assert.Equal(2, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, range.GetProperty("end").GetProperty("character").GetInt32());
        var context = parameters.GetProperty("context").GetProperty("diagnostics");
        Assert.Equal(1, context.GetArrayLength());
        Assert.Equal("unused", context[0].GetProperty("message").GetString());
        Assert.Equal(2, context[0].GetProperty("severity").GetInt32());
        Assert.Equal("CS1", context[0].GetProperty("code").GetString());

        await server.RespondAsync(request.RootElement.GetProperty("id"), $$$"""
            [{"title":"Fix it","kind":"quickfix","isPreferred":true,
              "edit":{"changes":{"{{{uri}}}":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"b"}] } } },
             {"title":"Command only","command":{"title":"Command only","command":"x.y"}},
             {"kind":"quickfix"}]
            """);

        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));
        var action = Assert.Single(result.Items);
        Assert.Equal("Fix it", action.Title);
        Assert.Equal("quickfix", action.Kind);
        Assert.True(action.IsPreferred);
        Assert.False(action.Disabled);
        Assert.NotNull(action.Edit);
        Assert.Equal(1, action.Edit!.FileCount);
        Assert.Equal(1, action.Edit.EditCount);
        Assert.Equal(uri, Assert.Single(action.Edit.Files).Uri);
        Assert.Equal(1, result.Unsupported);
        Assert.Equal(1, result.Malformed);
    }

    [Fact]
    public async Task Code_action_resolve_returns_the_edit_the_server_withheld()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"codeActionProvider":{"resolveProvider":true}}""");
        var document = new TextDocument("abc", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var call = client.RequestCodeActionsAsync(document, new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)));

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/codeAction");
        await server.RespondAsync(request.RootElement.GetProperty("id"), """[{"title":"Fix","kind":"quickfix","data":{"id":7}}]""");
        var action = Assert.Single((await call.WaitAsync(TimeSpan.FromSeconds(10))).Items);
        Assert.Null(action.Edit);
        Assert.True(action.NeedsResolve);

        var resolving = client.ResolveCodeActionAsync(action);
        using var resolve = await LspHarness.ReceiveMethodAsync(server, "codeAction/resolve");
        Assert.Equal("Fix", resolve.RootElement.GetProperty("params").GetProperty("title").GetString());
        Assert.Equal(7, resolve.RootElement.GetProperty("params").GetProperty("data").GetProperty("id").GetInt32());
        await server.RespondAsync(resolve.RootElement.GetProperty("id"), $$$"""
            {"title":"Fix","kind":"quickfix","edit":{"changes":{"{{{uri}}}":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"z"}] } } }
            """);

        var resolved = await resolving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(resolved);
        Assert.Equal(1, resolved!.Edit!.EditCount);
        Assert.Equal("z", Assert.Single(Assert.Single(resolved.Edit.Files).Edits).NewText);
    }

    [Fact]
    public async Task Code_action_resolve_is_not_sent_without_the_resolve_capability()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"codeActionProvider":true}""");
        var document = new TextDocument("abc", dir.PathOf("a.cs"));
        var call = client.RequestCodeActionsAsync(document, new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)));
        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/codeAction");
        await server.RespondAsync(
            request.RootElement.GetProperty("id"),
            """[{"title":"Fix","kind":"quickfix","data":{"id":7},"edit":{"changes":{}}}]""");
        var action = Assert.Single((await call.WaitAsync(TimeSpan.FromSeconds(10))).Items);

        Assert.Null(await client.ResolveCodeActionAsync(action));
        using var silent = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ReceiveAsync(silent.Token));
    }

    [Fact]
    public async Task Rename_sends_the_position_and_the_new_name_and_reads_the_changes_map()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"renameProvider":true}""");
        var document = new TextDocument("abc\nabc\n", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var call = client.RequestRenameAsync(document, 5, "zed");

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/rename");
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(1, parameters.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal("zed", parameters.GetProperty("newName").GetString());
        await server.RespondAsync(request.RootElement.GetProperty("id"), $$$"""
            {"changes":{"{{{uri}}}":[
              {"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"newText":"zed"},
              {"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":3}},"newText":"zed"}]}}
            """);

        var edit = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(edit);
        Assert.Equal(1, edit!.FileCount);
        Assert.Equal(2, edit.EditCount);
        Assert.Equal(0, edit.UnsupportedOperations);
    }

    [Fact]
    public async Task Rename_reads_document_changes_and_counts_file_operations_it_cannot_apply()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"renameProvider":true}""");
        var document = new TextDocument("abc\n", dir.PathOf("a.cs"));
        var uri = client.UriFor(document);
        var other = LspUri.FromPath(dir.PathOf("b.cs"));
        var call = client.RequestRenameAsync(document, 0, "zed");

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/rename");
        await server.RespondAsync(request.RootElement.GetProperty("id"), $$$"""
            {"documentChanges":[
              {"textDocument":{"uri":"{{{uri}}}","version":3},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"z"}]},
              {"textDocument":{"uri":"{{{other}}}","version":1},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"z"}]},
              {"kind":"rename","oldUri":"{{{other}}}","newUri":"{{{other}}}2"}]}
            """);

        var edit = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(edit);
        Assert.Equal(2, edit!.FileCount);
        Assert.Equal(2, edit.EditCount);
        Assert.Equal(1, edit.UnsupportedOperations);
    }

    [Fact]
    public async Task Prepare_rename_reads_a_range_with_a_placeholder_and_the_legacy_shapes()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"renameProvider":{"prepareProvider":true}}""");
        var document = new TextDocument("abc\n", dir.PathOf("a.cs"));

        var call = client.RequestPrepareRenameAsync(document, 1);
        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/prepareRename");
        Assert.Equal(1, request.RootElement.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32());
        await server.RespondAsync(
            request.RootElement.GetProperty("id"),
            """{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":3}},"placeholder":"abcValue"}""");
        var preparation = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(preparation);
        Assert.Equal(3, preparation!.Range.End.Character);
        Assert.Equal("abcValue", preparation.Placeholder);
        Assert.False(preparation.DefaultBehavior);

        var legacy = client.RequestPrepareRenameAsync(document, 1);
        using var legacyRequest = await LspHarness.ReceiveMethodAsync(server, "textDocument/prepareRename");
        await server.RespondAsync(legacyRequest.RootElement.GetProperty("id"), "[0,0,0,3]");
        Assert.Equal(3, (await legacy.WaitAsync(TimeSpan.FromSeconds(10)))!.Range.End.Character);

        var anywhere = client.RequestPrepareRenameAsync(document, 1);
        using var anywhereRequest = await LspHarness.ReceiveMethodAsync(server, "textDocument/prepareRename");
        await server.RespondAsync(anywhereRequest.RootElement.GetProperty("id"), """{"defaultBehavior":true}""");
        Assert.True((await anywhere.WaitAsync(TimeSpan.FromSeconds(10)))!.DefaultBehavior);
    }

    [Fact]
    public async Task Inlay_hints_are_parsed_from_string_and_part_labels_in_position_order()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"inlayHintProvider":true}""");
        var document = new TextDocument("Foo(a, b);\n", dir.PathOf("a.cs"));
        var call = client.RequestInlayHintsAsync(document, new LspRange(new LspPosition(0, 0), new LspPosition(10, 0)));

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/inlayHint");
        var range = request.RootElement.GetProperty("params").GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(10, range.GetProperty("end").GetProperty("line").GetInt32());
        await server.RespondAsync(request.RootElement.GetProperty("id"), """
            [{"position":{"line":0,"character":6},"label":[{"value":"b"},{"value":": "}],"kind":2,"paddingRight":true},
             {"position":{"line":0,"character":4},"label":"a:","kind":2,"paddingLeft":true}]
            """);

        var hints = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, hints.Count);
        Assert.Equal(4, hints[0].Position.Character);
        Assert.Equal("a:", hints[0].Label);
        Assert.True(hints[0].PaddingLeft);
        Assert.Equal(6, hints[1].Position.Character);
        Assert.Equal("b: ", hints[1].Label);
        Assert.Equal(InlayHintKind.Parameter, hints[1].Kind);
        Assert.True(hints[1].PaddingRight);
        Assert.False(hints[1].NeedsResolve);
    }

    [Fact]
    public async Task Inlay_hint_resolve_fills_a_label_the_server_withheld()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, """{"inlayHintProvider":{"resolveProvider":true}}""");
        var document = new TextDocument("Foo(a);\n", dir.PathOf("a.cs"));
        var call = client.RequestInlayHintsAsync(document, new LspRange(new LspPosition(0, 0), new LspPosition(0, 7)));

        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/inlayHint");
        await server.RespondAsync(
            request.RootElement.GetProperty("id"),
            """[{"position":{"line":0,"character":4},"paddingRight":true,"data":{"hint":1}}]""");
        var hint = Assert.Single(await call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(hint.Label);
        Assert.True(hint.NeedsResolve);

        var resolving = client.ResolveInlayHintAsync(hint);
        using var resolve = await LspHarness.ReceiveMethodAsync(server, "inlayHint/resolve");
        Assert.Equal(4, resolve.RootElement.GetProperty("params").GetProperty("position").GetProperty("character").GetInt32());
        await server.RespondAsync(resolve.RootElement.GetProperty("id"), """{"position":{"line":0,"character":4},"label":"a:"}""");
        Assert.Equal("a:", (await resolving.WaitAsync(TimeSpan.FromSeconds(10)))!.Label);
    }

    [Fact]
    public async Task Servers_without_the_providers_answer_with_nothing_and_get_no_request()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("abc\n", dir.PathOf("a.cs"));
        var range = new LspRange(new LspPosition(0, 0), new LspPosition(0, 3));

        Assert.Empty((await client.RequestCodeActionsAsync(document, range)).Items);
        Assert.Null(await client.ResolveCodeActionAsync(new LspCodeAction("x", null, false, false, null, null, true, default)));
        Assert.Null(await client.RequestPrepareRenameAsync(document, 0));
        Assert.Null(await client.RequestRenameAsync(document, 0, "z"));
        Assert.Empty(await client.RequestInlayHintsAsync(document, range));
        Assert.Null(await client.ResolveInlayHintAsync(new InlayHint(new LspPosition(0, 0), string.Empty, null, false, false, default)));

        using var silent = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ReceiveAsync(silent.Token));
    }

    [Fact]
    public async Task Session_routes_the_editor_requests_and_raises_its_refresh_events()
    {
        using var dir = new TempDirectory();
        var documentPath = dir.PathOf("a.cs");
        var documentUri = LspUri.FromPath(documentPath);
        var process = new FakeLanguageServerProcess(
            capabilities: $$"""{"capabilities":{{EditorProviders}}}""",
            answers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["textDocument/codeAction"] =
                    "[{\"title\":\"Fix\",\"kind\":\"quickfix\",\"edit\":{\"changes\":{\"" + documentUri +
                    "\":[{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}},\"newText\":\"z\"}]}}}]",
                ["textDocument/rename"] = "{\"changes\":{}}",
                ["textDocument/inlayHint"] = """[{"position":{"line":0,"character":1},"label":"a:"}]""",
            });
        var options = new LanguageSessionOptions
        {
            LanguageId = "csharp",
            WorkspacePath = dir.Root,
            ProcessFactory = (_, _) => process,
        };
        await using var session = new LanguageSession(new LanguageServerLaunch(FakeServerCommand), options);
        Assert.True(await session.TryStartAsync());
        var document = new TextDocument("abc\n", dir.PathOf("a.cs"));
        await session.OpenAsync(document);
        var range = new LspRange(new LspPosition(0, 0), new LspPosition(0, 3));

        var actions = await session.RequestCodeActionsAsync(document, range);
        Assert.NotNull(actions);
        Assert.Equal("Fix", Assert.Single(actions!.Items).Title);

        var renamed = await session.RequestRenameAsync(document, 0, "zed");
        Assert.NotNull(renamed);
        Assert.Equal(0, renamed!.FileCount);

        var hints = await session.RequestInlayHintsAsync(document, range);
        Assert.Equal("a:", Assert.Single(hints!).Label);

        var inlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var code = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.InlayHintsRefreshRequested += (_, _) => inlay.TrySetResult();
        session.CodeActionsRefreshRequested += (_, _) => code.TrySetResult();
        await process.RequestAsync(41, "workspace/inlayHint/refresh");
        await process.RequestAsync(42, "workspace/codeAction/refresh");
        await inlay.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await code.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RequestRenameAsync(document, 0, "zed", cancelled.Token));
    }
}
