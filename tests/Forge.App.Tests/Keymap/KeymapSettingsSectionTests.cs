using Avalonia.Input;
using Forge.App.Input;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The <c>keymap</c> settings section (W10e): the three scope groups, search over name and gesture, the capture-and-confirm
/// rebind with same-scope conflict hint, single reset and reset-all, and the immediate effect on <see cref="KeymapApplier"/>.
/// </summary>
public sealed class KeymapSettingsSectionTests : IDisposable
{
    private readonly AppPreferences preferences;
    private readonly KeymapSettingsSectionViewModel section;

    public KeymapSettingsSectionTests()
    {
        preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        section = new KeymapSettingsSectionViewModel(preferences);
    }

    public void Dispose()
    {
        section.Dispose();
        KeymapApplier.Unbind();
    }

    [Fact]
    public void The_catalog_is_grouped_into_window_editor_and_view()
    {
        Assert.Equal(4, section.WindowRows.Count);
        Assert.Equal(13, section.EditorRows.Count);
        Assert.Equal(4, section.ViewRows.Count);
    }

    [Fact]
    public void Search_matches_the_action_name_and_the_current_gesture()
    {
        section.SearchText = "shift+f12";
        Assert.Single(section.ViewRows);
        Assert.Equal(KeymapActionIds.LspReferences, section.ViewRows[0].Id);

        section.SearchText = "ctrl+shift+p";
        Assert.Single(section.WindowRows);
        Assert.Equal(KeymapActionIds.OpenCommandPalette, section.WindowRows[0].Id);

        using (new LanguageScope(UiLanguage.English))
        {
            section.SearchText = "save";
            Assert.Single(section.ViewRows);
            Assert.Equal(KeymapActionIds.SaveDocument, section.ViewRows[0].Id);
        }

        section.SearchText = "zzz";
        Assert.Empty(section.WindowRows);
        Assert.Empty(section.EditorRows);
        Assert.Empty(section.ViewRows);
    }

