using Avalonia.Media;
using Forge.App.Terminal.Vt;

namespace Forge.App.Terminal;

/// <summary>
/// The colours a terminal cell can ask for: the 16 ANSI slots, the 6×6×6 colour cube and the 24-step grey ramp, one table
/// per theme variant. True colour values are passed through untouched. The default foreground and background are not part
/// of the table: they come from the theme tokens (<c>TextPrimaryBrush</c>, <c>SurfaceBackgroundBrush</c>), and
/// <see cref="FallbackForeground"/>/<see cref="FallbackBackground"/> only answer OSC 10/11 before a view is attached.
/// </summary>
internal sealed class TerminalPalette
{
    /// <summary>Levels of the 6×6×6 colour cube, as xterm defines them.</summary>
    private static readonly byte[] CubeLevels = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];

    private static readonly TerminalPalette Light = new(
        [
            // The 16 ANSI slots, light variant: readable as text on the #F5F6F5 background.
            Color.Parse("#2B2F2C"), Color.Parse("#B3261E"), Color.Parse("#2F7449"), Color.Parse("#8A5A00"),
            Color.Parse("#1C5FA8"), Color.Parse("#7B3FA0"), Color.Parse("#146C7E"), Color.Parse("#6E7671"),
            Color.Parse("#5C625E"), Color.Parse("#C7372E"), Color.Parse("#3A8F5C"), Color.Parse("#A06A00"),
            Color.Parse("#2A74C2"), Color.Parse("#9350B8"), Color.Parse("#1B8399"), Color.Parse("#3F4541"),
        ],
        Color.Parse("#2B2F2C"),
        Color.Parse("#F5F6F5"));

    private static readonly TerminalPalette Dark = new(
        [
            // The same 16 slots, dark variant: the dim slots stay distinct from the #1E2120 background.
            Color.Parse("#3F4541"), Color.Parse("#E06C75"), Color.Parse("#98C379"), Color.Parse("#E5C07B"),
            Color.Parse("#61AFEF"), Color.Parse("#C678DD"), Color.Parse("#56B6C2"), Color.Parse("#DDE3DF"),
            Color.Parse("#5C625E"), Color.Parse("#FF7B86"), Color.Parse("#B0D98C"), Color.Parse("#F0D098"),
            Color.Parse("#7CC0FF"), Color.Parse("#D79AE8"), Color.Parse("#7FD4DE"), Color.Parse("#F5F7F5"),
        ],
        Color.Parse("#DDE3DF"),
        Color.Parse("#1E2120"));

    private readonly Color[] ansi;

    private TerminalPalette(Color[] ansi, Color fallbackForeground, Color fallbackBackground)
    {
        this.ansi = ansi;
        FallbackForeground = fallbackForeground;
        FallbackBackground = fallbackBackground;
    }

    /// <summary>The table of the theme variant: dark terminals read <paramref name="dark"/>, light ones the other one.</summary>
    public static TerminalPalette For(bool dark) => dark ? Dark : Light;

    /// <summary>Colour OSC 10 is answered with when no view has handed the theme tokens over yet.</summary>
    public Color FallbackForeground { get; }

    /// <summary>Colour OSC 11 is answered with when no view has handed the theme tokens over yet.</summary>
    public Color FallbackBackground { get; }

    /// <summary>
    /// Resolves a palette index or a true colour. A default colour has no palette entry and returns
    /// <see langword="null"/>: the caller substitutes the theme's foreground or background.
    /// </summary>
    public Color? Resolve(TerminalColor color)
    {
        if (color.IsDefault)
        {
            return null;
        }

        if (color.IsPalette)
        {
            return FromIndex(color.PaletteIndex);
        }

        return Color.FromRgb(color.Red, color.Green, color.Blue);
    }

    /// <summary>Resolves the 0–255 xterm palette index (16 ANSI, 216 cube, 24 grey).</summary>
    public Color FromIndex(int index)
    {
        if ((uint)index < 16u)
        {
            return ansi[index];
        }

        if (index < 232)
        {
            int cube = index - 16;
            return Color.FromRgb(CubeLevels[cube / 36], CubeLevels[cube / 6 % 6], CubeLevels[cube % 6]);
        }

        byte level = (byte)Math.Clamp(8 + ((index - 232) * 10), 0, 255);
        return Color.FromRgb(level, level, level);
    }
}
