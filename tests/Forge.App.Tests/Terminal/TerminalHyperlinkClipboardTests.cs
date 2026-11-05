using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers OSC 8 hyperlinks (id per URI, empty URI ends the link) and OSC 52 clipboard writes
/// (base64 payload delivered, read queries ignored without answering).
/// </summary>
public sealed class TerminalHyperlinkClipboardTests
{
    [Fact]
    public void Osc8_marks_printed_cells_with_a_hyperlink_id()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b]8;;https://example.com\u0007link\u001b]8;;\u0007plain");

        int id = VtHelpers.ScreenCell(emulator, 0, 0).HyperlinkId;
        Assert.True(id > 0);
        Assert.Equal("https://example.com", emulator.GetHyperlink(id));
        Assert.Equal(id, VtHelpers.ScreenCell(emulator, 0, 1).HyperlinkId);
        Assert.Equal(id, VtHelpers.ScreenCell(emulator, 0, 3).HyperlinkId);
        Assert.Equal(0, VtHelpers.ScreenCell(emulator, 0, 4).HyperlinkId);
        Assert.Equal("linkplain   ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Osc8_reuses_the_id_for_a_known_uri()
    {
        var emulator = VtHelpers.Create(20, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b]8;;https://a.example\u0007one\u001b]8;;\u0007 ");
        VtHelpers.Feed(emulator, "\u001b]8;;https://a.example\u0007two");

        int first = VtHelpers.ScreenCell(emulator, 0, 0).HyperlinkId;
        int second = VtHelpers.ScreenCell(emulator, 0, 4).HyperlinkId;
        Assert.True(first > 0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Osc8_distinguishes_uris_and_rejects_unknown_ids()
    {
        var emulator = VtHelpers.Create(20, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b]8;;https://a.example\u0007a\u001b]8;;\u0007\u001b]8;;https://b.example\u0007b\u001b]8;;\u0007");

        int first = VtHelpers.ScreenCell(emulator, 0, 0).HyperlinkId;
        int second = VtHelpers.ScreenCell(emulator, 0, 1).HyperlinkId;
        Assert.NotEqual(first, second);
        Assert.Equal("https://b.example", emulator.GetHyperlink(second));
        Assert.Null(emulator.GetHyperlink(0));
        Assert.Null(emulator.GetHyperlink(999999));
    }

    [Fact]
    public void Osc8_ids_stop_at_4096_and_overflow_prints_without_link()
    {
        var emulator = VtHelpers.Create(40, 4, 16, out _);
        for (int i = 0; i < 4097; i++)
        {
            VtHelpers.Feed(emulator, $"\u001b]8;;https://overflow{i}.example\u0007x\u001b]8;;\u0007");
        }

        TerminalCell last = VtHelpers.ScreenCell(emulator, 3, 39);
        Assert.Equal(0, last.HyperlinkId);
        Assert.Null(emulator.GetHyperlink(0));
        Assert.Equal("https://overflow0.example", emulator.GetHyperlink(1));
    }

    [Fact]
    public void Osc8_without_separator_is_ignored()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b]8;https://example.com\u0007X");

        Assert.Equal(0, VtHelpers.ScreenCell(emulator, 0, 0).HyperlinkId);
        Assert.Equal("X           ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Osc52_writes_decoded_text_to_clipboard()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b]52;c;SGVsbG8sIHdvcmxkIQ==\u0007");

        Assert.Equal(new[] { "Hello, world!" }, host.ClipboardWrites);
        Assert.Empty(host.Responses);
    }

    [Fact]
    public void Osc52_query_is_ignored_without_answer_or_write()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b]52;c;?\u0007");

        Assert.Empty(host.ClipboardWrites);
        Assert.Empty(host.Responses);
    }

    [Fact]
    public void Osc52_invalid_base64_is_ignored()
    {
        var emulator = VtHelpers.Create(12, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b]52;c;!!!not-base64!!!\u0007next");

        Assert.Empty(host.ClipboardWrites);
        Assert.Equal("next        ", VtHelpers.RowText(emulator, 0));
    }
}
