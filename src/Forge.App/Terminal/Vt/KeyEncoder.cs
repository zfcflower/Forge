using System.Text;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// Turns a key plus modifiers plus terminal modes into the bytes xterm would send, so editors and full-screen programs
/// see what they expect: DECCKM switches cursor/home/end to <c>SS3</c>, modifiers add the <c>1;m</c> parameter, F5–F12
/// use the <c>~</c> forms, Shift+Tab is <c>CSI Z</c>, Backspace is DEL (Ctrl+Backspace is BS) and Alt prefixes ESC.
/// </summary>
internal static class KeyEncoder
{
    /// <summary>Largest sequence the encoder produces; callers sizing a buffer can rely on it.</summary>
    public const int MaxSequenceLength = 16;

    /// <summary>Encodes a non-character key. Returns false when the key has no terminal representation.</summary>
    public static bool TryEncode(
        TerminalKey key,
        TerminalModifiers modifiers,
        TerminalModes modes,
        Span<byte> destination,
        out int written)
    {
        Span<byte> scratch = stackalloc byte[MaxSequenceLength];
        AsciiWriter writer = new(scratch);
        if (!Emit(key, modifiers, modes, ref writer))
        {
            written = 0;
            return false;
        }

        return Copy(writer.Written, destination, out written);
    }

    /// <summary>
    /// Encodes typed text (one rune at a time). Ctrl maps letters to 0x01–0x1A, Ctrl+@/Space to NUL, Ctrl+<c>[ \ ] ^ _</c>
    /// to their control codes and Ctrl+? to DEL; Alt prefixes ESC; anything else is sent as its UTF-8 bytes.
    /// </summary>
    public static bool TryEncodeChar(int rune, TerminalModifiers modifiers, Span<byte> destination, out int written)
    {
        Span<byte> scratch = stackalloc byte[8];
        AsciiWriter writer = new(scratch);
        bool alt = (modifiers & (TerminalModifiers.Alt | TerminalModifiers.Meta)) != 0;
        if (alt)
        {
            writer.Add(0x1B);
        }

        if ((modifiers & TerminalModifiers.Control) != 0)
        {
            int control = ControlCode(rune);
            if (control >= 0)
            {
                writer.Add((byte)control);
                return Copy(writer.Written, destination, out written);
            }
        }

        if (!Rune.TryCreate(rune, out Rune value))
        {
            written = 0;
            return false;
        }

        Span<byte> utf8 = stackalloc byte[4];
        int bytes = value.EncodeToUtf8(utf8);
        writer.Add(utf8[..bytes]);
        return Copy(writer.Written, destination, out written);
    }

    /// <summary>The xterm modifier parameter: 1 plus Shift=1, Alt=2, Control=4, Meta=8; 1 when nothing is held.</summary>
    public static int ModifierParameter(TerminalModifiers modifiers)
    {
        int value = 1;
        if ((modifiers & TerminalModifiers.Shift) != 0)
        {
            value += 1;
        }

        if ((modifiers & TerminalModifiers.Alt) != 0)
        {
            value += 2;
        }

        if ((modifiers & TerminalModifiers.Control) != 0)
        {
            value += 4;
        }

        if ((modifiers & TerminalModifiers.Meta) != 0)
        {
            value += 8;
        }

        return value;
    }

    private static int ControlCode(int rune)
    {
        if (rune is >= 'a' and <= 'z')
        {
            return rune - 'a' + 1;
        }

        if (rune is >= 'A' and <= 'Z')
        {
            return rune - 'A' + 1;
        }

        return rune switch
        {
            '@' or ' ' => 0x00,
            '[' => 0x1B,
            '\\' => 0x1C,
            ']' => 0x1D,
            '^' => 0x1E,
            '_' => 0x1F,
            '?' => 0x7F,
            _ => -1,
        };
    }

