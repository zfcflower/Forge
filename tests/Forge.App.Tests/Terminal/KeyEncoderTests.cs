using System.Text;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Covers <see cref="KeyEncoder"/>: cursor/home/end in normal vs. application-cursor mode,
/// F1–F4 SS3 vs. F5–F12 tilde forms, modified keys, Shift+Tab, Backspace variants, Enter,
/// <see cref="KeyEncoder.TryEncodeChar"/> control/alt mappings, and short-buffer rejection.
/// </summary>
public sealed class KeyEncoderTests
{
    private static string Encode(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None, bool appCursor = false)
    {
        var modes = appCursor ? TerminalModes.ApplicationCursorKeys : TerminalModes.None;
        Span<byte> buffer = stackalloc byte[KeyEncoder.MaxSequenceLength];
        Assert.True(KeyEncoder.TryEncode(key, modifiers, modes, buffer, out int written));
        return Encoding.Latin1.GetString(buffer[..written]);
    }

    public static IEnumerable<object[]> CursorKeys()
    {
        yield return new object[] { (int)TerminalKey.Up, "A" };
        yield return new object[] { (int)TerminalKey.Down, "B" };
        yield return new object[] { (int)TerminalKey.Right, "C" };
        yield return new object[] { (int)TerminalKey.Left, "D" };
        yield return new object[] { (int)TerminalKey.Home, "H" };
        yield return new object[] { (int)TerminalKey.End, "F" };
    }

    [Theory]
    [MemberData(nameof(CursorKeys))]
    public void Cursor_keys_use_csi_in_normal_mode(int key, string final)
    {
        Assert.Equal("\u001b[" + final, Encode((TerminalKey)key));
    }

    [Theory]
    [MemberData(nameof(CursorKeys))]
    public void Cursor_keys_use_ss3_in_application_mode(int key, string final)
    {
        Assert.Equal("\u001bO" + final, Encode((TerminalKey)key, appCursor: true));
    }

    [Theory]
    [MemberData(nameof(CursorKeys))]
    public void Cursor_keys_with_modifiers_use_csi_with_parameter(int key, string final)
    {
        Assert.Equal("\u001b[1;2" + final, Encode((TerminalKey)key, TerminalModifiers.Shift));
        Assert.Equal("\u001b[1;5" + final, Encode((TerminalKey)key, TerminalModifiers.Control, appCursor: true));
    }

    [Theory]
    [InlineData((int)TerminalKey.F1, "P")]
    [InlineData((int)TerminalKey.F2, "Q")]
    [InlineData((int)TerminalKey.F3, "R")]
    [InlineData((int)TerminalKey.F4, "S")]
    public void F1_to_F4_use_ss3(int key, string final)
    {
        Assert.Equal("\u001bO" + final, Encode((TerminalKey)key));
    }

    [Fact]
    public void F1_with_modifier_uses_csi()
    {
        Assert.Equal("\u001b[1;2P", Encode(TerminalKey.F1, TerminalModifiers.Shift));
    }

    [Theory]
    [InlineData((int)TerminalKey.F5, 15)]
    [InlineData((int)TerminalKey.F6, 17)]
    [InlineData((int)TerminalKey.F7, 18)]
    [InlineData((int)TerminalKey.F8, 19)]
    [InlineData((int)TerminalKey.F9, 20)]
    [InlineData((int)TerminalKey.F10, 21)]
    [InlineData((int)TerminalKey.F11, 23)]
    [InlineData((int)TerminalKey.F12, 24)]
    public void F5_to_F12_use_tilde_forms(int key, int number)
    {
        Assert.Equal($"\u001b[{number}~", Encode((TerminalKey)key));
        Assert.Equal($"\u001b[{number};2~", Encode((TerminalKey)key, TerminalModifiers.Shift));
    }

