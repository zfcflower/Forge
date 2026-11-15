using Avalonia.Input;
using Forge.App.Input;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.Tests.Palette;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The display side of the keymap (W10e): the catalog is the one table every surface reads, so it has to be complete and
/// nameable, and the surfaces that show a gesture — the palette's command badges, its source strip, the parameterized
/// "default …" and "conflicts with …" sentences — have to follow a rebind and the UI language instead of spelling the
/// gesture out themselves. Nothing here needs a window; the applied behaviour is in
/// <see cref="KeymapHeadlessTests"/>.
/// </summary>
public sealed class KeymapSurfacesTests : IDisposable
{
    public void Dispose() => KeymapApplier.Unbind();

    /// <summary>
    /// Twenty-one actions, one id each, one localized name each in both languages, and a real default gesture: the settings page
    /// builds one row per entry and the appliers resolve them by id, so a duplicate or an unregistered name is a row that
    /// either never shows or shows <c>[keymap.action.…]</c>.
    /// </summary>
    [Fact]
    public void The_catalog_has_twenty_one_unique_actions_each_with_a_registered_name_and_default_gesture()
    {
        Assert.Equal(21, KeymapCatalog.Actions.Count);
        Assert.Equal(21, KeymapCatalog.Actions.Select(static action => action.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, KeymapCatalog.InScope(KeymapScope.Window).Count());
        Assert.Equal(13, KeymapCatalog.InScope(KeymapScope.Editor).Count());
        Assert.Equal(4, KeymapCatalog.InScope(KeymapScope.View).Count());

        foreach (var language in new[] { UiLanguage.Chinese, UiLanguage.English })
        {
            using var _ = new LanguageScope(language);
            foreach (var action in KeymapCatalog.Actions)
            {
                Assert.NotEqual(Key.None, action.DefaultGesture.Key);
                var name = Loc.Instance[action.DisplayNameKey];
                Assert.False(string.IsNullOrWhiteSpace(name), $"{action.Id} has no display name");
                Assert.DoesNotContain("[", name);
            }
        }

        Assert.Null(KeymapCatalog.Find("window.doesNotExist"));
    }

    /// <summary>
    /// The six gestures the shell shipped before the keymap wave: four window bindings, save and next-unviewed. They are the
    /// defaults users already have in their fingers, so the catalog may not drift off them.
    /// </summary>
    [Fact]
    public void The_six_original_gestures_are_the_catalog_defaults()
    {
        Assert.Equal(new KeyGesture(Key.OemTilde, KeyModifiers.Control), KeymapCatalog.DefaultGesture(KeymapActionIds.NewTerminal));
        Assert.Equal(new KeyGesture(Key.P, KeyModifiers.Control), KeymapCatalog.DefaultGesture(KeymapActionIds.OpenFilePalette));
        Assert.Equal(new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift), KeymapCatalog.DefaultGesture(KeymapActionIds.OpenCommandPalette));
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift), KeymapCatalog.DefaultGesture(KeymapActionIds.OpenSearchPalette));
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), KeymapCatalog.DefaultGesture(KeymapActionIds.SaveDocument));
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.None), KeymapCatalog.DefaultGesture(KeymapActionIds.NextUnviewedChange));
    }

    /// <summary>
    /// The palette shows the four global commands twice — the badge on the command row and the gesture on the source strip — and
    /// both resolve through <see cref="KeymapApplier"/>, so a rebind has to move both and leave the commands without one empty.
    /// </summary>
    [Fact]
    public void The_palette_badges_and_the_source_strip_follow_a_rebind()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            var files = new PaletteCommand(PaletteCommandIds.Files, PaletteStrings.CommandFiles, PaletteStrings.CategoryNavigate);
            var content = new PaletteCommand(PaletteCommandIds.Content, PaletteStrings.CommandContent, PaletteStrings.CategoryNavigate);
            var commands = new PaletteCommand(PaletteCommandIds.Commands, PaletteStrings.CommandCommands, PaletteStrings.CategoryNavigate);
            var terminal = new PaletteCommand(PaletteCommandIds.Terminal, PaletteStrings.CommandTerminal, PaletteStrings.CategoryView);
            var sessions = new PaletteCommand(PaletteCommandIds.Sessions, PaletteStrings.CommandSessions, PaletteStrings.CategoryNavigate);

            Assert.Equal("Ctrl+P", files.Gesture);

            preferences.KeymapOverrides = ["window.openFilePalette|Ctrl+Shift+O", "window.openSearchPalette|Ctrl+Alt+F"];

            Assert.Equal("Ctrl+Shift+O", files.Gesture);
            Assert.Equal("Ctrl+Alt+F", content.Gesture);
            Assert.Equal("Ctrl+Shift+P", commands.Gesture);
            Assert.Equal("Ctrl+`", terminal.Gesture);
            Assert.Equal(string.Empty, sessions.Gesture);

            var item = Assert.Single(PaletteSources.Commands([files], string.Empty, 10));
            Assert.Equal("Ctrl+Shift+O", item.Gesture);
            Assert.Equal("Ctrl+Shift+O", item.Badge);

            var strip = new PaletteSourceOption(PaletteSource.Files, PaletteStrings.SourceFiles, KeymapActionIds.OpenFilePalette);
            Assert.Equal("Ctrl+Shift+O", strip.Gesture);
            Assert.True(strip.HasGesture);
            Assert.Equal(Loc.Instance[PaletteStrings.SourceFiles] + " (Ctrl+Shift+O)", strip.AccessibleName);

            var plain = new PaletteSourceOption(PaletteSource.Sessions, PaletteStrings.SourceSessions, null);
            Assert.False(plain.HasGesture);
            Assert.Equal(Loc.Instance[PaletteStrings.SourceSessions], plain.AccessibleName);
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    /// <summary>
    /// The palette's source strip belongs to <see cref="PaletteViewModel"/>: it resolves the four opening gestures on read, so a
    /// rebind moves them, but a strip nobody re-reads keeps drawing the old key until the palette is reopened. The same holds
    /// for a command row's badge, which is baked into the immutable row and comes back only from a rebuild — so the view model
    /// listens to <see cref="KeymapApplier.Changed"/> and does both.
    /// </summary>
    [Fact]
    public void The_palette_view_model_re_announces_its_source_strip_and_command_badges_after_a_rebind()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        using var palette = new PaletteViewModel(new FakePaletteHost(), preferences);
        try
        {
            palette.Open(PaletteSource.Commands);
            Assert.Equal("Ctrl+P", palette.Sources[0].Gesture);
            Assert.Equal("Ctrl+P", GestureOf(palette, PaletteCommandIds.Files));

            var changed = PropertyChangeLog.For(palette);
            preferences.KeymapOverrides = ["window.openFilePalette|Ctrl+Shift+O"];

            Assert.Contains(nameof(PaletteViewModel.Sources), changed);
            Assert.Equal("Ctrl+Shift+O", palette.Sources[0].Gesture);

            // Only the rebound source moved; the content source keeps the gesture its own action resolves to.
            Assert.Equal("Ctrl+Shift+F", palette.Sources[1].Gesture);
            Assert.Equal("Ctrl+Shift+O", GestureOf(palette, PaletteCommandIds.Files));
            Assert.Equal("Ctrl+Shift+F", GestureOf(palette, PaletteCommandIds.Content));
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    private static string GestureOf(PaletteViewModel palette, string commandId) =>
        palette.Items.Single(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal)).Gesture;

    /// <summary>
    /// The two parameterized keymap sentences take the gesture text and the conflicting action's name as arguments, so a
    /// gesture change or a language switch reaches them without a second copy of the sentence. The English side must not fall
    /// back to the Chinese template (a missing <c>{0}</c> on one side is what the string-table gate catches statically; this
    /// case proves the substitution itself).
    /// </summary>
    [Fact]
    public void The_parameterized_texts_substitute_the_gesture_and_the_action_name_in_both_languages()
    {
        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("Default Ctrl+S", KeymapStrings.DefaultGesture("Ctrl+S"));
            Assert.Equal(
                "Conflicts with " + Loc.Instance[KeymapStrings.ActionEditorUndoKey] + ".",
                KeymapStrings.Conflict(KeymapStrings.ActionName(KeymapStrings.ActionEditorUndoKey)));
        }

        using (new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Equal("默认 Ctrl+S", KeymapStrings.DefaultGesture("Ctrl+S"));
            Assert.Equal(
                "与 " + Loc.Instance[KeymapStrings.ActionEditorUndoKey] + " 冲突。",
                KeymapStrings.Conflict(KeymapStrings.ActionName(KeymapStrings.ActionEditorUndoKey)));
        }
    }
}
