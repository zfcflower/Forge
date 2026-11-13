using System.Diagnostics;
using Forge.App.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.App.Tests.Logging;

public sealed class TraceBridgeListenerTests
{
    [Theory]
    [InlineData("[session] opened x", "session", "opened x")]
    [InlineData("[approval-rules]   spaced", "approval-rules", "spaced")]
    [InlineData("no tag here", "trace", "no tag here")]
    [InlineData("[] empty tag", "trace", "[] empty tag")]
    [InlineData("[not a tag: {x}] y", "trace", "[not a tag: {x}] y")]
    [InlineData("[unterminated", "trace", "[unterminated")]
    public void Parse_splits_a_leading_tag(string message, string category, string text)
        => Assert.Equal((category, text), TraceBridgeListener.Parse(message));

    [Fact]
    public void Parse_rejects_an_overlong_tag()
    {
        var message = "[" + new string('a', 100) + "] x";

        Assert.Equal("trace", TraceBridgeListener.Parse(message).Category);
    }

    [Theory]
    [InlineData(TraceEventType.Warning, LogLevel.Warning)]
    [InlineData(TraceEventType.Error, LogLevel.Error)]
    [InlineData(TraceEventType.Critical, LogLevel.Error)]
    [InlineData(TraceEventType.Information, LogLevel.Information)]
    [InlineData(TraceEventType.Verbose, LogLevel.Information)]
    public void Event_types_map_to_log_levels(TraceEventType type, LogLevel level)
        => Assert.Equal(level, TraceBridgeListener.Map(type));

    [Fact]
    public void WriteLine_logs_under_the_tag_at_information()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        listener.WriteLine("[core] hello");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal(("core", LogLevel.Information, "hello"), (entry.Category, entry.Level, entry.Message));
    }

    [Fact]
    public void TraceEvent_warning_and_error_keep_their_level()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        listener.TraceEvent(null, "src", TraceEventType.Warning, 0, "[hostclient] slow");
        listener.TraceEvent(null, "src", TraceEventType.Error, 0, "plain failure");
        listener.TraceEvent(null, "src", TraceEventType.Error, 0, "[x] {0} and {1}", "a", 2);

        Assert.Equal(
            [
                ("hostclient", LogLevel.Warning, "slow"),
                ("trace", LogLevel.Error, "plain failure"),
                ("x", LogLevel.Error, "a and 2"),
            ],
            capture.Entries.Select(static e => (e.Category, e.Level, e.Message)).ToArray());
    }

    [Fact]
    public void Partial_writes_are_buffered_until_the_line_ends()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        listener.Write("[editor] part one, ");
        listener.Write("part two");
        Assert.Empty(capture.Entries);
        listener.WriteLine(", end");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal("editor", entry.Category);
        Assert.Equal("part one, part two, end", entry.Message);
    }

    [Fact]
    public void A_category_argument_is_kept_in_the_text_and_does_not_change_the_level()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        listener.WriteLine("[Binding] could not resolve", "Warning");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(TraceBridgeListener.DefaultCategory, entry.Category);
        Assert.Equal("Warning: [Binding] could not resolve", entry.Message);
    }

    [Fact]
    public void Blank_lines_are_dropped_and_a_null_message_does_not_throw()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        listener.WriteLine(string.Empty);
        listener.WriteLine((string?)null);
        listener.Write(null);

        Assert.Empty(capture.Entries);
    }

    [Fact]
    public async Task Concurrent_writers_each_produce_whole_lines()
    {
        using var capture = new LogCapture();
        using var listener = new TraceBridgeListener();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                listener.WriteLine($"[w{worker}] line {i}");
            }
        })));

        Assert.Equal(400, capture.Entries.Count);
        Assert.All(capture.Entries, static e => Assert.StartsWith("line ", e.Message));
    }

    [Fact]
    public void Writing_after_the_factory_is_gone_never_reaches_the_trace_caller()
    {
        using var listener = new TraceBridgeListener();
        var capture = new LogCapture();
        capture.Dispose();

        var failure = Record.Exception(() => listener.WriteLine("[x] after the factory is gone"));

        Assert.Null(failure);
    }
}