    [Theory]
    [InlineData((int)TerminalKey.Insert, 2)]
    [InlineData((int)TerminalKey.Delete, 3)]
    [InlineData((int)TerminalKey.PageUp, 5)]
    [InlineData((int)TerminalKey.PageDown, 6)]
    public void Editing_keys_use_tilde_forms(int key, int number)
    {
        Assert.Equal($"\u001b[{number}~", Encode((TerminalKey)key));
        Assert.Equal($"\u001b[{number};3~", Encode((TerminalKey)key, TerminalModifiers.Alt));
    }

    [Fact]
    public void Shift_tab_is_csi_z()
    {
        Assert.Equal("\u001b[Z", Encode(TerminalKey.Tab, TerminalModifiers.Shift));
    }

    [Fact]
    public void Plain_tab_is_a_tab_character()
    {
        Assert.Equal("\t", Encode(TerminalKey.Tab));
    }

    [Fact]
    public void Backspace_is_del_and_ctrl_backspace_is_bs()
    {
        Assert.Equal("\u007f", Encode(TerminalKey.Backspace));
        Assert.Equal("\b", Encode(TerminalKey.Backspace, TerminalModifiers.Control));
    }

    [Fact]
    public void Enter_is_carriage_return()
    {
        Assert.Equal("\r", Encode(TerminalKey.Enter));
    }

    [Fact]
    public void Escape_encodes_to_esc()
    {
        Assert.Equal("\u001b", Encode(TerminalKey.Escape));
    }

    [Theory]
    [InlineData((int)TerminalModifiers.None, 1)]
    [InlineData((int)TerminalModifiers.Shift, 2)]
    [InlineData((int)TerminalModifiers.Alt, 3)]
    [InlineData((int)(TerminalModifiers.Shift | TerminalModifiers.Alt), 4)]
    [InlineData((int)TerminalModifiers.Control, 5)]
    [InlineData((int)(TerminalModifiers.Shift | TerminalModifiers.Control), 6)]
    [InlineData((int)TerminalModifiers.Meta, 9)]
    [InlineData((int)(TerminalModifiers.Shift | TerminalModifiers.Alt | TerminalModifiers.Control | TerminalModifiers.Meta), 16)]
    public void Modifier_parameter_matches_xterm(int modifiers, int expected)
    {
        Assert.Equal(expected, KeyEncoder.ModifierParameter((TerminalModifiers)modifiers));
    }

    private static byte[] EncodeChar(int rune, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        Span<byte> buffer = stackalloc byte[KeyEncoder.MaxSequenceLength];
        Assert.True(KeyEncoder.TryEncodeChar(rune, modifiers, buffer, out int written));
        return buffer[..written].ToArray();
    }

    [Theory]
    [InlineData('a', 0x01)]
    [InlineData('z', 0x1A)]
    [InlineData('A', 0x01)]
    [InlineData('Z', 0x1A)]
    [InlineData('@', 0x00)]
    [InlineData(' ', 0x00)]
    [InlineData('[', 0x1B)]
    [InlineData('?', 0x7F)]
    public void Ctrl_letter_maps_to_control_code(char letter, int expected)
    {
        Assert.Equal(new[] { (byte)expected }, EncodeChar(letter, TerminalModifiers.Control));
    }

    [Fact]
    public void Alt_prefixes_escape()
    {
        Assert.Equal(new byte[] { 0x1B, (byte)'x' }, EncodeChar('x', TerminalModifiers.Alt));
    }

    [Fact]
    public void Plain_character_encodes_as_utf8()
    {
        Assert.Equal(new[] { (byte)'A' }, EncodeChar('A'));
        Assert.Equal(Encoding.UTF8.GetBytes("é"), EncodeChar('é'));
    }

    [Fact]
    public void Short_destination_buffer_fails_without_truncation()
    {
        Span<byte> one = stackalloc byte[1];
        Assert.False(KeyEncoder.TryEncode(TerminalKey.Up, TerminalModifiers.None, TerminalModes.None, one, out int written));
        Assert.Equal(0, written);

        Span<byte> zero = Span<byte>.Empty;
        Assert.False(KeyEncoder.TryEncodeChar('é', TerminalModifiers.None, zero, out int charWritten));
        Assert.Equal(0, charWritten);
    }
}
