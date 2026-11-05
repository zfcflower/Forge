namespace Forge.App.Terminal.Vt;

/// <summary>SGR attributes that apply to a single cell. Fifteen bits fit in 16, which keeps <see cref="TerminalCell"/> small.</summary>
[Flags]
internal enum CellFlags : ushort
{
    None = 0,

    Bold = 1 << 0,

    Dim = 1 << 1,

    Italic = 1 << 2,

    Underline = 1 << 3,

    DoubleUnderline = 1 << 4,

    Blink = 1 << 5,

    Inverse = 1 << 6,

    Hidden = 1 << 7,

    Strikethrough = 1 << 8,

    /// <summary>Second cell of a double-width character; its own <see cref="TerminalCell.Rune"/> is unused.</summary>
    WideContinuation = 1 << 9,
}
