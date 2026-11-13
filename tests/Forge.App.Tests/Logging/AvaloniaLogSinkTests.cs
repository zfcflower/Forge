using Avalonia.Logging;
using Forge.App.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Forge.App.Tests.Logging;

public sealed class AvaloniaLogSinkTests
{
    [Fact]
    public void A_warning_keeps_its_level_and_logs_under_its_area_with_the_template_filled()
    {
        using var capture = new LogCapture();
        var sink = new AvaloniaLogSink();

        sink.Log(LogEventLevel.Warning, "Binding", null, "Error in binding to {Target}.{Property}: {Message}", "TextBlock", "Text", "not found");
        sink.Log(LogEventLevel.Error, "Layout", new object(), "Measure failed");

        Assert.Equal(
            [("Binding", LogLevel.Warning, "Error in binding to TextBlock.Text: not found"), ("Layout", LogLevel.Error, "Measure failed (Object)")],
            capture.Entries.Select(static e => (e.Category, e.Level, e.Message)).ToArray());
    }

    [Fact]
    public void Levels_below_the_minimum_are_neither_enabled_nor_logged()
    {
        using var capture = new LogCapture();
        var sink = new AvaloniaLogSink();

        Assert.False(sink.IsEnabled(LogEventLevel.Information, "Binding"));
        Assert.True(sink.IsEnabled(LogEventLevel.Warning, "Binding"));
        sink.Log(LogEventLevel.Information, "Binding", null, "noise");
        sink.Log(LogEventLevel.Debug, "Binding", null, "noise");

        Assert.Empty(capture.Entries);
    }

    [Fact]
    public void The_app_minimum_level_also_gates_the_sink()
    {
        using var capture = new LogCapture(LogLevel.Error);
        var sink = new AvaloniaLogSink();

        Assert.False(sink.IsEnabled(LogEventLevel.Warning, "Binding"));
        Assert.True(sink.IsEnabled(LogEventLevel.Fatal, "Binding"));
    }

    public static TheoryData<string, object?[], string> Templates => new()
    {
        { "plain", [], "plain" },
        { "{A} and {B}", [1], "1 and {B}" },
        { "{A}", [null], "" },
        { "unclosed {A", [1], "unclosed {A" },
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Templates_fill_holes_left_to_right_and_keep_what_has_no_value(string template, object?[] values, string expected)
        => Assert.Equal(expected, AvaloniaLogSink.Render(template, values));

    [Theory]
    [InlineData(LogEventLevel.Verbose, LogLevel.Trace)]
    [InlineData(LogEventLevel.Debug, LogLevel.Debug)]
    [InlineData(LogEventLevel.Information, LogLevel.Information)]
    [InlineData(LogEventLevel.Warning, LogLevel.Warning)]
    [InlineData(LogEventLevel.Error, LogLevel.Error)]
    [InlineData(LogEventLevel.Fatal, LogLevel.Critical)]
    public void Levels_map_one_to_one(LogEventLevel avalonia, LogLevel expected)
        => Assert.Equal(expected, AvaloniaLogSink.Map(avalonia));
}