    private static bool Emit(TerminalKey key, TerminalModifiers modifiers, TerminalModes modes, ref AsciiWriter writer)
    {
        bool alt = (modifiers & (TerminalModifiers.Alt | TerminalModifiers.Meta)) != 0;
        bool control = (modifiers & TerminalModifiers.Control) != 0;
        switch (key)
        {
            case TerminalKey.Enter:
                return Prefixed(ref writer, (byte)'\r', alt);
            case TerminalKey.Tab:
                if ((modifiers & TerminalModifiers.Shift) != 0 && !control && !alt)
                {
                    writer.Add("\u001b[Z"u8);
                    return true;
                }

                return Prefixed(ref writer, (byte)'\t', alt);
            case TerminalKey.Backspace:
                return Prefixed(ref writer, control ? (byte)0x08 : (byte)0x7F, alt);
            case TerminalKey.Escape:
                return Prefixed(ref writer, 0x1B, alt);
            case TerminalKey.Up:
                return CursorKey(ref writer, (byte)'A', modifiers, modes);
            case TerminalKey.Down:
                return CursorKey(ref writer, (byte)'B', modifiers, modes);
            case TerminalKey.Right:
                return CursorKey(ref writer, (byte)'C', modifiers, modes);
            case TerminalKey.Left:
                return CursorKey(ref writer, (byte)'D', modifiers, modes);
            case TerminalKey.Home:
                return CursorKey(ref writer, (byte)'H', modifiers, modes);
            case TerminalKey.End:
                return CursorKey(ref writer, (byte)'F', modifiers, modes);
            case TerminalKey.Insert:
                return TildeKey(ref writer, 2, modifiers);
            case TerminalKey.Delete:
                return TildeKey(ref writer, 3, modifiers);
            case TerminalKey.PageUp:
                return TildeKey(ref writer, 5, modifiers);
            case TerminalKey.PageDown:
                return TildeKey(ref writer, 6, modifiers);
            case TerminalKey.F1:
                return FunctionKey(ref writer, (byte)'P', modifiers);
            case TerminalKey.F2:
                return FunctionKey(ref writer, (byte)'Q', modifiers);
            case TerminalKey.F3:
                return FunctionKey(ref writer, (byte)'R', modifiers);
            case TerminalKey.F4:
                return FunctionKey(ref writer, (byte)'S', modifiers);
            case TerminalKey.F5:
                return TildeKey(ref writer, 15, modifiers);
            case TerminalKey.F6:
                return TildeKey(ref writer, 17, modifiers);
            case TerminalKey.F7:
                return TildeKey(ref writer, 18, modifiers);
            case TerminalKey.F8:
                return TildeKey(ref writer, 19, modifiers);
            case TerminalKey.F9:
                return TildeKey(ref writer, 20, modifiers);
            case TerminalKey.F10:
                return TildeKey(ref writer, 21, modifiers);
            case TerminalKey.F11:
                return TildeKey(ref writer, 23, modifiers);
            case TerminalKey.F12:
                return TildeKey(ref writer, 24, modifiers);
            default:
                return false;
        }
    }

    private static bool Prefixed(ref AsciiWriter writer, byte value, bool alt)
    {
        if (alt)
        {
            writer.Add(0x1B);
        }

        writer.Add(value);
        return true;
    }

    private static bool CursorKey(ref AsciiWriter writer, byte final, TerminalModifiers modifiers, TerminalModes modes)
    {
        if (modifiers == TerminalModifiers.None)
        {
            writer.Add(0x1B);
            writer.Add((modes & TerminalModes.ApplicationCursorKeys) != 0 ? (byte)'O' : (byte)'[');
            writer.Add(final);
            return true;
        }

        CsiPrefix(ref writer, modifiers);
        writer.Add(final);
        return true;
    }

    private static bool FunctionKey(ref AsciiWriter writer, byte final, TerminalModifiers modifiers)
    {
        if (modifiers == TerminalModifiers.None)
        {
            writer.Add("\u001bO"u8);
            writer.Add(final);
            return true;
        }

        CsiPrefix(ref writer, modifiers);
        writer.Add(final);
        return true;
    }

    private static bool TildeKey(ref AsciiWriter writer, int number, TerminalModifiers modifiers)
    {
        writer.Add(0x1B);
        writer.Add((byte)'[');
        writer.AddNumber(number);
        if (modifiers != TerminalModifiers.None)
        {
            writer.Add((byte)';');
            writer.AddNumber(ModifierParameter(modifiers));
        }

        writer.Add((byte)'~');
        return true;
    }

    private static void CsiPrefix(ref AsciiWriter writer, TerminalModifiers modifiers)
    {
        writer.Add(0x1B);
        writer.Add((byte)'[');
        writer.Add((byte)'1');
        writer.Add((byte)';');
        writer.AddNumber(ModifierParameter(modifiers));
    }

    private static bool Copy(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        if (destination.Length < source.Length)
        {
            written = 0;
            return false;
        }

        source.CopyTo(destination);
        written = source.Length;
        return true;
    }
}
