using System.Text;
using Forge.Editor.Lsp;
using Forge.Editor.Tests.Support;

namespace Forge.Editor.Tests.Lsp;

public class JsonRpcFramingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Content_length_counts_utf8_bytes_not_characters()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = Capture(client);
        const string message = "naïve ✓ 😀 café";

        await server.NotifyAsync("window/logMessage", $"{{\"message\":\"{message}\"}}");

        Assert.Equal(message, await logged.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Frames_delivered_one_byte_at_a_time_are_reassembled()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = Capture(client);
        var frame = FakeLanguageServer.Frame("{\"jsonrpc\":\"2.0\",\"method\":\"window/logMessage\",\"params\":{\"message\":\"slow ✓\"}}");

        foreach (var b in frame)
        {
            await server.SendRawAsync(new[] { b });
        }

        Assert.Equal("slow ✓", await logged.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Two_frames_in_one_write_are_both_dispatched_in_order()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var messages = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LogMessage += text =>
        {
            lock (messages)
            {
                messages.Add(text);
                if (messages.Count == 2)
                {
                    done.TrySetResult();
                }
            }
        };
        var combined = FakeLanguageServer.Frame("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"first\"}}")
            .Concat(FakeLanguageServer.Frame("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"second\"}}"))
            .ToArray();

        await server.SendRawAsync(combined);

        await done.Task.WaitAsync(Patience);
        Assert.Equal(new[] { "first", "second" }, messages);
    }

    [Fact]
    public async Task Extra_headers_and_lower_case_names_are_accepted()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = Capture(client);
        var body = Encoding.UTF8.GetBytes("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"lenient\"}}");
        var header = Encoding.ASCII.GetBytes($"content-type: application/vscode-jsonrpc; charset=utf-8\r\ncontent-length:{body.Length}\r\n\r\n");

        await server.SendRawAsync(header.Concat(body).ToArray());

        Assert.Equal("lenient", await logged.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_bare_lf_line_ending_in_the_header_is_accepted()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = Capture(client);
        var body = Encoding.UTF8.GetBytes("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"lf only\"}}");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\n\n");

        await server.SendRawAsync(header.Concat(body).ToArray());

        Assert.Equal("lf only", await logged.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Oversized_header_section_closes_the_connection_with_a_protocol_error()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var closed = NextClosed(client);

        await server.SendRawAsync(Encoding.ASCII.GetBytes(new string('A', 9000)));

        var reason = await closed.WaitAsync(Patience);
        var protocol = Assert.IsType<LspProtocolException>(reason);
        Assert.Contains("too long", protocol.Message);
        Assert.True(client.IsClosed);
    }

    [Theory]
    [InlineData("Content-Length: abc\r\n\r\n{}")]
    [InlineData("Content-Length: -4\r\n\r\n{}")]
    [InlineData("Content-Type: text/plain\r\n\r\n{}")]
    public async Task Malformed_or_missing_content_length_closes_the_connection(string frame)
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var closed = NextClosed(client);

        await server.SendRawAsync(Encoding.ASCII.GetBytes(frame));

        var reason = await closed.WaitAsync(Patience);
        Assert.IsType<LspProtocolException>(reason);
        Assert.True(client.IsClosed);
    }

    [Fact]
    public async Task Body_that_is_not_json_closes_the_connection()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var closed = NextClosed(client);
        var body = Encoding.UTF8.GetBytes("not json at all");

        await server.SendRawAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray());

        Assert.IsAssignableFrom<Exception>(await closed.WaitAsync(Patience));
        Assert.True(client.IsClosed);
    }

    [Fact]
    public async Task End_of_stream_inside_a_body_is_a_protocol_error()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var closed = NextClosed(client);

        await server.SendRawAsync(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\n{\"method\":"));
        server.CloseOutput();

        var reason = await closed.WaitAsync(Patience);
        var protocol = Assert.IsType<LspProtocolException>(reason);
        Assert.Contains("end of stream", protocol.Message);
    }

    [Fact]
    public async Task End_of_stream_between_messages_is_a_clean_close()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var closed = NextClosed(client);

        server.CloseOutput();

        Assert.Null(await closed.WaitAsync(Patience));
    }

    [Fact]
    public async Task Header_split_across_writes_while_a_request_is_sent_is_still_parsed()
    {
        using var dir = new Support.TempDirectory();
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server, "{\"hoverProvider\":true}");
        var logged = Capture(client);
        var document = new Forge.Editor.Text.TextDocument("x", dir.PathOf("race.cs"));

        await server.SendRawAsync(Encoding.ASCII.GetBytes("Content-Length: 2"));
        var hover = client.RequestHoverAsync(document, 0);
        using var request = await server.ReceiveAsync();
        Assert.Equal("textDocument/hover", request.RootElement.GetProperty("method").GetString());

        await server.SendRawAsync(Encoding.ASCII.GetBytes("\r\n\r\n{}"));
        await server.RespondAsync(request.RootElement.GetProperty("id"), "null");
        await server.NotifyAsync("window/logMessage", "{\"message\":\"after split\"}");

        Assert.Equal("after split", await logged.Task.WaitAsync(Patience));
        Assert.Null(await hover.WaitAsync(Patience));
    }

    [Fact]
    public async Task Frame_with_a_body_split_in_the_middle_is_reassembled()
    {
        await using var server = new FakeLanguageServer();
        await using var client = await LspHarness.ConnectAsync(server);
        var logged = Capture(client);
        var frame = FakeLanguageServer.Frame("{\"method\":\"window/logMessage\",\"params\":{\"message\":\"halves\"}}");
        var middle = frame.Length - 12;

        await server.SendRawAsync(frame.AsMemory(0, middle));
        await Task.Delay(20);
        await server.SendRawAsync(frame.AsMemory(middle));

        Assert.Equal("halves", await logged.Task.WaitAsync(Patience));
    }

    private static TaskCompletionSource<string> Capture(LspClient client)
    {
        var logged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LogMessage += message => logged.TrySetResult(message);
        return logged;
    }

    private static Task<Exception?> NextClosed(LspClient client)
    {
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += (_, args) => completion.TrySetResult(args.Reason);
        return completion.Task;
    }
}
