using System.Windows.Input;
using Avalonia.Input;
using Forge.App.Input;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// <see cref="KeymapApplier"/> is the one piece of process-wide state in the wave: it holds a static subscription to the live
/// <see cref="AppPreferences"/> and a static <see cref="KeymapApplier.Changed"/> event that every window and view subscribes to.
/// These cases pin the lifecycle a test (or a second window) can break — a double bind, a bind without unbind, a stale
/// snapshot — plus the reload seam the shell needs because the initial settings load never raises <c>PropertyChanged</c>.
/// </summary>
public sealed class KeymapApplierTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-keymap-applier-");

    /// <summary>Never leave the static subscription behind: the next test class would inherit it.</summary>
    public void Dispose()
    {
        KeymapApplier.Unbind();
        TestTempFiles.DeleteQuietly(root);
    }

    [Fact]
    public void Binding_the_same_instance_twice_subscribes_once()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        KeymapApplier.Bind(preferences);

        var raised = TrackChanges();
        try
        {
            preferences.KeymapOverrides = ["view.saveDocument|Ctrl+U"];

            Assert.Equal(1, raised.Count);
            Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));
        }
        finally
        {
            KeymapApplier.Changed -= raised.OnChanged;
        }
    }

    [Fact]
    public void Unbind_drops_the_snapshot_and_stops_listening()
    {
        var first = new AppPreferences();
        KeymapApplier.Bind(first);
        first.KeymapOverrides = ["view.saveDocument|Ctrl+U"];
        Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

        var raised = TrackChanges();
        try
        {
            KeymapApplier.Unbind();

            first.KeymapOverrides = ["view.saveDocument|Ctrl+K"];
            Assert.Equal(0, raised.Count);
            Assert.Equal(KeymapCatalog.DefaultGesture(KeymapActionIds.SaveDocument), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

            // Binding a second instance detaches the first: only the live one may rebuild the keymap.
            var second = new AppPreferences();
            KeymapApplier.Bind(first);
            KeymapApplier.Bind(second);
            second.KeymapOverrides = ["view.saveDocument|Ctrl+J"];
            Assert.Equal(1, raised.Count);
            Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

            first.KeymapOverrides = ["view.saveDocument|Ctrl+K"];
            Assert.Equal(1, raised.Count);
            Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));
        }
        finally
        {
            KeymapApplier.Changed -= raised.OnChanged;
        }
    }

    [Fact]
    public void A_change_to_another_preference_does_not_rebuild_the_keymap()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);

        var raised = TrackChanges();
        try
        {
            preferences.UpdatesChannel = "beta";
            preferences.UpdatesCheckFrequency = "weekly";
            Assert.Equal(0, raised.Count);
        }
        finally
        {
            KeymapApplier.Changed -= raised.OnChanged;
        }
    }

    /// <summary>
    /// A row id from an older (or edited) settings file must not throw and must not invent an action: the resolved map is exactly
    /// the catalog, so every row the settings page builds still has a gesture and a stale id reaches nobody.
    /// </summary>
    [Fact]
    public void An_override_for_an_unknown_action_stays_out_of_the_resolved_map()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        preferences.KeymapOverrides = ["window.doesNotExist|Ctrl+U", "view.saveDocument|Ctrl+J"];

        var resolved = KeymapApplier.ResolveAll();

        Assert.Equal(KeymapCatalog.Actions.Count, resolved.Count);
        Assert.DoesNotContain("window.doesNotExist", resolved.Keys);
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Control), resolved[KeymapActionIds.SaveDocument]);
        Assert.Null(KeymapCatalog.Find("window.doesNotExist"));
    }

    /// <summary>
    /// The initial settings load fills <see cref="AppPreferences"/> through <c>ApplyLoaded</c>, which raises no
    /// <c>PropertyChanged</c> (the applier was bound at composition time, before the file was read). Without
    /// <see cref="KeymapApplier.Reload"/> a stored rebind would not reach the keys until the next edit or restart.
    /// </summary>
    [Fact]
    public async Task Reload_catches_up_on_a_settings_load_that_raises_no_change()
    {
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, """{ "preferences": { "keymapOverrides": ["view.saveDocument|Ctrl+U"] } }""");

        await using var store = new PreferencesStore(path);
        KeymapApplier.Bind(store.Current);
        try
        {
            await store.LoadAsync(CancellationToken.None);
            Assert.Null(store.LoadProblem);
            Assert.Equal(["view.saveDocument|Ctrl+U"], store.Current.KeymapOverrides);

            var raised = TrackChanges();
            try
            {
                Assert.Equal(KeymapCatalog.DefaultGesture(KeymapActionIds.SaveDocument), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));

                KeymapApplier.Reload();

                Assert.Equal(1, raised.Count);
                Assert.Equal(new KeyGesture(Key.U, KeyModifiers.Control), KeymapApplier.Gesture(KeymapActionIds.SaveDocument));
            }
            finally
            {
                KeymapApplier.Changed -= raised.OnChanged;
            }
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    /// <summary>
    /// The window's four bindings are built from the catalog in a fixed order, so a rebind swaps the gesture but keeps the
    /// command: the shell hands the applier its four commands and never looks an index up itself.
    /// </summary>
    [Fact]
    public void The_window_bindings_carry_the_catalog_gestures_and_follow_a_rebind()
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        try
        {
            var newTerminal = new RelayCommand(static () => { });
            var files = new RelayCommand(static () => { });
            var commands = new RelayCommand(static () => { });
            var search = new RelayCommand(static () => { });

            var bindings = KeymapApplier.WindowKeyBindings(newTerminal, files, commands, search);

            Assert.Equal(4, bindings.Count);
            Assert.Equal(
                [
                    new KeyGesture(Key.OemTilde, KeyModifiers.Control),
                    new KeyGesture(Key.P, KeyModifiers.Control),
                    new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift),
                    new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift),
                ],
                bindings.Select(static binding => binding.Gesture).ToArray());
            Assert.Equal<ICommand?[]>([newTerminal, files, commands, search], bindings.Select(static binding => binding.Command).ToArray());

            preferences.KeymapOverrides = ["window.openFilePalette|Ctrl+Shift+O"];

            var rebound = KeymapApplier.WindowKeyBindings(newTerminal, files, commands, search);
            Assert.Equal(4, rebound.Count);
            Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift), rebound[1].Gesture);
            Assert.Same(files, rebound[1].Command);
            Assert.Equal(new KeyGesture(Key.OemTilde, KeyModifiers.Control), rebound[0].Gesture);
            Assert.Equal(new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift), rebound[2].Gesture);
            Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift), rebound[3].Gesture);
        }
        finally
        {
            KeymapApplier.Unbind();
        }
    }

    private static ChangeCounter TrackChanges()
    {
        var counter = new ChangeCounter();
        KeymapApplier.Changed += counter.OnChanged;
        return counter;
    }

    /// <summary>Counts <see cref="KeymapApplier.Changed"/> without a lambda, so the test can unsubscribe the same delegate.</summary>
    private sealed class ChangeCounter
    {
        public int Count { get; private set; }

        public void OnChanged() => Count++;
    }
}
