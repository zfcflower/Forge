using System.Text.Json;
using Forge.Mcp.Tests.Fixtures;
using Xunit;

namespace Forge.Mcp.Tests.Client;

public class McpClientListAndResultTests
{
    private static string ToolPage(string name, string? nextCursor)
    {
        var tool = "{\"name\":\"" + name + "\",\"inputSchema\":{\"type\":\"object\"}}";
        return nextCursor is null
            ? "{\"tools\":[" + tool + "]}"
            : "{\"tools\":[" + tool + "],\"nextCursor\":\"" + nextCursor + "\"}";
    }

    [Fact]
    public async Task ListToolsAsync_follows_next_cursor_across_pages_and_passes_the_cursor_back()
    {
        var pages = new Dictionary<string, string>
        {
            [""] = ToolPage("one", "c1"),
            ["c1"] = ToolPage("two", "c2"),
            ["c2"] = ToolPage("three", null),
        };
        var server = new ScriptedServer().On(McpProtocol.ToolsList, root =>
        {
            var parameters = JsonRpc.ParamsOf(root);
            var cursor = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out var value)
                ? value.GetString()!
                : string.Empty;
            return pages[cursor];
        });
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(new[] { "one", "two", "three" }, tools.Select(tool => tool.Name));
        var listRequests = transport.Sent.Where(message => JsonRpc.MethodOf(message) == McpProtocol.ToolsList).ToList();
        Assert.Equal(3, listRequests.Count);
        Assert.DoesNotContain("cursor", listRequests[0]);
        Assert.Contains("\"cursor\":\"c1\"", listRequests[1]);
        Assert.Contains("\"cursor\":\"c2\"", listRequests[2]);
    }

    [Fact]
    public async Task ListToolsAsync_stops_at_max_list_pages_even_when_the_server_keeps_returning_cursors()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => ToolPage("loop", "again"));
        var options = new McpClientOptions { MaxListPages = 2, RequestTimeout = TimeSpan.FromSeconds(5) };
        var (client, transport) = await Clients.ConnectLoopbackAsync(server, options);
        await using var connection = client;

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(2, tools.Count);
        Assert.Equal(2, transport.Sent.Count(message => JsonRpc.MethodOf(message) == McpProtocol.ToolsList));
    }

    [Fact]
    public async Task Zero_max_list_pages_still_requests_one_page()
    {
        var options = new McpClientOptions { MaxListPages = 0, RequestTimeout = TimeSpan.FromSeconds(5) };
        var (client, _) = await Clients.ConnectLoopbackAsync(options: options);
        await using var connection = client;

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Single(tools);
    }

    [Fact]
    public async Task Empty_next_cursor_ends_pagination()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => ToolPage("only", ""));
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var tools = await client.ListToolsAsync(CancellationToken.None);

        Assert.Single(tools);
        Assert.Equal(1, transport.Sent.Count(message => JsonRpc.MethodOf(message) == McpProtocol.ToolsList));
    }

    [Fact]
    public async Task EnumerateToolsAsync_fetches_pages_lazily()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, root =>
        {
            var parameters = JsonRpc.ParamsOf(root);
            return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out _)
                ? ToolPage("second", null)
                : ToolPage("first", "next");
        });
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        await using var enumerator = client.EnumerateToolsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", enumerator.Current.Name);
        Assert.Equal(1, transport.Sent.Count(message => JsonRpc.MethodOf(message) == McpProtocol.ToolsList));

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("second", enumerator.Current.Name);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Tool_without_input_schema_gets_an_empty_object_schema_and_schema_is_passed_through_compacted()
    {
        const string pageJson = """
            {"tools":[
              {"name":"bare","description":"no schema"},
              {"name":"typed","title":"Typed tool","inputSchema":{
                  "type": "object",
                  "properties": { "q": { "type": "string" } },
                  "required": ["q"]
              }}
            ]}
            """;
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => pageJson);
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var tools = await client.ListToolsAsync(CancellationToken.None);

        var bare = tools[0];
        Assert.Equal("bare", bare.Name);
        Assert.Equal("no schema", bare.Description);
        Assert.Equal("""{"type":"object","properties":{}}""", bare.InputSchemaJson);
        Assert.Null(bare.Title);
        var typed = tools[1];
        Assert.Equal("Typed tool", typed.Title);
        Assert.Null(typed.Description);
        Assert.Equal("""{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""", typed.InputSchemaJson);
    }

    [Fact]
    public async Task Tool_with_null_input_schema_falls_back_to_the_empty_object_schema()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => """{"tools":[{"name":"nul","inputSchema":null}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var tool = Assert.Single(await client.ListToolsAsync(CancellationToken.None));

        Assert.Equal("""{"type":"object","properties":{}}""", tool.InputSchemaJson);
    }

    [Fact]
    public async Task Tool_without_name_is_a_protocol_error()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => """{"tools":[{"description":"anonymous"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.ListToolsAsync(CancellationToken.None));

        Assert.Equal(McpProtocol.ToolsList, error.Method);
        Assert.Equal(McpErrorCodes.InvalidParams, error.Code);
    }

    [Fact]
    public async Task List_result_without_the_array_property_yields_no_items()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsList, _ => """{"tools":"not-an-array"}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.Empty(await client.ListToolsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListResourcesAsync_and_ListPromptsAsync_parse_their_fields_and_argument_flags()
    {
        var server = new ScriptedServer()
            .On(McpProtocol.ResourcesList, _ => """{"resources":[{"uri":"file:///a.txt","name":"a","description":"A","mimeType":"text/plain","size":42},{"uri":"file:///b"}]}""")
            .On(McpProtocol.PromptsList, _ => """{"prompts":[{"name":"review","description":"Review code","arguments":[{"name":"path","description":"file","required":true},{"name":"style"}]},{"name":"plain"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var resources = await client.ListResourcesAsync(CancellationToken.None);
        var prompts = await client.ListPromptsAsync(CancellationToken.None);

        Assert.Equal(new McpResourceInfo("file:///a.txt", "a", "A", "text/plain", 42), resources[0]);
        Assert.Equal(new McpResourceInfo("file:///b"), resources[1]);
        Assert.Equal("review", prompts[0].Name);
        Assert.Equal("Review code", prompts[0].Description);
        Assert.NotNull(prompts[0].Arguments);
        Assert.Equal(new McpPromptArgument("path", "file", true), prompts[0].Arguments![0]);
        Assert.Equal(new McpPromptArgument("style", null, false), prompts[0].Arguments![1]);
        Assert.Null(prompts[1].Arguments);
    }

    [Fact]
    public async Task Prompt_without_name_is_a_protocol_error()
    {
        var server = new ScriptedServer().On(McpProtocol.PromptsList, _ => """{"prompts":[{"description":"x"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.ListPromptsAsync(CancellationToken.None));

        Assert.Equal(McpProtocol.PromptsList, error.Method);
    }

    [Fact]
    public async Task Resource_without_uri_is_a_protocol_error()
    {
        var server = new ScriptedServer().On(McpProtocol.ResourcesList, _ => """{"resources":[{"name":"x"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        await Assert.ThrowsAsync<McpProtocolException>(() => client.ListResourcesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Resource_size_that_is_not_an_integer_is_reported_as_unknown()
    {
        var server = new ScriptedServer().On(McpProtocol.ResourcesList, _ => """{"resources":[{"uri":"x://y","size":"big"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var resource = Assert.Single(await client.ListResourcesAsync(CancellationToken.None));

        Assert.Null(resource.Size);
    }

    [Fact]
    public async Task CallToolAsync_flattens_a_single_text_block()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """{"content":[{"type":"text","text":"hello"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal("hello", result.Text);
        Assert.False(result.IsError);
        var block = Assert.Single(result.Content!);
        Assert.Equal(new McpContent("text", "hello"), block);
    }

    [Fact]
    public async Task CallToolAsync_joins_multiple_blocks_with_newlines_and_keeps_order()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """{"content":[{"type":"text","text":"first"},{"type":"text","text":"second"},{"type":"text"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal("first\nsecond\n", result.Text);
        Assert.Equal(3, result.Content!.Count);
    }

    [Fact]
    public async Task CallToolAsync_describes_image_and_audio_blocks_with_estimated_size()
    {
        var data = new string('A', 4096);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => $$"""{"content":[{"type":"image","data":"{{data}}","mimeType":"image/png"},{"type":"audio","data":"{{data}}"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal("[image: image/png, 3 KiB]\n[audio: unknown type, 3 KiB]", result.Text);
        Assert.Equal("image/png", result.Content![0].MimeType);
        Assert.Equal(3072, result.Content[0].SizeBytes);
        Assert.Equal("audio", result.Content[1].Type);
    }

    [Fact]
    public async Task CallToolAsync_describes_embedded_text_and_blob_resources()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """
            {"content":[
              {"type":"resource","resource":{"uri":"file:///notes.md","mimeType":"text/markdown","text":"# Notes"}},
              {"type":"resource","resource":{"uri":"file:///blob.bin","mimeType":"application/octet-stream","blob":"AAAAAAAA"}},
              {"type":"resource","resource":"broken"}
            ]}
            """);
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal(
            "[resource file:///notes.md]\n# Notes\n[resource: file:///blob.bin (application/octet-stream, 6 B)]\n[resource: malformed]",
            result.Text);
        Assert.Equal("file:///notes.md", result.Content![0].Uri);
        Assert.Equal(7, result.Content[0].SizeBytes);
        Assert.Equal(6, result.Content[1].SizeBytes);
    }

    [Fact]
    public async Task CallToolAsync_describes_resource_links_with_optional_parts()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """
            {"content":[
              {"type":"resource_link","uri":"https://example.test/doc","name":"Doc","mimeType":"text/html","description":"Spec page"},
              {"type":"resource_link","uri":"file:///bare"}
            ]}
            """);
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.Equal(
            "[resource_link: Doc (https://example.test/doc, text/html)] — Spec page\n[resource_link: file:///bare (file:///bare)]",
            result.Text);
    }

    [Fact]
    public async Task CallToolAsync_truncates_unknown_block_payloads_at_512_characters()
    {
        var padding = new string('x', 600);
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => $$"""{"content":[{"type":"widget","payload":"{{padding}}"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("echo", null, CancellationToken.None);

        Assert.StartsWith("[widget: {\"type\":\"widget\",\"payload\":\"", result.Text);
        Assert.EndsWith("…]", result.Text);
        Assert.True(result.Text.Length < 600);
    }

    [Fact]
    public async Task CallToolAsync_reports_is_error_and_defaults_to_false_when_absent()
    {
        var server = new ScriptedServer()
            .On(McpProtocol.ToolsCall, root => JsonRpc.ParamsOf(root).GetProperty("name").GetString() == "fails"
                ? """{"content":[{"type":"text","text":"disk full"}],"isError":true}"""
                : """{"content":[{"type":"text","text":"fine"}],"isError":"true"}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var failed = await client.CallToolAsync("fails", null, CancellationToken.None);
        var notFlagged = await client.CallToolAsync("other", null, CancellationToken.None);

        Assert.True(failed.IsError);
        Assert.Equal("disk full", failed.Text);
        Assert.False(notFlagged.IsError);
    }

    [Fact]
    public async Task CallToolAsync_uses_structured_content_only_when_there_are_no_content_blocks()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, root => JsonRpc.ParamsOf(root).GetProperty("name").GetString() == "structured"
            ? """{"content":[],"structuredContent":{"answer":42}}"""
            : """{"content":[{"type":"text","text":"text wins"}],"structuredContent":{"answer":42}}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        Assert.Equal("""{"answer":42}""", (await client.CallToolAsync("structured", null, CancellationToken.None)).Text);
        Assert.Equal("text wins", (await client.CallToolAsync("both", null, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task CallToolAsync_with_no_content_returns_empty_text()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => "{}");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("quiet", null, CancellationToken.None);

        Assert.Equal(string.Empty, result.Text);
        Assert.Empty(result.Content!);
    }

    [Fact]
    public async Task CallToolAsync_treats_non_object_blocks_as_unknown_content()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => """{"content":["plain string",42]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.CallToolAsync("odd", null, CancellationToken.None);

        Assert.Equal("plain string\n42", result.Text);
        Assert.All(result.Content!, block => Assert.Equal("unknown", block.Type));
    }

    [Fact]
    public async Task CallToolAsync_sends_arguments_compacted_and_defaults_to_an_empty_object()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;

        await client.CallToolAsync("echo", "{\n  \"text\": \"hi\",\n  \"n\": 2\n}", CancellationToken.None);
        await client.CallToolAsync("echo", null, CancellationToken.None);
        await client.CallToolAsync("echo", "   ", CancellationToken.None);

        var calls = transport.Sent.Where(message => JsonRpc.MethodOf(message) == McpProtocol.ToolsCall).ToList();
        using (var first = JsonDocument.Parse(calls[0]))
        {
            Assert.Equal("echo", first.RootElement.GetProperty("params").GetProperty("name").GetString());
            Assert.Equal("""{"text":"hi","n":2}""", first.RootElement.GetProperty("params").GetProperty("arguments").GetRawText());
        }

        Assert.Contains("\"arguments\":{}", calls[1]);
        Assert.Contains("\"arguments\":{}", calls[2]);
    }

    [Fact]
    public async Task CallToolAsync_rejects_blank_names_before_sending()
    {
        var (client, transport) = await Clients.ConnectLoopbackAsync();
        await using var connection = client;
        var before = transport.Sent.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => client.CallToolAsync(" ", null, CancellationToken.None));

        Assert.Equal(before, transport.Sent.Count);
    }

    [Fact]
    public async Task Call_result_without_result_object_is_a_protocol_error()
    {
        var server = new ScriptedServer().On(McpProtocol.ToolsCall, _ => "[]");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("echo", null, CancellationToken.None));

        Assert.Equal(McpProtocol.ToolsCall, error.Method);
        Assert.Equal(McpErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task ReadResourceAsync_renders_spec_shaped_text_contents_as_their_text()
    {
        var server = new ScriptedServer().On(McpProtocol.ResourcesRead, _ => """{"contents":[{"uri":"file:///a","mimeType":"text/plain","text":"alpha"}]}""");
        var (client, _) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.ReadResourceAsync("file:///a", CancellationToken.None);

        Assert.Contains("alpha", result.Text);
        Assert.DoesNotContain("[unknown", result.Text);
    }

    [Fact]
    public async Task ReadResourceAsync_sends_the_uri_and_keeps_the_contents_list()
    {
        var server = new ScriptedServer().On(McpProtocol.ResourcesRead, _ => """{"contents":[{"type":"text","uri":"file:///a","text":"alpha"},{"type":"text","text":"beta"}]}""");
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.ReadResourceAsync("file:///a", CancellationToken.None);

        Assert.Equal("alpha\nbeta", result.Text);
        Assert.Equal(2, result.Content!.Count);
        Assert.Contains(transport.Sent, message => message.Contains("\"uri\":\"file:///a\""));
    }

    [Fact]
    public async Task GetPromptAsync_sends_non_empty_arguments_and_flattens_messages()
    {
        var server = new ScriptedServer().On(McpProtocol.PromptsGet, _ => """
            {"description":"Review","messages":[
              {"role":"user","content":{"type":"text","text":"Check {{path}}"}},
              {"role":"assistant","content":[{"type":"text","text":"ok"},{"type":"text","text":"done"}]},
              {"content":{"type":"text","text":"no role"}}
            ]}
            """);
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.GetPromptAsync("review", new Dictionary<string, string?> { ["path"] = "src", ["empty"] = null }, CancellationToken.None);

        Assert.Equal("Review", result.Description);
        Assert.Equal("user: Check {{path}}\nassistant: ok\ndone\nuser: no role", result.Text);
        Assert.Equal(new[] { "user", "assistant", "user" }, result.Messages!.Select(message => message.Role));
        var request = transport.Sent.Single(message => JsonRpc.MethodOf(message) == McpProtocol.PromptsGet);
        Assert.Contains("\"arguments\":{\"path\":\"src\",\"empty\":\"\"}", request);
    }

    [Fact]
    public async Task GetPromptAsync_omits_arguments_when_none_are_given_and_handles_missing_messages()
    {
        var server = new ScriptedServer().On(McpProtocol.PromptsGet, _ => """{"description":"Bare"}""");
        var (client, transport) = await Clients.ConnectLoopbackAsync(server);
        await using var connection = client;

        var result = await client.GetPromptAsync("bare", null, CancellationToken.None);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal("Bare", result.Description);
        Assert.Null(result.Messages);
        var request = transport.Sent.Single(message => JsonRpc.MethodOf(message) == McpProtocol.PromptsGet);
        Assert.DoesNotContain("arguments", request);
    }
}
