using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers the answer-back paths: DSR 5/6 (including the <c>CSI ? 6</c> form), DA1/DA2,
/// OSC 10/11 color queries against the host defaults, title reporting, BEL, and the
/// guarantee that truncated, unknown or over-long sequences never corrupt the parser:
/// plain text printed afterwards still lands on screen.
/// </summary>
public sealed class TerminalResponsesTests
{
    [Fact]
    public void Dsr_5_answers_device_ok()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b[5n");

        Assert.Single(host.Responses);
        Assert.Equal("\u001b[0n", host.AllResponsesText());
    }

    [Fact]
    public void Dsr_6_answers_cursor_position_one_based()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);
        VtHelpers.Feed(emulator, "\u001b[2;5H");

        VtHelpers.Feed(emulator, "\u001b[6n");

        Assert.Equal("\u001b[2;5R", host.AllResponsesText());
    }

    [Fact]
    public void Dsr_question_6_answers_with_question_prefix()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);
        VtHelpers.Feed(emulator, "\u001b[2;5H");

        VtHelpers.Feed(emulator, "\u001b[?6n");

        Assert.Equal("\u001b[?2;5R", host.AllResponsesText());
    }

    [Fact]
    public void Cpr_answer_can_be_fed_back_without_disturbing_the_screen()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);
        VtHelpers.Feed(emulator, "\u001b[2;5H\u001b[6n");
        string answer = host.AllResponsesText();
        host.Responses.Clear();

        VtHelpers.Feed(emulator, answer);
        VtHelpers.Feed(emulator, "OK");

        Assert.Empty(host.Responses);
        Assert.Equal("    OK    ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 6), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Da1_reports_vt220_identity()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b[c");

        string answer = host.AllResponsesText();
        Assert.StartsWith("\u001b[?", answer);
        Assert.Contains("62", answer);
        Assert.Equal("\u001b[?62;22c", answer);
    }

    [Fact]
    public void Da2_reports_secondary_attributes()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b[>c");

        string answer = host.AllResponsesText();
        Assert.StartsWith("\u001b[>", answer);
        Assert.Contains("10", answer);
        Assert.Equal("\u001b[>0;10;1c", answer);
    }

    [Fact]
    public void Osc_10_and_11_answer_host_colors_as_rgb()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b]10;?\u0007\u001b]11;?\u0007");

        Assert.Equal(
            "\u001b]10;rgb:1111/2222/3333\u001b\\" +
            "\u001b]11;rgb:4444/5555/6666\u001b\\",
            host.AllResponsesText());
    }

    [Fact]
    public void Osc_10_and_11_fall_back_for_default_theme_colors()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);
        host.DefaultForeground = TerminalColor.Default;
        host.DefaultBackground = TerminalColor.Default;

        VtHelpers.Feed(emulator, "\u001b]10;?\u0007\u001b]11;?\u0007");

        Assert.Equal(
            "\u001b]10;rgb:ffff/ffff/ffff\u001b\\" +
            "\u001b]11;rgb:0000/0000/0000\u001b\\",
            host.AllResponsesText());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void Osc_title_codes_report_the_title(string code)
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, $"\u001b]{code};my-title\u0007");

        Assert.Equal("my-title", emulator.Title);
        Assert.Equal(new[] { "my-title" }, host.Titles);
    }

    [Fact]
    public void Osc_title_accepts_st_terminator()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u001b]2;st-title\u001b\\");

        Assert.Equal("st-title", emulator.Title);
        Assert.Equal(new[] { "st-title" }, host.Titles);
    }

    [Fact]
    public void Bel_rings_without_printing()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out VtTestHost host);

        VtHelpers.Feed(emulator, "\u0007");

        Assert.Equal(1, host.BellCount);
        Assert.Equal("          ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 0), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Truncated_csi_resumes_with_the_next_feed()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "AB");

        VtHelpers.Feed(emulator, "\u001b[");
        VtHelpers.Feed(emulator, "2JAfter");

        // ED clears the screen but never moves the cursor: text starts at column 2.
        Assert.Equal("  After   ", VtHelpers.RowText(emulator, 0));
        Assert.Equal("          ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((0, 7), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Csi_with_bare_letter_dispatches_and_keeps_parsing()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[ZAfter");

        Assert.Equal("After     ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 5), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Oversized_parameter_moves_to_edge_without_breaking_parsing()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[99999CHi");

        Assert.Equal("         H", VtHelpers.RowText(emulator, 0));
        Assert.Equal("i         ", VtHelpers.RowText(emulator, 1));
        Assert.Equal((1, 1), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void More_than_32_parameters_ignores_the_whole_sequence()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        VtHelpers.Feed(emulator, "\u001b[31m");
        string overflow = "\u001b[" + string.Join(";", Enumerable.Repeat("1", 33)) + "m";

        VtHelpers.Feed(emulator, overflow + "G");

        // The overflowed SGR was skipped: the earlier red color is still active.
        Assert.Equal(TerminalColor.Palette(1), VtHelpers.ScreenCell(emulator, 0, 0).Foreground);
        Assert.Equal('G', (char)VtHelpers.ScreenCell(emulator, 0, 0).Rune);
    }

    [Fact]
    public void Overflowed_movement_sequence_does_not_move_cursor()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);
        string overflow = "\u001b[?" + string.Join(";", Enumerable.Repeat("1", 33)) + "C";

        VtHelpers.Feed(emulator, overflow + "Hi");

        Assert.Equal("Hi        ", VtHelpers.RowText(emulator, 0));
        Assert.Equal((0, 2), VtHelpers.Cursor(emulator));
    }

    [Fact]
    public void Can_aborts_a_partial_sequence_and_text_continues_plain()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[31\u0018Hi");

        TerminalCell cell = VtHelpers.ScreenCell(emulator, 0, 0);
        Assert.Equal('H', (char)cell.Rune);
        Assert.True(cell.Foreground.IsDefault);
        Assert.Equal("Hi        ", VtHelpers.RowText(emulator, 0));
    }

    [Fact]
    public void Unknown_sequences_are_ignored_and_text_continues()
    {
        var emulator = VtHelpers.Create(10, 4, 16, out _);

        VtHelpers.Feed(emulator, "\u001b[qHi\u001b[999;999;999~Bye");

        Assert.Equal("HiBye     ", VtHelpers.RowText(emulator, 0));
    }
}
