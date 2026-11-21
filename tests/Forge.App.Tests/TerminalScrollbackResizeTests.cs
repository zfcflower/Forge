using System.Text;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A shorter screen hands its top rows to the scrollback so the prompt at the bottom stays visible — but only the rows
/// that carry text: the blank rows below the content are not history. They used to be pushed as well, which put a blank
/// band into the scrollback on every shrink (a terminal starts at 30 rows and is laid out shorter, so a fresh terminal
/// gained one immediately), and scrolling back showed that band instead of the output above it.
/// </summary>
public sealed class TerminalScrollbackResizeTests
{
    [Fact]
    public void Shrinking_the_screen_pushes_the_content_rows_and_drops_the_blank_ones()
    {
        var emulator = new TerminalEmulator(20, 30, 10000, SilentHost.Instance);
        emulator.Feed(Encoding.UTF8.GetBytes("PS> hello\r\nworld\r\n"));

        emulator.Resize(20, 12);

        Assert.Equal(2, emulator.ScrollbackCount);
        Assert.Equal("PS> hello", Text(emulator, 0));
        Assert.Equal("world", Text(emulator, 1));

        // The 16 blank rows are reported as trimmed, which is what tells a viewer to let go of a screen-row anchor.
        Assert.Equal(16, emulator.ScreenTrimmed);
    }

    [Fact]
    public void Shrinking_an_empty_screen_leaves_the_scrollback_empty()
    {
        var emulator = new TerminalEmulator(20, 30, 10000, SilentHost.Instance);

        emulator.Resize(20, 12);

        Assert.Equal(0, emulator.ScrollbackCount);
        Assert.Equal(12, emulator.Rows);
        Assert.Equal(18, emulator.ScreenTrimmed);
    }

    private static string Text(TerminalEmulator emulator, int line)
    {
        var text = new StringBuilder();
        var row = emulator.GetLine(line);
        for (var column = 0; column < row.Count; column++)
        {
            var rune = row[column].Rune;
            text.Append(rune == 0 ? ' ' : char.ConvertFromUtf32(rune));
        }

        return text.ToString().TrimEnd();
    }

    private sealed class SilentHost : ITerminalEmulatorHost
    {
        public static readonly SilentHost Instance = new();

        public TerminalColor DefaultForeground => default;

        public TerminalColor DefaultBackground => default;

        public void Respond(ReadOnlySpan<byte> bytes)
        {
        }

        public void TitleChanged(string title)
        {
        }

        public void Bell()
        {
        }

        public void ClipboardWrite(string text)
        {
        }
    }
}
