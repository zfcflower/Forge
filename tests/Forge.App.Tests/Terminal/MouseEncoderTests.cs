using System.Text;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers <see cref="MouseEncoder"/>: per-mode filtering (1000/1002/1003), the SGR (1006) and
/// legacy X10 encodings, coordinates past 223, the release <c>m</c> suffix, and modifier bits.
/// </summary>
public sealed class MouseEncoderTests
{
    private static string EncodeSgr(
        MouseEventKind kind, MouseButton button, int column, int row,
        TerminalModifiers modifiers = TerminalModifiers.None, MouseTracking mode = MouseTracking.Normal)
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        Assert.True(MouseEncoder.TryEncode(kind, button, column, row, modifiers, mode, sgr: true, buffer, out int written));
        return Encoding.Latin1.GetString(buffer[..written]);
    }

    private static bool TryEncodeRaw(
        MouseEventKind kind, MouseButton button, int column, int row,
        TerminalModifiers modifiers, MouseTracking mode, bool sgr, Span<byte> destination, out int written) =>
        MouseEncoder.TryEncode(kind, button, column, row, modifiers, mode, sgr, destination, out written);

    [Fact]
    public void No_tracking_mode_rejects_everything()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];

        Assert.False(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 0, 0, TerminalModifiers.None, MouseTracking.None, true, buffer, out _));
    }

    [Fact]
    public void Mode_1000_reports_press_release_wheel_but_not_moves()
    {
        // Press and release encode.
        Assert.Equal("\u001b[<0;6;4M", EncodeSgr(MouseEventKind.Press, MouseButton.Left, 5, 3));
        Assert.Equal("\u001b[<0;6;4m", EncodeSgr(MouseEventKind.Release, MouseButton.Left, 5, 3));
        Assert.Equal("\u001b[<64;6;4M", EncodeSgr(MouseEventKind.Press, MouseButton.WheelUp, 5, 3));

        // Moves, bare moves and wheel releases do not encode.
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        Assert.False(TryEncodeRaw(MouseEventKind.Move, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.Normal, true, buffer, out _));
        Assert.False(TryEncodeRaw(MouseEventKind.Release, MouseButton.WheelUp, 5, 3, TerminalModifiers.None, MouseTracking.Normal, true, buffer, out _));
    }

    [Fact]
    public void Mode_1002_reports_drags_but_not_bare_moves()
    {
        Assert.Equal("\u001b[<32;6;4M", EncodeSgr(MouseEventKind.Move, MouseButton.Left, 5, 3, mode: MouseTracking.ButtonEvent));

        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        Assert.False(TryEncodeRaw(MouseEventKind.Move, MouseButton.None, 5, 3, TerminalModifiers.None, MouseTracking.ButtonEvent, true, buffer, out _));
    }

    [Fact]
    public void Mode_1003_reports_any_move()
    {
        Assert.Equal("\u001b[<35;6;4M", EncodeSgr(MouseEventKind.Move, MouseButton.None, 5, 3, mode: MouseTracking.AnyEvent));
        Assert.Equal("\u001b[<0;6;4M", EncodeSgr(MouseEventKind.Press, MouseButton.Left, 5, 3, mode: MouseTracking.AnyEvent));
    }

    [Fact]
    public void Mode_x10_reports_only_presses()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];

        Assert.True(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.X10, true, buffer, out _));
        Assert.False(TryEncodeRaw(MouseEventKind.Release, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.X10, true, buffer, out _));
        Assert.False(TryEncodeRaw(MouseEventKind.Move, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.X10, true, buffer, out _));
    }

    [Fact]
    public void Legacy_x10_encoding_uses_offset_bytes()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        Assert.True(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.Normal, false, buffer, out int written));

        // ESC [ M, then 32+code, 32+x, 32+y with 1-based coordinates.
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 38, 36 }, buffer[..written].ToArray());
    }

    [Fact]
    public void Legacy_x10_release_reports_code_3()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        Assert.True(TryEncodeRaw(MouseEventKind.Release, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.Normal, false, buffer, out int written));

        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 35, 38, 36 }, buffer[..written].ToArray());
    }

    [Fact]
    public void Large_coordinates_fit_sgr_but_not_legacy()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];

        Assert.False(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 223, 0, TerminalModifiers.None, MouseTracking.Normal, false, buffer, out _));
        Assert.True(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 223, 0, TerminalModifiers.None, MouseTracking.Normal, true, buffer, out int written));
        Assert.Equal("\u001b[<0;224;1M", Encoding.Latin1.GetString(buffer[..written]));

        Assert.True(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 222, 0, TerminalModifiers.None, MouseTracking.Normal, false, buffer, out _));
    }

    [Theory]
    [InlineData((int)TerminalModifiers.Shift, 4)]
    [InlineData((int)TerminalModifiers.Alt, 8)]
    [InlineData((int)TerminalModifiers.Control, 16)]
    [InlineData((int)(TerminalModifiers.Shift | TerminalModifiers.Alt | TerminalModifiers.Control), 28)]
    public void Modifier_bits_add_into_button_code(int modifiers, int bits)
    {
        string encoded = EncodeSgr(MouseEventKind.Press, MouseButton.Left, 5, 3, (TerminalModifiers)modifiers);

        Assert.Equal($"\u001b[<{bits};6;4M", encoded);
    }

    [Fact]
    public void Negative_coordinates_are_rejected()
    {
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];

        Assert.False(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, -1, 0, TerminalModifiers.None, MouseTracking.Normal, true, buffer, out _));
        Assert.False(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 0, -1, TerminalModifiers.None, MouseTracking.Normal, true, buffer, out _));
    }

    [Fact]
    public void Short_destination_buffer_fails_without_truncation()
    {
        Span<byte> one = stackalloc byte[1];

        Assert.False(TryEncodeRaw(MouseEventKind.Press, MouseButton.Left, 5, 3, TerminalModifiers.None, MouseTracking.Normal, true, one, out int written));
        Assert.Equal(0, written);
    }
}
