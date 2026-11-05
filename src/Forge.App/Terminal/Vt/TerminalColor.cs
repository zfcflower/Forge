using System.Globalization;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// A terminal colour in one of three forms: the theme default, a 0–255 palette index, or a 24-bit RGB value. The whole
/// colour is packed into a single word so <see cref="TerminalCell"/> stays a small, reference-free value type.
/// </summary>
internal readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private const uint KindMask = 0xFF00_0000u;
    private const uint PaletteKind = 0x0100_0000u;
    private const uint RgbKind = 0x0200_0000u;

    private readonly uint packed;

    private TerminalColor(uint packed)
    {
        this.packed = packed;
    }

    /// <summary>The theme default foreground or background; this is also the state of a freshly cleared cell.</summary>
    public static TerminalColor Default => default;

    /// <summary>A 0–255 palette slot (16 ANSI colours, 216 cube colours, 24 greys).</summary>
    public static TerminalColor Palette(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 255);
        return new TerminalColor(PaletteKind | (uint)index);
    }

    /// <summary>A 24-bit true colour.</summary>
    public static TerminalColor Rgb(byte red, byte green, byte blue) =>
        new(RgbKind | ((uint)red << 16) | ((uint)green << 8) | blue);

    public bool IsDefault => packed == 0;

    public bool IsPalette => (packed & KindMask) == PaletteKind;

    public bool IsRgb => (packed & KindMask) == RgbKind;

    /// <summary>Valid when <see cref="IsPalette"/>; otherwise 0.</summary>
    public int PaletteIndex => IsPalette ? (int)(packed & 0xFFu) : 0;

    /// <summary>Valid when <see cref="IsRgb"/>; otherwise 0.</summary>
    public byte Red => IsRgb ? (byte)(packed >> 16) : (byte)0;

    /// <summary>Valid when <see cref="IsRgb"/>; otherwise 0.</summary>
    public byte Green => IsRgb ? (byte)(packed >> 8) : (byte)0;

    /// <summary>Valid when <see cref="IsRgb"/>; otherwise 0.</summary>
    public byte Blue => IsRgb ? (byte)packed : (byte)0;

    public bool Equals(TerminalColor other) => packed == other.packed;

    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    public override int GetHashCode() => (int)packed;

    public static bool operator ==(TerminalColor left, TerminalColor right) => left.packed == right.packed;

    public static bool operator !=(TerminalColor left, TerminalColor right) => left.packed != right.packed;

    public override string ToString()
    {
        if (IsDefault)
        {
            return "default";
        }

        return IsPalette
            ? "palette:" + PaletteIndex.ToString(CultureInfo.InvariantCulture)
            : "rgb:" + Red.ToString(CultureInfo.InvariantCulture) + "/" +
              Green.ToString(CultureInfo.InvariantCulture) + "/" +
              Blue.ToString(CultureInfo.InvariantCulture);
    }
}
