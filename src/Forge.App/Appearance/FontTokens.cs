namespace Forge.App.Appearance;

/// <summary>
/// Resource keys that carry the font preferences. Controls reach fonts only through these keys with
/// <c>{DynamicResource ...}</c>; nothing subscribes to <see cref="Preferences.AppPreferences"/> per control.
/// <c>Resources/Tokens.axaml</c> holds the static defaults; <see cref="AppearanceApplier"/> overrides them at runtime.
/// </summary>
internal static class FontResourceKeys
{
    /// <summary><c>FontFamily</c> of the UI (trees, tabs, chat, buttons); from <c>UiFontFamily</c>.</summary>
    public const string Ui = "FontUi";

    /// <summary><c>FontFamily</c> of code (file preview, diff, tool arguments/results, terminal); from <c>CodeFontFamily</c>.</summary>
    public const string Mono = "FontMono";

    /// <summary><c>double</c>: the code font size, exactly <c>CodeFontSize</c> (no scaling).</summary>
    public const string SizeCode = "FontSizeCode";

    public const string SizeXs = "FontSizeXs";
    public const string SizeSm = "FontSizeSm";
    public const string SizeBase = "FontSizeBase";
    public const string SizeMd = "FontSizeMd";
    public const string SizeLg = "FontSizeLg";
    public const string SizeXl = "FontSizeXl";
}

/// <summary>The UI size scale; <see cref="FontScale.Default"/> equals the values in <c>Tokens.axaml</c>.</summary>
internal readonly record struct FontSizeTokens(double Xs, double Sm, double Base, double Md, double Lg, double Xl);

/// <summary>Derives the UI size scale from the UI font size.</summary>
internal static class FontScale
{
    /// <summary>The UI size the token defaults were designed for.</summary>
    public const double ReferenceSize = 13;

    /// <summary>11/12/13/14/18/22, the scale at <see cref="ReferenceSize"/>.</summary>
    public static readonly FontSizeTokens Default = new(11, 12, 13, 14, 18, 22);

    /// <summary>
    /// Every token of <see cref="Default"/> times <paramref name="uiSize"/>/<see cref="ReferenceSize"/>, rounded to a whole
    /// number with <see cref="MidpointRounding.AwayFromZero"/>. At <see cref="ReferenceSize"/> the result equals
    /// <see cref="Default"/>. Input is assumed already clamped by <c>AppPreferences</c>.
    /// </summary>
    public static FontSizeTokens Compute(double uiSize)
    {
        var scale = uiSize / ReferenceSize;
        return new FontSizeTokens(
            Round(Default.Xs * scale),
            Round(Default.Sm * scale),
            Round(Default.Base * scale),
            Round(Default.Md * scale),
            Round(Default.Lg * scale),
            Round(Default.Xl * scale));
    }

    /// <summary>Token values are whole device-independent pixels; halves go away from zero so 9 and 28 stay symmetric.</summary>
    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}