    [Fact]
    public void A_captured_gesture_normalizes_and_confirms_into_an_override()
    {
        var row = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        section.BeginRebind(row);

        section.CaptureKey(Key.U, KeyModifiers.Control);
        Assert.Equal("Ctrl+U", section.CaptureGestureText);
        Assert.False(section.HasConflict);

        section.ConfirmCaptureCommand.Execute(null);

        Assert.Equal(["view.saveDocument|Ctrl+U"], preferences.KeymapOverrides);
        Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));
        Assert.True(row.IsOverridden);
    }

    [Fact]
    public void A_conflict_is_reported_with_the_action_name_but_confirm_still_writes()
    {
        var row = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        section.BeginRebind(row);

        // J is the default of the next-unviewed action in the same (view) scope.
        section.CaptureKey(Key.J, KeyModifiers.None);
        Assert.Equal("J", section.CaptureGestureText);
        Assert.True(section.HasConflict);

        section.ConfirmCaptureCommand.Execute(null);

        Assert.Equal(["view.saveDocument|J"], preferences.KeymapOverrides);
    }

    [Fact]
    public void A_lone_modifier_is_not_a_gesture_yet()
    {
        var row = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        section.BeginRebind(row);

        section.CaptureKey(Key.LeftCtrl, KeyModifiers.Control);
        Assert.False(section.HasCapturedGesture);
        Assert.Empty(section.CaptureConflictText);

        section.CaptureKey(Key.S, KeyModifiers.Control);
        Assert.True(section.HasCapturedGesture);
    }

    [Fact]
    public void Reset_removes_one_override_and_reset_all_clears_every_override()
    {
        preferences.KeymapOverrides =
        [
            "view.saveDocument|Ctrl+U",
            "window.newTerminal|Ctrl+T",
        ];

        var save = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        section.Reset(save);
        Assert.Equal(["window.newTerminal|Ctrl+T"], preferences.KeymapOverrides);
        Assert.False(save.IsOverridden);

        section.ResetAllCommand.Execute(null);
        Assert.Empty(preferences.KeymapOverrides);
    }

    [Fact]
    public void Reload_re_syncs_rows_after_a_direct_preference_change()
    {
        preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
        section.Reload();

        var save = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        Assert.Equal("Ctrl+U", save.CurrentGestureText);
        Assert.True(save.IsOverridden);
    }

    /// <summary>
    /// The page is <see cref="IReloadableSettingsSection"/> because a settings-file load fills the preferences without raising
    /// <c>PropertyChanged</c>, and the rows only re-read their computed values when something tells them to. Reloading must
    /// therefore re-read the preferences and notify the rows: nothing else is going to wake them up.
    /// </summary>
    [Fact]
    public async Task Reload_reads_a_preferences_load_that_raised_no_change()
    {
        var directory = TestTempFiles.NewDirectory("forge-keymap-section-");
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ \"preferences\": {} }");
        await using var store = new PreferencesStore(path);
        await store.LoadAsync(CancellationToken.None);

        // The fixture bound the applier to its own preferences; point it at the store's instance for this case.
        KeymapApplier.Bind(store.Current);
        try
        {
            using var fromStore = new KeymapSettingsSectionViewModel(store.Current);
            var save = fromStore.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
            var changed = PropertyChangeLog.For(save);

            await File.WriteAllTextAsync(path, """{ "preferences": { "keymapOverrides": ["view.saveDocument|Ctrl+U"] } }""");
            await store.LoadAsync(CancellationToken.None);
            Assert.Equal(["view.saveDocument|Ctrl+U"], store.Current.KeymapOverrides);
            Assert.Empty(changed);

            fromStore.Reload();

            Assert.Contains(nameof(KeymapRowViewModel.IsOverridden), changed);
            Assert.Contains(nameof(KeymapRowViewModel.CurrentGestureText), changed);
            Assert.True(save.IsOverridden);

            // The rows display through the applier, which the shell reloads for the same reason.
            KeymapApplier.Reload();
            Assert.Equal("Ctrl+U", save.CurrentGestureText);
            Assert.Equal("Ctrl+S", save.DefaultGestureText);
        }
        finally
        {
            KeymapApplier.Bind(preferences);
            TestTempFiles.DeleteQuietly(directory);
        }
    }

    /// <summary>
    /// The section's own labels are sentences, not keys: reading the string table's constants made the page draw
    /// <c>keymap.hint</c> and <c>keymap.scope.window</c> verbatim, which no registry test notices. They resolve at call time,
    /// so a language switch reaches them as well.
    /// </summary>
    [Fact]
    public void The_section_texts_are_resolved_sentences_in_both_languages()
    {
        foreach (var language in new[] { UiLanguage.Chinese, UiLanguage.English })
        {
            using var _ = new LanguageScope(language);

            Assert.Equal(Loc.Instance[KeymapStrings.HintKey], section.Hint);
            Assert.Equal(Loc.Instance[KeymapStrings.SearchPlaceholderKey], section.SearchPlaceholder);
            Assert.Equal(Loc.Instance[KeymapStrings.ScopeWindowKey], section.WindowScopeHeading);
            Assert.Equal(Loc.Instance[KeymapStrings.ScopeEditorKey], section.EditorScopeHeading);
            Assert.Equal(Loc.Instance[KeymapStrings.ScopeViewKey], section.ViewScopeHeading);
            Assert.Equal(Loc.Instance[KeymapStrings.ResetAllKey], section.ResetAllText);
            Assert.Equal(Loc.Instance[KeymapStrings.CapturePromptKey], section.CapturePrompt);
            Assert.Equal(Loc.Instance[KeymapStrings.CaptureConfirmKey], section.ConfirmText);
            Assert.Equal(Loc.Instance[KeymapStrings.CaptureCancelKey], section.CancelText);

            foreach (var text in new[]
                     {
                         section.Hint, section.SearchPlaceholder, section.WindowScopeHeading, section.EditorScopeHeading,
                         section.ViewScopeHeading, section.ResetAllText, section.CapturePrompt, section.ConfirmText,
                         section.CancelText,
                     })
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("keymap.", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The three scope groups have to keep their catalog membership under a filter: a match in one scope may not leak into
    /// another group's list, and clearing the box has to put every row back.
    /// </summary>
    [Fact]
    public void Search_keeps_each_match_in_its_scope_group_and_clearing_restores_every_row()
    {
        using (new LanguageScope(UiLanguage.English))
        {
            section.SearchText = "fold";

            Assert.Empty(section.WindowRows);
            Assert.Equal(
                [KeymapActionIds.EditorFoldAll, KeymapActionIds.EditorToggleFold, KeymapActionIds.EditorUnfoldAll],
                section.EditorRows.Select(static row => row.Id).Order(StringComparer.Ordinal).ToArray());
            Assert.Empty(section.ViewRows);
        }

        section.SearchText = string.Empty;

        Assert.Equal(4, section.WindowRows.Count);
        Assert.Equal(13, section.EditorRows.Count);
        Assert.Equal(4, section.ViewRows.Count);
    }

    [Fact]
    public void Search_matches_the_rebound_gesture_and_no_longer_the_default()
    {
        preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];

        section.SearchText = "ctrl+u";
        Assert.Equal(KeymapActionIds.SaveDocument, Assert.Single(section.ViewRows).Id);

        // The action moved off Ctrl+S, so its own default must not find it any more. (Another row may still match: the
        // search is a plain substring test, so "ctrl+s" also hits "Ctrl+Space".)
        section.SearchText = "ctrl+s";
        Assert.DoesNotContain(section.ViewRows, static row => row.Id == KeymapActionIds.SaveDocument);
    }

    /// <summary>
    /// One capture session at a time: starting a rebind on another row must drop the first row's capture affordance, and cancel
    /// must leave the preferences untouched (a capture is a draft until confirmed).
    /// </summary>
    [Fact]
    public void Only_one_row_captures_at_a_time_and_cancel_writes_nothing()
    {
        var save = section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument);
        var next = section.ViewRows.Single(static row => row.Id == KeymapActionIds.NextUnviewedChange);

        section.BeginRebind(save);
        Assert.True(save.IsCapturing);
        Assert.True(section.HasCapture);
        Assert.False(section.ConfirmCaptureCommand.CanExecute(null));

        section.BeginRebind(next);
        Assert.False(save.IsCapturing);
        Assert.True(next.IsCapturing);
        Assert.Same(next, section.CaptureRow);

        section.CaptureKey(Key.K, KeyModifiers.Control);
        Assert.Equal("Ctrl+K", section.CaptureGestureText);
        Assert.True(section.ConfirmCaptureCommand.CanExecute(null));

        section.CancelCaptureCommand.Execute(null);

        Assert.False(section.HasCapture);
        Assert.Null(section.CaptureRow);
        Assert.False(section.HasCapturedGesture);
        Assert.Equal(string.Empty, section.CaptureGestureText);
        Assert.False(next.IsCapturing);
        Assert.False(section.ConfirmCaptureCommand.CanExecute(null));
        Assert.False(section.CancelCaptureCommand.CanExecute(null));
        Assert.Empty(preferences.KeymapOverrides);
    }

    /// <summary>
    /// A gesture is occupied by what the keys actually do, so an action that was itself rebound out of a gesture frees it. Here a
    /// rebind onto another action's <em>override</em> is reported, resetting that action removes the conflict, and confirming
    /// anyway leaves both rows showing the same gesture (the user was warned, the file now says it twice).
    /// </summary>
    [Fact]
    public void A_conflict_with_another_override_is_reported_and_reset_frees_the_gesture()
    {
        preferences.KeymapOverrides = ["window.newTerminal|Ctrl+T"];
        var terminal = section.WindowRows.Single(static row => row.Id == KeymapActionIds.NewTerminal);
        var files = section.WindowRows.Single(static row => row.Id == KeymapActionIds.OpenFilePalette);

        section.BeginRebind(files);
        using (new LanguageScope(UiLanguage.English))
        {
            section.CaptureKey(Key.T, KeyModifiers.Control);
            Assert.True(section.HasConflict);
            Assert.Equal("Conflicts with New terminal.", section.CaptureConflictText);
        }

        section.Reset(terminal);
        section.CaptureKey(Key.T, KeyModifiers.Control);
        Assert.False(section.HasConflict);
        Assert.Equal(string.Empty, section.CaptureConflictText);

        // Confirm it anyway — the hint said so — and the override lands even though one gesture now sits on two window
        // actions; at runtime the first matching key binding wins, which is what the hint warned about.
        section.ConfirmCaptureCommand.Execute(null);
        Assert.Equal(["window.openFilePalette|Ctrl+T"], preferences.KeymapOverrides);
        Assert.Equal("Ctrl+T", files.CurrentGestureText);
        Assert.Equal("Ctrl+`", terminal.CurrentGestureText);
        Assert.False(terminal.IsOverridden);
    }

    [Fact]
    public void Reset_all_is_offered_only_while_an_override_exists()
    {
        Assert.False(section.ResetAllCommand.CanExecute(null));

        preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
        Assert.True(section.ResetAllCommand.CanExecute(null));

        section.ResetAllCommand.Execute(null);

        Assert.Empty(preferences.KeymapOverrides);
        Assert.False(section.ResetAllCommand.CanExecute(null));
        Assert.Equal("Ctrl+S", section.ViewRows.Single(static row => row.Id == KeymapActionIds.SaveDocument).CurrentGestureText);
    }
}
