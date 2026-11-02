using System.Text.Json;
using Forge.Editor.Lsp;
using Forge.Editor.Tests.Support;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Lsp;

public class LspClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string SemanticCapabilities = """
        {"semanticTokensProvider":{
          "legend":{"tokenTypes":["namespace","type","class","method"],"tokenModifiers":["declaration","static"]},
          "full":{"delta":true},"range":false}}
        """;

    [Fact]
    public async Task Initialize_sends_client_info_and_utf16_encoding_then_negotiates_utf8()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        var process = new LanguageServerProcess(new LanguageServerLaunch("fake"));
        var options = new LspClientOptions { WorkspacePath = dir.Root, ClientName = "TestClient", ClientVersion = "9.9" };
        var connecting = LspClient.ConnectAsync(process, server.Streams, options);

        using var initialize = await server.ReceiveAsync();
        var root = initialize.RootElement;
        Assert.Equal("initialize", root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal("TestClient", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.9", parameters.GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.StartsWith("file:", parameters.GetProperty("rootUri").GetString());
        Assert.Equal("utf-16", parameters.GetProperty("capabilities").GetProperty("general").GetProperty("positionEncodings")[0].GetString());
        Assert.Equal(1, parameters.GetProperty("workspaceFolders").GetArrayLength());

        await server.RespondAsync(root.GetProperty("id"), "{\"capabilities\":{\"positionEncoding\":\"utf-8\",\"textDocumentSync\":2,\"hoverProvider\":true}}");
        using var initialized = await server.ReceiveAsync();
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());

        await using var client = await connecting.WaitAsync(Patience);
        Assert.Equal(PositionEncoding.Utf8, client.PositionEncoding);
        Assert.True(client.Capabilities.SupportsIncrementalSync);
        Assert.True(client.Capabilities.SupportsHover);
        Assert.False(client.Capabilities.SupportsCompletion);
        Assert.False(client.IsClosed);
    }

    [Fact]
    public async Task Initialize_error_response_fails_the_connection()
    {
        await using var server = new FakeLanguageServer();
        var process = new LanguageServerProcess(new LanguageServerLaunch("fake"));
        var connecting = LspClient.ConnectAsync(process, server.Streams);

        using var initialize = await server.ReceiveAsync();
        await server.RespondErrorAsync(initialize.RootElement.GetProperty("id"), -32603, "no thanks");

        var error = await Assert.ThrowsAsync<LspProtocolException>(() => connecting.WaitAsync(Patience));
        Assert.Equal("no thanks", error.Message);
    }

    [Fact]
    public async Task Did_open_sends_uri_language_version_and_full_text()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("class A {}", dir.PathOf("a.cs"), version: 3) { LanguageId = "csharp" };

        await client.DidOpenAsync(document);

        using var message = await server.ReceiveAsync();
        Assert.Equal("textDocument/didOpen", message.RootElement.GetProperty("method").GetString());
        var text = message.RootElement.GetProperty("params").GetProperty("textDocument");
        Assert.Equal(client.UriFor(document), text.GetProperty("uri").GetString());
        Assert.Equal("csharp", text.GetProperty("languageId").GetString());
        Assert.Equal(3, text.GetProperty("version").GetInt32());
        Assert.Equal("class A {}", text.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Did_open_uses_the_configured_language_id_for_untyped_documents()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, options: new LspClientOptions { LanguageId = "configured" });

        await client.DidOpenAsync(new TextDocument("y", dir.PathOf("c.txt")));

        using var message = await server.ReceiveAsync();
        Assert.Equal("configured", message.RootElement.GetProperty("params").GetProperty("textDocument").GetProperty("languageId").GetString());
    }

    [Fact]
    public async Task Did_open_defaults_to_plaintext_language_id()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);

        await client.DidOpenAsync(new TextDocument("z", dir.PathOf("d.txt")));

        using var message = await server.ReceiveAsync();
        Assert.Equal("plaintext", message.RootElement.GetProperty("params").GetProperty("textDocument").GetProperty("languageId").GetString());
    }

    [Fact]
    public async Task Documents_without_a_path_cannot_be_synced()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("no path");

        Assert.Throws<ArgumentException>(() => client.UriFor(document));
        Assert.Throws<ArgumentException>(() => client.DidOpenAsync(document));
    }

    [Fact]
    public async Task Incremental_change_reports_the_range_in_utf16_units()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"textDocumentSync\":2}");
        var document = new TextDocument("x😀y\nz", dir.PathOf("u16.cs"));
        TextChangedEventArgs? captured = null;
        document.Changed += (_, args) => captured = args;

        document.Replace(1, 2, "Q");
        await client.DidChangeAsync(document, captured!);

        using var message = await server.ReceiveAsync();
        var parameters = message.RootElement.GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("textDocument").GetProperty("version").GetInt32());
        var change = parameters.GetProperty("contentChanges")[0];
        Assert.Equal("Q", change.GetProperty("text").GetString());
        var range = change.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Incremental_change_reports_the_range_in_utf8_bytes_when_negotiated()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"textDocumentSync\":2,\"positionEncoding\":\"utf-8\"}");
        var document = new TextDocument("x😀y\nz", dir.PathOf("u8.cs"));
        TextChangedEventArgs? captured = null;
        document.Changed += (_, args) => captured = args;

        document.Replace(1, 2, "Q");
        await client.DidChangeAsync(document, captured!);

        using var message = await server.ReceiveAsync();
        var range = message.RootElement.GetProperty("params").GetProperty("contentChanges")[0].GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Multi_line_incremental_change_spans_both_lines()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"textDocumentSync\":2}");
        var document = new TextDocument("ab\ncd\nef", dir.PathOf("multi.cs"));
        TextChangedEventArgs? captured = null;
        document.Changed += (_, args) => captured = args;

        document.Replace(1, 6, "X");
        await client.DidChangeAsync(document, captured!);

        using var message = await server.ReceiveAsync();
        var range = message.RootElement.GetProperty("params").GetProperty("contentChanges")[0].GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(1, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Full_sync_servers_receive_the_whole_text_without_a_range()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"textDocumentSync\":1}");
        var document = new TextDocument("whole", dir.PathOf("full.cs"));
        TextChangedEventArgs? captured = null;
        document.Changed += (_, args) => captured = args;
        document.Insert(5, "!");

        await client.DidChangeAsync(document, captured!);

        using var message = await server.ReceiveAsync();
        var change = message.RootElement.GetProperty("params").GetProperty("contentChanges")[0];
        Assert.False(change.TryGetProperty("range", out _));
        Assert.Equal("whole!", change.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Save_and_close_notifications_carry_the_document_identity()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("text", dir.PathOf("s.cs"));
        document.Insert(4, "!");

        await client.DidSaveAsync(document, includeText: true);
        await client.DidSaveAsync(document, includeText: false);
        await client.DidCloseAsync(document);

        using var save = await server.ReceiveAsync();
        Assert.Equal("text!", save.RootElement.GetProperty("params").GetProperty("text").GetString());
        using var bareSave = await server.ReceiveAsync();
        Assert.False(bareSave.RootElement.GetProperty("params").TryGetProperty("text", out _));
        using var close = await server.ReceiveAsync();
        Assert.Equal("textDocument/didClose", close.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task Published_diagnostics_are_stored_as_immutable_snapshots()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var uri = LspUri.FromPath(dir.PathOf("d.cs"));
        var changed = NextDiagnostics(client.Diagnostics);

        await server.NotifyAsync("textDocument/publishDiagnostics", """
            {"uri":"__URI__","version":4,"diagnostics":[
              {"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}},"severity":1,"code":"CS0103","source":"csc","message":"bad name","tags":[1],
               "relatedInformation":[{"location":{"uri":"__URI__","range":{"start":{"line":2,"character":0},"end":{"line":2,"character":1}}},"message":"see here"}]},
              {"range":{"start":{"line":3,"character":0},"end":{"line":3,"character":0}},"severity":2,"code":7,"message":"careful"}
            ]}
            """.Replace("__URI__", uri));

        var first = await changed.WaitAsync(Patience);
        Assert.Equal(uri, first.Uri);
        Assert.Equal(4, first.Version);
        Assert.Equal(1, first.ErrorCount);
        Assert.Equal(1, first.WarningCount);
        var error = first.Diagnostics[0];
        Assert.Equal("CS0103", error.Code);
        Assert.Equal("csc", error.Source);
        Assert.Equal("bad name", error.Message);
        Assert.Equal(new[] { "unnecessary" }, error.Tags);
        Assert.Equal("see here", Assert.Single(error.RelatedInformation!).Message);
        Assert.Equal("7", first.Diagnostics[1].Code);
        Assert.Equal(DiagnosticSeverity.Warning, first.Diagnostics[1].Severity);

        changed = NextDiagnostics(client.Diagnostics);
        await server.NotifyAsync("textDocument/publishDiagnostics", """{"uri":"__URI__","diagnostics":[]}""".Replace("__URI__", uri));
        var second = await changed.WaitAsync(Patience);

        Assert.Null(second.Version);
        Assert.True(second.Sequence > first.Sequence);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(2, first.Diagnostics.Count);
        Assert.Equal(1, first.ErrorCount);
        Assert.Same(second, client.Diagnostics.Get(uri));
    }

    [Fact]
    public async Task Closing_a_document_clears_its_diagnostics()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("x", dir.PathOf("close.cs"));
        var uri = client.UriFor(document);
        var published = NextDiagnostics(client.Diagnostics);
        await server.NotifyAsync("textDocument/publishDiagnostics", """{"uri":"__URI__","version":1,"diagnostics":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"message":"m"}]}""".Replace("__URI__", uri));
        await published.WaitAsync(Patience);
        var removed = NextDiagnostics(client.Diagnostics);

        await client.DidCloseAsync(document);

        var snapshot = await removed.WaitAsync(Patience);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Null(client.Diagnostics.Get(uri));
        Assert.Equal(0, client.Diagnostics.DocumentCount);
    }

    [Fact]
    public void Diagnostics_store_ignores_removal_of_unknown_documents()
    {
        var store = new DiagnosticsStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Remove("file:///nothing.cs");
        store.Update("file:///a.cs", 1, []);
        store.Clear();

        Assert.Equal(2, raised);
        Assert.Equal(0, store.DocumentCount);
        Assert.Null(store.GetForPath(string.Empty));
    }

    [Fact]
    public void Diagnostic_ranges_map_to_offsets_in_the_requested_encoding()
    {
        var snapshot = TextSnapshot.FromText("x😀y\nz");
        var utf16 = new LspDiagnostic(new LspRange(new LspPosition(0, 1), new LspPosition(0, 3)), DiagnosticSeverity.Error, null, null, "m");
        var utf8 = new LspDiagnostic(new LspRange(new LspPosition(0, 1), new LspPosition(0, 5)), DiagnosticSeverity.Error, null, null, "m");

        Assert.Equal((1, 3), utf16.ToOffsets(snapshot, PositionEncoding.Utf16));
        Assert.Equal((1, 3), utf8.ToOffsets(snapshot, PositionEncoding.Utf8));
    }

    [Fact]
    public async Task Log_messages_with_multibyte_text_arrive_intact()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LogMessage += message => logged.TrySetResult(message);

        await server.NotifyAsync("window/logMessage", "{\"type\":3,\"message\":\"héllo 😀 ✓\"}");

        Assert.Equal("héllo 😀 ✓", await logged.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Concurrent_requests_are_matched_to_their_responses_out_of_order()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var first = new TextDocument("one", dir.PathOf("first.cs"));
        var second = new TextDocument("two", dir.PathOf("second.cs"));

        var firstCall = client.RequestHoverAsync(first, 0);
        var secondCall = client.RequestHoverAsync(second, 0);
        var requests = new Dictionary<string, JsonDocument>();
        for (var i = 0; i < 2; i++)
        {
            var message = await server.ReceiveAsync();
            requests[message.RootElement.GetProperty("params").GetProperty("textDocument").GetProperty("uri").GetString()!] = message;
        }

        await server.RespondAsync(requests[client.UriFor(second)].RootElement.GetProperty("id"), "{\"contents\":{\"kind\":\"markdown\",\"value\":\"from second\"}}");
        await server.RespondAsync(requests[client.UriFor(first)].RootElement.GetProperty("id"), "{\"contents\":\"from first\"}");

        Assert.Equal("from first", (await firstCall.WaitAsync(Patience))!.Markup);
        Assert.Equal("from second", (await secondCall.WaitAsync(Patience))!.Markup);
        foreach (var request in requests.Values)
        {
            request.Dispose();
        }
    }

    [Fact]
    public async Task Hover_null_result_and_empty_markup_both_yield_no_hover()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("h.cs"));

        var nullCall = client.RequestHoverAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), "null");
        }

        Assert.Null(await nullCall.WaitAsync(Patience));

        var emptyCall = client.RequestHoverAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), "{\"contents\":[{\"language\":\"csharp\",\"value\":\"\"}]}");
        }

        Assert.Null(await emptyCall.WaitAsync(Patience));
    }

    [Fact]
    public async Task Hover_array_contents_are_joined_with_blank_lines()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("arr.cs"));

        var call = client.RequestHoverAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await server.RespondAsync(
                request.RootElement.GetProperty("id"),
                "{\"contents\":[\"signature\",{\"language\":\"csharp\",\"value\":\"docs\"}],\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}");
        }

        var hover = await call.WaitAsync(Patience);
        Assert.Equal("signature\n\ndocs", hover!.Markup);
        Assert.Equal(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)), hover.Range);
    }

    [Fact]
    public async Task Requests_without_the_capability_are_answered_locally_and_never_sent()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("x", dir.PathOf("local.cs"));

        Assert.Null(await client.RequestHoverAsync(document, 0));
        Assert.Empty(await client.RequestDocumentSymbolsAsync(document));
        Assert.Empty(await client.RequestDefinitionAsync(document, 0));
        Assert.Empty(await client.RequestReferencesAsync(document, 0));
        Assert.Empty((await client.RequestCompletionAsync(document, 0)).Items);
        Assert.Empty((await client.RequestSemanticTokensAsync(document)).Tokens);

        await client.DidOpenAsync(document);
        using var next = await server.ReceiveAsync();
        Assert.Equal("textDocument/didOpen", next.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task Completion_maps_items_and_respects_the_item_limit()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"completionProvider\":{}}", new LspClientOptions { MaxCompletionItems = 2 });
        var document = new TextDocument("x", dir.PathOf("comp.cs"));

        var call = client.RequestCompletionAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/completion"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """
                {"isIncomplete":true,"items":[
                  {"label":"alpha","kind":3,"detail":"d","documentation":{"kind":"markdown","value":"doc"},"insertText":"alphaInsert","sortText":"0"},
                  {"label":"beta","kind":14,"documentation":"plain"},
                  {"label":"gamma"}
                ]}
                """);
        }

        var result = await call.WaitAsync(Patience);
        Assert.True(result.IsIncomplete);
        Assert.Equal(2, result.Items.Count);
        var alpha = result.Items[0];
        Assert.Equal(CompletionItemKind.Function, alpha.Kind);
        Assert.Equal("doc", alpha.Documentation);
        Assert.Equal("alphaInsert", alpha.InsertText);
        Assert.Equal("0", alpha.SortText);
        Assert.Equal(CompletionItemKind.Keyword, result.Items[1].Kind);
        Assert.Equal("plain", result.Items[1].Documentation);
    }

    [Fact]
    public async Task Completion_text_edits_and_bare_array_results_are_understood()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"completionProvider\":{}}");
        var document = new TextDocument("abc", dir.PathOf("edit.cs"));

        var call = client.RequestCompletionAsync(document, 1);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/completion"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """
                [{"label":"abc","textEdit":{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"newText":"abcd"}}]
                """);
        }

        var result = await call.WaitAsync(Patience);
        Assert.False(result.IsIncomplete);
        var edit = Assert.Single(result.Items).TextEdit!;
        Assert.Equal("abcd", edit.NewText);
        Assert.Equal(new LspRange(new LspPosition(0, 0), new LspPosition(0, 2)), edit.Range);
    }

    [Fact]
    public async Task Document_symbols_keep_hierarchy_and_accept_location_form()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"documentSymbolProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("sym.cs"));

        var call = client.RequestDocumentSymbolsAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/documentSymbol"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """
                [{"name":"Outer","kind":5,"detail":"class","range":{"start":{"line":0,"character":0},"end":{"line":9,"character":1}},
                  "selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":11}},
                  "children":[{"name":"Inner","kind":6,"range":{"start":{"line":1,"character":2},"end":{"line":2,"character":3}},
                    "selectionRange":{"start":{"line":1,"character":6},"end":{"line":1,"character":11}}},{"kind":6}]},
                 {"name":"Flat","kind":12,"location":{"uri":"file:///x.cs","range":{"start":{"line":4,"character":0},"end":{"line":5,"character":0}}}}]
                """);
        }

        var symbols = await call.WaitAsync(Patience);
        var names = symbols.SelectMany(s => s.Flatten()).Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "Outer", "Inner", "Flat" }, names);
        Assert.Equal(SymbolKind.Class, symbols[0].Kind);
        Assert.Equal("class", symbols[0].Detail);
        Assert.Equal(new LspPosition(0, 6), symbols[0].SelectionRange.Start);
        Assert.Equal(SymbolKind.Function, symbols[1].Kind);
        Assert.Equal(new LspRange(new LspPosition(4, 0), new LspPosition(5, 0)), symbols[1].Range);
    }

    [Fact]
    public async Task Definition_accepts_a_single_location_and_location_links()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"definitionProvider\":true,\"referencesProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("def.cs"));

        var single = client.RequestDefinitionAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/definition"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"uri":"file:///a.cs","range":{"start":{"line":1,"character":2},"end":{"line":1,"character":4}}}""");
        }

        var location = Assert.Single(await single.WaitAsync(Patience));
        Assert.Equal("file:///a.cs", location.Uri);
        Assert.Equal(new LspRange(new LspPosition(1, 2), new LspPosition(1, 4)), location.Range);

        var links = client.RequestDefinitionAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/definition"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """
                [{"targetUri":"file:///b.cs","targetRange":{"start":{"line":0,"character":0},"end":{"line":9,"character":0}},
                  "targetSelectionRange":{"start":{"line":3,"character":4},"end":{"line":3,"character":8}}}]
                """);
        }

        var target = Assert.Single(await links.WaitAsync(Patience));
        Assert.Equal("file:///b.cs", target.Uri);
        Assert.Equal(new LspRange(new LspPosition(3, 4), new LspPosition(3, 8)), target.Range);

        var none = client.RequestReferencesAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/references"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), "null");
        }

        Assert.Empty(await none.WaitAsync(Patience));
    }

    [Fact]
    public async Task Server_error_responses_surface_as_protocol_exceptions()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("err.cs"));

        var call = client.RequestHoverAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await server.RespondErrorAsync(request.RootElement.GetProperty("id"), -32602, "bad position");
        }

        var error = await Assert.ThrowsAsync<LspProtocolException>(() => call.WaitAsync(Patience));
        Assert.Equal("bad position", error.Message);
    }

    [Fact]
    public async Task Cancelling_a_request_sends_cancel_request_for_its_id()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("cancel.cs"));
        using var cts = new CancellationTokenSource();

        var call = client.RequestHoverAsync(document, 0, cts.Token);
        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover");
        var id = request.RootElement.GetProperty("id").GetInt64();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Patience));
        using var cancel = await LspHarness.ReceiveMethodAsync(server, "$/cancelRequest");
        Assert.Equal(id, cancel.RootElement.GetProperty("params").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task A_late_response_to_a_cancelled_request_is_ignored()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var document = new TextDocument("x", dir.PathOf("late.cs"));
        using var cts = new CancellationTokenSource();

        var cancelled = client.RequestHoverAsync(document, 0, cts.Token);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await cts.CancelAsync();
            await server.RespondAsync(request.RootElement.GetProperty("id"), "{\"contents\":\"stale\"}");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));
        var live = client.RequestHoverAsync(document, 0);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), "{\"contents\":\"fresh\"}");
        }

        Assert.Equal("fresh", (await live.WaitAsync(Patience))!.Markup);
        Assert.False(client.IsClosed);
    }

    [Fact]
    public async Task Requests_time_out_after_the_configured_request_timeout()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(
            server,
            "{\"hoverProvider\":true}",
            new LspClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(150) });
        var document = new TextDocument("x", dir.PathOf("timeout.cs"));

        var call = client.RequestHoverAsync(document, 0);
        using var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/hover");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Patience));
        Assert.False(client.IsClosed);
    }

    [Fact]
    public async Task Server_requests_for_configuration_get_one_null_per_item()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);

        await server.RequestAsync(7, "workspace/configuration", """{"items":[{"section":"a"},{"section":"b"},{"section":"c"}]}""");

        using var reply = await server.ReceiveAsync();
        Assert.Equal(7, reply.RootElement.GetProperty("id").GetInt32());
        var result = reply.RootElement.GetProperty("result");
        Assert.Equal(3, result.GetArrayLength());
        Assert.All(result.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.ValueKind));
    }

    [Fact]
    public async Task Server_requests_without_data_get_null_and_unknown_or_unsupported_ones_get_method_not_found()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);

        await server.RequestAsync(1, "client/registerCapability", """{"registrations":[]}""");
        using (var reply = await server.ReceiveAsync())
        {
            Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("result").ValueKind);
        }

        await server.RequestAsync(2, "workspace/applyEdit", """{"edit":{}}""");
        using (var reply = await server.ReceiveAsync())
        {
            Assert.Equal(-32601, reply.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.False(reply.RootElement.TryGetProperty("result", out _));
        }

        await server.RequestAsync(3, "custom/unknown", "{}");
        using (var reply = await server.ReceiveAsync())
        {
            Assert.Equal(-32601, reply.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task Workspace_folder_requests_return_the_configured_folders()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        var process = new LanguageServerProcess(new LanguageServerLaunch("fake"));
        var connecting = LspClient.ConnectAsync(process, server.Streams, new LspClientOptions { WorkspacePath = dir.Root });
        using (var initialize = await server.ReceiveAsync())
        {
            await server.RespondAsync(initialize.RootElement.GetProperty("id"), "{\"capabilities\":{}}");
        }

        using (await server.ReceiveAsync())
        {
        }

        await using var client = await connecting;
        await server.RequestAsync(9, "workspace/workspaceFolders", "null");

        using var reply = await server.ReceiveAsync();
        var folder = Assert.Single(reply.RootElement.GetProperty("result").EnumerateArray());
        Assert.StartsWith("file:", folder.GetProperty("uri").GetString());
        Assert.Equal(Path.GetFileName(dir.Root), folder.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Workspace_refresh_requests_raise_the_refresh_event_and_are_acknowledged()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var refreshed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RefreshRequested += method => refreshed.TrySetResult(method);

        await server.RequestAsync(4, "workspace/semanticTokens/refresh", "null");

        Assert.Equal("workspace/semanticTokens/refresh", await refreshed.Task.WaitAsync(Patience));
        using var reply = await server.ReceiveAsync();
        Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Unknown_notifications_and_stray_responses_are_ignored()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var logged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LogMessage += message => logged.TrySetResult(message);

        await server.NotifyAsync("$/progress", "{\"token\":1}");
        await server.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}");
        await server.NotifyAsync("window/logMessage", "{\"message\":\"still alive\"}");

        Assert.Equal("still alive", await logged.Task.WaitAsync(Patience));
        Assert.False(client.IsClosed);
    }

    [Fact]
    public async Task Closing_the_stream_completes_the_connection_cleanly_and_fails_pending_calls()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var closed = NextClosed(client);
        var document = new TextDocument("x", dir.PathOf("eof.cs"));

        var pending = client.RequestHoverAsync(document, 0);
        using (await LspHarness.ReceiveMethodAsync(server, "textDocument/hover"))
        {
        }

        server.CloseOutput();

        Assert.Null(await closed.WaitAsync(Patience));
        Assert.True(client.IsClosed);
        Assert.False(client.IsAlive);
        await Assert.ThrowsAsync<LspProtocolException>(() => pending.WaitAsync(Patience));
        await Assert.ThrowsAsync<LspProtocolException>(() => client.RequestHoverAsync(document, 0));
    }

    [Fact]
    public async Task Semantic_tokens_decode_the_full_result_then_apply_a_delta()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, SemanticCapabilities);
        var document = new TextDocument("x", dir.PathOf("semantic.cs"));

        var full = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"resultId":"r1","data":[0,0,3,2,0, 0,4,5,3,1, 1,2,1,1,0]}""");
        }

        var first = await full.WaitAsync(Patience);
        Assert.Equal("r1", first.ResultId);
        Assert.Equal(
            new[] { (0, 0, 3, "class"), (0, 4, 5, "method"), (1, 2, 1, "type") },
            first.Tokens.Select(t => (t.Line, t.StartChar, t.Length, t.TypeName)).ToArray());
        Assert.Equal(new[] { "declaration" }, first.ModifierNames(first.Tokens[1]));

        var delta = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full/delta"))
        {
            Assert.Equal("r1", request.RootElement.GetProperty("params").GetProperty("previousResultId").GetString());
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"resultId":"r2","edits":[{"start":5,"deleteCount":5,"data":[0,4,2,3,0]}]}""");
        }

        var second = await delta.WaitAsync(Patience);
        Assert.Equal("r2", second.ResultId);
        Assert.Equal(
            new[] { (0, 0, 3, "class"), (0, 4, 2, "method"), (1, 2, 1, "type") },
            second.Tokens.Select(t => (t.Line, t.StartChar, t.Length, t.TypeName)).ToArray());
    }

    [Fact]
    public async Task Failed_delta_falls_back_to_a_full_request()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, SemanticCapabilities);
        var document = new TextDocument("x", dir.PathOf("fallback.cs"));

        var full = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"resultId":"a","data":[0,0,1,0,0]}""");
        }

        await full.WaitAsync(Patience);

        var delta = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full/delta"))
        {
            await server.RespondErrorAsync(request.RootElement.GetProperty("id"), -32803, "stale");
        }

        using (var retry = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full"))
        {
            await server.RespondAsync(retry.RootElement.GetProperty("id"), """{"resultId":"b","data":[0,0,2,0,0,1,0,1,0,0]}""");
        }

        var result = await delta.WaitAsync(Patience);
        Assert.Equal("b", result.ResultId);
        Assert.Equal(2, result.Tokens.Count);
    }

    [Fact]
    public async Task Closing_a_document_forgets_its_semantic_state_so_the_next_request_is_full()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, SemanticCapabilities);
        var document = new TextDocument("x", dir.PathOf("forget.cs"));

        var first = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"resultId":"r1","data":[]}""");
        }

        await first.WaitAsync(Patience);
        await client.DidCloseAsync(document);

        var second = client.RequestSemanticTokensAsync(document);
        using (var request = await LspHarness.ReceiveMethodAsync(server, "textDocument/semanticTokens/full"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), """{"resultId":"r2","data":[]}""");
        }

        Assert.Equal("r2", (await second.WaitAsync(Patience)).ResultId);
    }

    [Fact]
    public async Task Semantic_tokens_without_a_provider_are_empty_and_send_nothing()
    {
        using var dir = new TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var document = new TextDocument("x", dir.PathOf("none.cs"));

        var result = await client.RequestSemanticTokensAsync(document);

        Assert.Empty(result.Tokens);
        Assert.Null(result.ResultId);
    }

    [Fact]
    public async Task Shutdown_sends_shutdown_then_exit_and_is_a_no_op_once_closed()
    {
        await using var server = new FakeLanguageServer();
        var client = await LspHarness.ConnectAsync(server);

        var shutdown = client.ShutdownAsync();
        using (var request = await LspHarness.ReceiveMethodAsync(server, "shutdown"))
        {
            await server.RespondAsync(request.RootElement.GetProperty("id"), "null");
        }

        await shutdown.WaitAsync(Patience);
        using (var exit = await LspHarness.ReceiveMethodAsync(server, "exit"))
        {
            Assert.Equal("exit", exit.RootElement.GetProperty("method").GetString());
        }

        await client.DisposeAsync();
        await client.ShutdownAsync();
        Assert.True(client.IsClosed);
    }

    [Fact]
    public async Task Disposing_the_client_is_idempotent_and_marks_it_closed()
    {
        await using var server = new FakeLanguageServer();
        var client = await LspHarness.ConnectAsync(server);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.True(client.IsClosed);
    }

    private static Task<DiagnosticSnapshot> NextDiagnostics(DiagnosticsStore store)
    {
        var completion = new TaskCompletionSource<DiagnosticSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DiagnosticsChangedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            store.Changed -= handler;
            completion.TrySetResult(args.Snapshot);
        };
        store.Changed += handler;
        return completion.Task;
    }

    private static Task<Exception?> NextClosed(LspClient client)
    {
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += (_, args) => completion.TrySetResult(args.Reason);
        return completion.Task;
    }
}
