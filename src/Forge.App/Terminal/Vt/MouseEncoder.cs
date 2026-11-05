namespace Forge.App.Terminal.Vt;

/// <summary>Mouse buttons as the window sees them; the encoder maps them onto the xterm button codes.</summary>
internal enum MouseButton
{
    Left = 0,

    Middle = 1,

    Right = 2,

    /// <summary>No button (a bare pointer move, or a release in modes that do not say which button it was).</summary>
    None = 3,

    WheelUp = 4,

    WheelDown = 5,
}

/// <summary>What happened to the pointer.</summary>
internal enum MouseEventKind
{
    Press,

    Release,

    /// <summary>Pointer motion; only reported by 1002 (with a button held) and 1003 (always).</summary>
    Move,
}

/// <summary>
/// Encodes pointer events for the mouse reporting modes xterm defines: 1000 press/release/wheel, 1002 adds drags,
/// 1003 adds bare motion; SGR (1006) uses <c>CSI &lt; b ; x ; y M</c> with an <c>m</c> suffix for releases and
/// carries coordinates past 223, the legacy X10 form uses three offset bytes and gives up beyond column/row 223.
/// </summary>
internal static class MouseEncoder
{
    /// <summary>Largest sequence the encoder produces.</summary>
    public const int MaxSequenceLength = 16;

    /// <summary>Largest coordinate the legacy X10 form can carry.</summary>
    private const int MaxLegacyCoordinate = 223;

    /// <summary>
    /// Encodes one pointer event. Returns false when the active reporting mode does not carry this event (a move with
    /// no button under 1002, anything under <see cref="MouseTracking.None"/>, a wheel release, or a legacy encoding
    /// whose coordinate does not fit).
    /// </summary>
    public static bool TryEncode(
        MouseEventKind kind,
        MouseButton button,
        int column,
        int row,
        TerminalModifiers modifiers,
        MouseTracking mode,
        bool sgr,
        Span<byte> destination,
        out int written)
    {
        written = 0;
        if (mode == MouseTracking.None || column < 0 || row < 0)
        {
            return false;
        }

        bool wheel = button is MouseButton.WheelUp or MouseButton.WheelDown;
        if (wheel && kind != MouseEventKind.Press)
        {
            return false;
        }

        if (!Accepts(mode, kind, button))
        {
            return false;
        }

        int code = ButtonCode(button, kind, sgr) + ModifierBits(modifiers);
        int x = column + 1;
        int y = row + 1;

        Span<byte> scratch = stackalloc byte[MaxSequenceLength];
        AsciiWriter writer = new(scratch);
        if (sgr)
        {
            writer.Add("\u001b[<"u8);
            writer.AddNumber(code);
            writer.Add((byte)';');
            writer.AddNumber(x);
            writer.Add((byte)';');
            writer.AddNumber(y);
            writer.Add(kind == MouseEventKind.Release ? (byte)'m' : (byte)'M');
        }
        else
        {
            if (x > MaxLegacyCoordinate || y > MaxLegacyCoordinate)
            {
                return false;
            }

            writer.Add("\u001b[M"u8);
            writer.Add((byte)(32 + code));
            writer.Add((byte)(32 + x));
            writer.Add((byte)(32 + y));
        }

        if (destination.Length < writer.Written.Length)
        {
            return false;
        }

        writer.Written.CopyTo(destination);
        written = writer.Written.Length;
        return true;
    }

    private static bool Accepts(MouseTracking mode, MouseEventKind kind, MouseButton button)
    {
        switch (mode)
        {
            case MouseTracking.X10:
                return kind == MouseEventKind.Press;
            case MouseTracking.Normal:
                return kind != MouseEventKind.Move && button != MouseButton.None;
            case MouseTracking.ButtonEvent:
                return kind != MouseEventKind.Move || button != MouseButton.None;
            case MouseTracking.AnyEvent:
                return true;
            default:
                return false;
        }
    }

    private static int ButtonCode(MouseButton button, MouseEventKind kind, bool sgr)
    {
        if (kind == MouseEventKind.Release && !sgr)
        {
            return 3;
        }

        int code = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            MouseButton.WheelUp => 64,
            MouseButton.WheelDown => 65,
            _ => 3,
        };

        return kind == MouseEventKind.Move ? code + 32 : code;
    }

    private static int ModifierBits(TerminalModifiers modifiers)
    {
        int bits = 0;
        if ((modifiers & TerminalModifiers.Shift) != 0)
        {
            bits += 4;
        }

        if ((modifiers & (TerminalModifiers.Alt | TerminalModifiers.Meta)) != 0)
        {
            bits += 8;
        }

        if ((modifiers & TerminalModifiers.Control) != 0)
        {
            bits += 16;
        }

        return bits;
    }
}
