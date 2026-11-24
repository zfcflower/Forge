using System.Text;
using Avalonia.Input;
using Forge.App.Terminal.Vt;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What a Ctrl or Alt chord sends now comes from the keyboard layout's own text, not from the US virtual-key table. The
/// cases below are the ones that table got wrong: AltGr on a German layout (<c>@</c> used to arrive as ESC + <c>q</c>
/// plus the typed <c>@</c>), punctuation on a non-US layout (<c>Ctrl+[</c> sent nothing), and Option on macOS, which
/// types a character instead of acting as Meta.
/// <para>
/// These are unit tests on purpose: the decision depends on three facts a platform supplies per key-down
/// (<c>KeyText</c>, <c>AlternateKeyText</c>, <c>ConsumedModifiers</c>), and neither the headless input helpers nor any
/// window can synthesize a German or Russian keyboard layout. The bytes each case must produce are asserted exactly.
/// </para>
/// </summary>
public sealed class TerminalKeyChordTests
{
    private const KeyModifiers AltGr = KeyModifiers.Control | KeyModifiers.Alt;

    private static string Encode(int rune, TerminalModifiers modifiers)
    {
        Span<byte> buffer = stackalloc byte[KeyEncoder.MaxSequenceLength];
        Assert.True(KeyEncoder.TryEncodeChar(rune, modifiers, buffer, out var written));
        return Encoding.ASCII.GetString(buffer[..written]);
    }

    [Fact]
    public void AltGr_types_its_character_and_the_key_down_stays_out_of_the_way()
    {
        // Windows reports AltGr as Ctrl+Alt and the layout consumes both, so "@" arrives through text input alone.
        Assert.True(KeyChord.LayoutTypesTheChord(AltGr, AltGr));
        Assert.Equal(KeyModifiers.None, KeyChord.Free(AltGr, AltGr));

        // The same chord with nothing consumed is a deliberate Ctrl+Alt and keeps its ESC prefix.
        Assert.False(KeyChord.LayoutTypesTheChord(AltGr, KeyModifiers.None));
        Assert.Equal("\u001b\u001a", Encode('z', TerminalModifiers.Control | TerminalModifiers.Alt));
    }

    [Fact]
    public void Option_that_types_a_character_is_text_but_Option_as_Meta_is_an_escape_prefix()
    {
        // macOS: Option+Q on a German layout types "«" (Option consumed), so the character arrives as text. Option is
        // Meta only where it maps nothing — on the US layout Option+B types "∫", which also comes through text input.
        Assert.True(KeyChord.LayoutTypesTheChord(KeyModifiers.Alt, KeyModifiers.Alt));
        Assert.False(KeyChord.LayoutTypesTheChord(KeyModifiers.Alt, KeyModifiers.None));

        Assert.True(KeyChord.TryChordRune("b", out var meta));
        Assert.Equal("\u001bb", Encode(meta, TerminalModifiers.Alt));
    }

    [Fact]
    public void A_control_character_from_the_layout_is_sent_verbatim()
    {
        // Ctrl+[ on a German keyboard, Ctrl+Space and Ctrl+Z: the platform hands over the byte xterm expects.
        var buffer = new byte[16];
        Assert.True(KeyChord.TryCopyControlText("\u001b", buffer, out var escape));
        Assert.Equal(1, escape);
        Assert.Equal(0x1B, buffer[0]);

        Assert.True(KeyChord.TryCopyControlText("\0", buffer, out var nul));
        Assert.Equal(1, nul);
        Assert.Equal(0x00, buffer[0]);

        Assert.True(KeyChord.TryCopyControlText("\u001a", buffer, out var ctrlZ));
        Assert.Equal(1, ctrlZ);
        Assert.Equal(0x1A, buffer[0]);
    }

    [Fact]
    public void Printable_or_oversized_text_is_not_a_control_chord()
    {
        var buffer = new byte[16];

        Assert.False(KeyChord.TryCopyControlText("z", buffer, out var printable));
        Assert.Equal(0, printable);

        Assert.False(KeyChord.TryCopyControlText(null, buffer, out _));
        Assert.False(KeyChord.TryCopyControlText(string.Empty, buffer, out _));

        // A control text that does not fit the caller's buffer is refused rather than truncated.
        Assert.False(KeyChord.TryCopyControlText("\0\0\0", new byte[2], out _));
    }

    [Fact]
    public void A_compose_result_is_left_to_text_input()
    {
        // Two runes mean a dead key or compose sequence: the chord must not encode the first one and swallow the rest.
        Assert.False(KeyChord.TryChordRune("ab", out var multi));
        Assert.Equal(0, multi);

        Assert.False(KeyChord.TryChordRune(null, out _));

        // One rune beyond the BMP survives as one rune, not as two halves of a surrogate pair.
        Assert.True(KeyChord.TryChordRune("😀", out var emoji));
        Assert.Equal(0x1F600, emoji);
    }
}
