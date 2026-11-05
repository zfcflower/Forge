namespace Forge.App.Terminal.Vt;

/// <summary>Non-character keys a terminal window can send; the renderer maps its key events onto these.</summary>
internal enum TerminalKey
{
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

/// <summary>Modifier state of a key or mouse event, mapped to the xterm modifier parameter (1 + the bits).</summary>
[Flags]
internal enum TerminalModifiers
{
    None = 0,

    Shift = 1,

    Alt = 2,

    Control = 4,

    /// <summary>Command on macOS, the Windows key elsewhere; sent as the 8 bit of the xterm modifier parameter.</summary>
    Meta = 8,
}
