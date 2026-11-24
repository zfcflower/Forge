using Avalonia.Input;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// The layout facts of a Ctrl or Alt chord, which the virtual-key table cannot supply: whether the layout itself used
/// the chord to type a character (AltGr on Windows and Linux, Option typing on macOS), and what the platform says the
/// key produces. <see cref="KeyEncoder"/> turns the result into bytes.
/// </summary>
/// <remarks>
/// Avalonia 12.2 reports three facts per key-down that a terminal needs: <c>KeyText</c> (the text with all modifiers
/// applied, C0 control characters included), <c>AlternateKeyText</c> (the same with Ctrl/Alt/Meta ignored, so Shift and
/// AltGr still count) and <c>ConsumedModifiers</c> (which of the pressed modifiers the layout used to get there).
/// Without them the encoder can only map <c>Key.A</c>…<c>Key.Z</c> and <c>Key.D0</c>…<c>Key.D9</c>, which is wrong for
/// AltGr (<c>@</c> on a German layout arrived as ESC + <c>q</c> followed by the typed <c>@</c>) and for punctuation on
/// any layout that is not US English (<c>Ctrl+[</c> sent nothing).
/// </remarks>
internal static class KeyChord
{
    /// <summary>
    /// True when the layout already used the chord's Ctrl and/or Alt to type a character: the character arrives through
    /// text input, and the key-down must not encode it again.
    /// </summary>
    public static bool LayoutTypesTheChord(KeyModifiers modifiers, KeyModifiers? consumedModifiers)
    {
        var chord = modifiers & (KeyModifiers.Control | KeyModifiers.Alt);
        return chord != 0 && (chord & ~(consumedModifiers ?? KeyModifiers.None)) == 0;
    }

    /// <summary>The modifiers the layout left for the application, which are the ones a chord may encode.</summary>
    public static KeyModifiers Free(KeyModifiers modifiers, KeyModifiers? consumedModifiers) =>
        modifiers & ~(consumedModifiers ?? KeyModifiers.None);

    /// <summary>
    /// The bytes a platform key text stands for when the whole text is control characters the terminal receives
    /// verbatim: Ctrl+Z gives <c>"\x1a"</c>, Ctrl+Space <c>"\0"</c> and Ctrl+<c>[</c> <c>"\x1b"</c> on every layout.
    /// False for printable text, an empty or missing text, and text too long for <paramref name="destination"/>.
    /// </summary>
    public static bool TryCopyControlText(string? keyText, Span<byte> destination, out int written)
    {
        written = 0;
        if (string.IsNullOrEmpty(keyText) || keyText.Length > destination.Length)
        {
            return false;
        }

        foreach (var character in keyText)
        {
            if (character > '\u001f' && character != '\u007f')
            {
                return false;
            }
        }

        for (var i = 0; i < keyText.Length; i++)
        {
            destination[i] = (byte)keyText[i];
        }

        written = keyText.Length;
        return true;
    }

    /// <summary>
    /// The character a chord types, taken from the layout's text with Ctrl/Alt/Meta ignored. One rune only: a longer
    /// text is a compose or dead-key result, which belongs to text input and not to a chord.
    /// </summary>
    public static bool TryChordRune(string? alternateKeyText, out int rune)
    {
        rune = 0;
        if (string.IsNullOrEmpty(alternateKeyText))
        {
            return false;
        }

        var runes = alternateKeyText.EnumerateRunes();
        if (!runes.MoveNext())
        {
            return false;
        }

        var first = runes.Current.Value;
        if (runes.MoveNext())
        {
            return false;
        }

        rune = first;
        return true;
    }
}
