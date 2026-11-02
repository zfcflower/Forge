using System.Text;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class SseReaderRegressionTests
{
    [Fact]
    public async Task Lf_crlf_and_bare_cr_terminators_dispatch_the_same_events_when_fed_one_byte_at_a_time()
    {
        var lf = await ReadAsync("data: a\n\ndata: b\n\n", chunkSize: 1);
        var crlf = await ReadAsync("data: a\r\n\r\ndata: b\r\n\r\n", chunkSize: 1);
        var cr = await ReadAsync("data: a\r\rdata: b\r\r", chunkSize: 1);

        Assert.Equal(new[] { "a", "b" }, lf.Select(e => e.Data));
        Assert.Equal(lf, crlf);
        Assert.Equal(lf, cr);
    }

    [Fact]
    public async Task Crlf_split_between_chunks_is_one_line_break()
    {
        var reader = new SseReader(ScriptedStream.FromText("data: a\r\n\r\ndata: b\r\n\r\n", chunkSize: 1));

        var events = await ReadAllAsync(reader);

        Assert.Equal(new[] { "a", "b" }, events.Select(e => e.Data));

        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("data: a\r"),
            Encoding.UTF8.GetBytes("\n"),
            Encoding.UTF8.GetBytes("\r"),
            Encoding.UTF8.GetBytes("\ndata: b\n\n"),
        };
        var split = await ReadAllAsync(new SseReader(new ScriptedStream(chunks)));

        Assert.Equal(new[] { "a", "b" }, split.Select(e => e.Data));
    }

    [Fact]
    public async Task Carriage_return_at_the_end_of_a_chunk_does_not_swallow_the_next_data_line()
    {
        var chunks = new[]
        {
            Encoding.UTF8.GetBytes("data: a\r"),
            Encoding.UTF8.GetBytes("\n"),
            Encoding.UTF8.GetBytes("\r\n"),
        };

        var events = await ReadAllAsync(new SseReader(new ScriptedStream(chunks)));

        Assert.Equal(new[] { "a" }, events.Select(e => e.Data));
    }

    [Fact]
    public async Task Utf8_byte_order_mark_split_across_chunks_is_skipped_at_stream_start()
    {
        var chunks = new[]
        {
            new byte[] { 0xEF },
            new byte[] { 0xBB },
            new byte[] { 0xBF },
            Encoding.UTF8.GetBytes("data: first\n\n"),
        };

        var events = await ReadAllAsync(new SseReader(new ScriptedStream(chunks)));

        var only = Assert.Single(events);
        Assert.Equal("first", only.Data);
    }

    [Fact]
    public async Task Byte_order_mark_in_the_middle_of_the_stream_is_not_stripped()
    {
        var bytes = Encoding.UTF8.GetBytes("data: a\n\n")
            .Concat(new byte[] { 0xEF, 0xBB, 0xBF })
            .Concat(Encoding.UTF8.GetBytes("data: b\n\n"))
            .ToArray();

        var events = await ReadAllAsync(new SseReader(ScriptedStream.FromText(Encoding.UTF8.GetString(bytes), chunkSize: 1)));

        Assert.Equal(new[] { "a" }, events.Select(e => e.Data));
    }

    [Fact]
    public async Task Final_event_without_a_trailing_blank_line_is_dispatched()
    {
        var reader = new SseReader(ScriptedStream.FromText("data: a\n\ndata: tail", chunkSize: 3));

        var events = await ReadAllAsync(reader);

        Assert.Equal(new[] { "a", "tail" }, events.Select(e => e.Data));
    }

    [Fact]
    public async Task Final_line_terminated_by_a_single_newline_is_dispatched()
    {
        var reader = new SseReader(ScriptedStream.FromText("event: done\r\ndata: [DONE]\r\n", chunkSize: 2));

        var events = await ReadAllAsync(reader);

        var evt = Assert.Single(events);
        Assert.Equal("done", evt.Event);
        Assert.Equal("[DONE]", evt.Data);
    }

    [Fact]
    public async Task Multibyte_utf8_split_across_chunks_decodes_intact()
    {
        var reader = new SseReader(ScriptedStream.FromText("data: héllo 世界 🙂\n\n", chunkSize: 1));

        var evt = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("héllo 世界 🙂", evt!.Value.Data);
    }

    private static async Task<List<SseEvent>> ReadAsync(string text, int chunkSize) =>
        await ReadAllAsync(new SseReader(ScriptedStream.FromText(text, chunkSize)));

    private static async Task<List<SseEvent>> ReadAllAsync(SseReader reader)
    {
        var events = new List<SseEvent>();
        while (await reader.ReadAsync(CancellationToken.None) is { } evt)
        {
            events.Add(evt);
        }

        return events;
    }
}
