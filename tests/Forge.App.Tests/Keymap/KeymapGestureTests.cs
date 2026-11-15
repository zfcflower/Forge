using Avalonia.Input;
using Forge.App.Input;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The pure gesture rules of the keymap (W10e): parse a stored override back into a <see cref="KeyGesture"/>, normalize to
/// the round-trippable stored form, format the human-friendly display form, and flag conflicts inside one key chain. All of
/// it is static data in, static data out, so nothing here needs a window or a preference store.
/// </summary>
public sealed class KeymapGestureTests
{
    [Theory]
    [InlineData("Ctrl+P", Key.P, KeyModifiers.Control)]
    [InlineData("Ctrl+OemTilde", Key.OemTilde, KeyModifiers.Control)]
    [InlineData("Ctrl+Shift+OemOpenBrackets", Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Ctrl+Alt+OemCloseBrackets", Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData("Shift+F12", Key.F12, KeyModifiers.Shift)]
    [InlineData("J", Key.J, KeyModifiers.None)]
    [InlineData("Ctrl+Space", Key.Space, KeyModifiers.Control)]
    public void Parsing_reads_the_stored_canonical_forms(string text, Key key, KeyModifiers modifiers)
    {
        Assert.True(KeymapGesture.TryParse(text, out var gesture));
        Assert.NotNull(gesture);
        Assert.Equal(key, gesture!.Key);
        Assert.Equal(modifiers, gesture.KeyModifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Foo+Z")]
    [InlineData("Ctrl+`")]
    [InlineData("Foo")]
    [InlineData("Ctrl+Foo")]
    [InlineData("F99")]
    [InlineData("Ctrl+F99")]
    [InlineData("None")]
    [InlineData("Ctrl+None")]
    // The key has to come last: "P+Ctrl" parses as an empty key with a modifier, which is not a gesture.
    [InlineData("P+Ctrl")]
    [InlineData("OemTilde+Ctrl")]
    [InlineData("Ctrl+AltGr+P")]
    [InlineData("Ctrl++P")]
    public void Parsing_rejects_a_lone_modifier_an_unknown_key_and_the_display_spelling(string text)
    {
        Assert.False(KeymapGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    /// <summary>
    /// A hand-edited <c>settings.json</c> may spell the same gesture any way the parser tolerates: lower case, spaces around
    /// the separators, a repeated modifier, or the modifiers in another order. All of them have to land on one gesture and on
    /// one stored form, otherwise the conflict check would miss the collision (it compares parsed gestures) and the file would
    /// keep two spellings of one binding.
    /// </summary>
    [Theory]
    [InlineData("ctrl+p", Key.P, KeyModifiers.Control)]
    [InlineData("CTRL+P", Key.P, KeyModifiers.Control)]
    [InlineData("Ctrl + P", Key.P, KeyModifiers.Control)]
    [InlineData("  Ctrl+P  ", Key.P, KeyModifiers.Control)]
    [InlineData("Ctrl+Ctrl+P", Key.P, KeyModifiers.Control)]
    [InlineData("Ctrl+Shift+Ctrl+P", Key.P, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Shift+Ctrl+P", Key.P, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Shift+Ctrl+OemTilde", Key.OemTilde, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Alt+Shift+Ctrl+OemOpenBrackets", Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)]
    public void A_hand_written_spelling_parses_to_the_one_gesture_the_conflict_check_compares(string text, Key key, KeyModifiers modifiers)
    {
        Assert.True(KeymapGesture.TryParse(text, out var gesture));
        Assert.Equal(new KeyGesture(key, modifiers), gesture);
    }

    /// <summary>
    /// <c>Normalize</c> is what every write goes through, so feeding its own output back in has to be a no-op: a stored form
    /// that changed on every save would rewrite <c>settings.json</c> and re-raise the change on each pass.
    /// </summary>
    [Fact]
    public void Normalize_is_idempotent_for_the_catalog_and_for_a_hand_written_spelling()
    {
        foreach (var action in KeymapCatalog.Actions)
        {
            // The default survives the round trip as the same gesture, not merely as a string that parses.
            Assert.Equal(action.DefaultGesture, Parse(KeymapGesture.Normalize(action.DefaultGesture)));
            AssertStable(KeymapGesture.Normalize(action.DefaultGesture));
        }

        foreach (var text in new[] { "ctrl+shift+o", "Shift+Ctrl+F12", "Ctrl+Ctrl+Space", "Meta+P" })
        {
            AssertStable(KeymapGesture.Normalize(Parse(text)));
        }

        static KeyGesture Parse(string text)
        {
            Assert.True(KeymapGesture.TryParse(text, out var gesture), $"'{text}' must parse");
            return gesture!;
        }

        static void AssertStable(string canonical)
        {
            Assert.True(KeymapGesture.TryParse(canonical, out var reparsed), $"'{canonical}' must parse back");
            Assert.Equal(canonical, KeymapGesture.Normalize(reparsed!));
        }
    }

    /// <summary>
    /// The OEM keys are the platform-sensitive half of the table: the <see cref="Key"/> enum carries two names for some of them
    /// (<c>OemTilde</c>/<c>Oem3</c>, <c>OemOpenBrackets</c>/<c>Oem4</c>), and the stored form uses whichever name
    /// <c>ToString</c> picked. A file written by the app and a file written by hand with the source's name must both be honoured,
    /// or the default <c>Ctrl+`</c> silently stops parsing on one of the two spellings.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+OemTilde", Key.OemTilde)]
    [InlineData("Ctrl+Oem3", Key.OemTilde)]
    [InlineData("Ctrl+Shift+OemOpenBrackets", Key.OemOpenBrackets)]
    [InlineData("Ctrl+Shift+Oem4", Key.OemOpenBrackets)]
    [InlineData("Ctrl+Alt+OemCloseBrackets", Key.OemCloseBrackets)]
    public void A_platform_key_parses_from_the_source_name_and_from_the_alias_the_stored_form_uses(string text, Key key)
    {
        Assert.True(KeymapGesture.TryParse(text, out var gesture));
        Assert.Equal(key, gesture!.Key);

        var stored = KeymapGesture.Normalize(gesture);
        Assert.True(KeymapGesture.TryParse(stored, out var reparsed));
        Assert.Equal(gesture, reparsed);
    }

    [Theory]
    [InlineData(Key.OemTilde, KeyModifiers.Control, "Ctrl+`")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+[")]
    [InlineData(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+]")]
    [InlineData(Key.Space, KeyModifiers.Control, "Ctrl+Space")]
    [InlineData(Key.F12, KeyModifiers.Shift, "Shift+F12")]
    [InlineData(Key.J, KeyModifiers.None, "J")]
    [InlineData(Key.S, KeyModifiers.Control, "Ctrl+S")]
    public void Format_uses_the_human_friendly_spelling(Key key, KeyModifiers modifiers, string expected)
    {
        Assert.Equal(expected, KeymapGesture.Format(new KeyGesture(key, modifiers)));
    }

    [Fact]
    public void A_conflict_is_detected_only_inside_the_same_key_chain()
    {
        var resolved = KeymapCatalog.Actions.ToDictionary(static action => action.Id, static action => action.DefaultGesture);

        // Ctrl+P is the default of OpenFilePalette; rebinding OpenCommandPalette to it collides in the window scope.
        Assert.Equal(
            KeymapActionIds.OpenFilePalette,
            KeymapGesture.FindConflict(KeymapActionIds.OpenCommandPalette, new KeyGesture(Key.P, KeyModifiers.Control), resolved));

        // The same gesture in the view scope is not a window conflict: the window's key bindings only see a key the focused
        // view did not handle.
        Assert.Null(
            KeymapGesture.FindConflict(KeymapActionIds.NewTerminal, new KeyGesture(Key.S, KeyModifiers.Control), resolved));

        // Rebinding an action to its own current gesture is not a conflict with itself.
        Assert.Null(
            KeymapGesture.FindConflict(KeymapActionIds.OpenFilePalette, new KeyGesture(Key.P, KeyModifiers.Control), resolved));

        // The other view/window direction too: a view action may take a window default, because the focused view handles the
        // key first.
        Assert.Null(
            KeymapGesture.FindConflict(KeymapActionIds.SaveDocument, new KeyGesture(Key.P, KeyModifiers.Control), resolved));
        Assert.Null(
            KeymapGesture.FindConflict(KeymapActionIds.SaveDocument, new KeyGesture(Key.OemTilde, KeyModifiers.Control), resolved));

        // The editor and the view actions are pressed in one chain — the file view's handler runs for the key while the
        // editor has focus — so a gesture may not sit on both. Ctrl+Z is the editor's undo; saving onto it would leave two
        // actions behind one key.
        Assert.Equal(
            KeymapActionIds.EditorUndo,
            KeymapGesture.FindConflict(KeymapActionIds.SaveDocument, new KeyGesture(Key.Z, KeyModifiers.Control), resolved));
        Assert.Equal(
            KeymapActionIds.SaveDocument,
            KeymapGesture.FindConflict(KeymapActionIds.EditorCopy, new KeyGesture(Key.S, KeyModifiers.Control), resolved));
    }

    /// <summary>
    /// The conflict check runs against what the keys actually do, so an action that was itself rebound out of a gesture frees
    /// that gesture (and an action rebound onto a default now owns it). Checking the raw default table instead would report
    /// conflicts that no longer exist and miss the ones that do.
    /// </summary>
    [Fact]
    public void Conflicts_follow_the_resolved_overrides_not_the_default_table()
    {
        var resolved = KeymapCatalog.Actions.ToDictionary(static action => action.Id, static action => action.DefaultGesture);

        // OpenFilePalette moves off Ctrl+P, so OpenCommandPalette may take it.
        resolved[KeymapActionIds.OpenFilePalette] = new KeyGesture(Key.O, KeyModifiers.Control);
        Assert.Null(
            KeymapGesture.FindConflict(KeymapActionIds.OpenCommandPalette, new KeyGesture(Key.P, KeyModifiers.Control), resolved));

        // SaveDocument takes Ctrl+T, which no default uses; the next action rebound onto it collides with the override.
        resolved[KeymapActionIds.SaveDocument] = new KeyGesture(Key.T, KeyModifiers.Control);
        Assert.Equal(
            KeymapActionIds.SaveDocument,
            KeymapGesture.FindConflict(KeymapActionIds.NextUnviewedChange, new KeyGesture(Key.T, KeyModifiers.Control), resolved));
    }
}
