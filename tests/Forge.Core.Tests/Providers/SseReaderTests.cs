using System.Text;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class SseReaderTests
{
    [Fact]
    public async Task Reads_data_lines_across_buffer_boundaries()
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"text\":\"héllo 世界\"}\n\n");
        var reader = new SseReader(new ChunkedStream(bytes.Select(b => new[] { b })), bufferSize: 128);

        var evt = await reader.ReadAsync(CancellationToken.None);

        Assert.True(evt.HasValue);
        Assert.Null(evt!.Value.Event);
        Assert.Equal("{\"text\":\"héllo 世界\"}", evt.Value.Data);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Joins_multiline_data_and_keeps_event_name()
    {
        var stream = new ChunkedStream(
        [
            Encoding.UTF8.GetBytes("event: content_block_delta\r\ndata: {\"a\":1}\r\ndata: {\"b\":2}\r\n\r\n"),
            Encoding.UTF8.GetBytes(": keep-alive comment\n\n"),
            Encoding.UTF8.GetBytes("data: last\n\n"),
        ]);

        var reader = new SseReader(stream);

        var first = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("content_block_delta", first!.Value.Event);
        Assert.Equal("{\"a\":1}\n{\"b\":2}", first.Value.Data);

        var second = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("last", second!.Value.Data);

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Flushes_trailing_event_without_blank_line()
    {
        var reader = new SseReader(new ChunkedStream([Encoding.UTF8.GetBytes("data: [DONE]")]));

        var evt = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal("[DONE]", evt!.Value.Data);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dispatches_empty_data_field()
    {
        var reader = new SseReader(new ChunkedStream([Encoding.UTF8.GetBytes("data\n\ndata: value\n\n")]));

        var evt = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(string.Empty, evt!.Value.Data);

        var next = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal("value", next!.Value.Data);
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        var reader = new SseReader(new HangingStream(Encoding.UTF8.GetBytes(": ping\n\n")));
        using var cts = new CancellationTokenSource();

        var pending = reader.ReadAsync(cts.Token).AsTask();
        await Task.Delay(50, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
