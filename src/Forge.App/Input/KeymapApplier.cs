using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Input;
using Forge.App.Preferences;

namespace Forge.App.Input;

/// <summary>
/// Applies the catalog plus the stored overrides to the live controls. It keeps a snapshot of
/// <see cref="AppPreferences.KeymapOverrides"/> and re-reads it when the preference changes, so a rebind reaches the window
/// key bindings, the editor menu and the view key handlers without a restart. The snapshot is read on the UI thread exactly
/// like the preference itself, and the fully resolved gesture table is built once per change — a key press is one dictionary
/// look-up and allocates nothing.
/// </summary>
internal static class KeymapApplier
{
    private static AppPreferences? bound;
    private static IReadOnlyDictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, KeyGesture> resolved = BuildResolved(overrides);

    /// <summary>Raised after <see cref="AppPreferences.KeymapOverrides"/> changes; the window and the views rebuild from it.</summary>
    public static event Action? Changed;

    /// <summary>Subscribes to <paramref name="preferences"/> and captures its current overrides; a second bind of the same instance is a no-op.</summary>
    public static void Bind(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (ReferenceEquals(bound, preferences))
        {
            return;
        }

        Unbind();
        bound = preferences;
        preferences.PropertyChanged += OnPreferencesChanged;
        overrides = ParseOverrides(preferences.KeymapOverrides);
        resolved = BuildResolved(overrides);
    }

    /// <summary>Unsubscribes and drops the snapshot; gesture resolution falls back to the catalog defaults afterwards.</summary>
    public static void Unbind()
    {
        if (bound is { } previous)
        {
            previous.PropertyChanged -= OnPreferencesChanged;
            bound = null;
        }

        overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        resolved = BuildResolved(overrides);
    }

    /// <summary>
    /// Re-reads the bound preferences and notifies. Used once after the initial settings load, which fills
    /// <see cref="AppPreferences.KeymapOverrides"/> without raising PropertyChanged (the same seam the rate-limit wave
    /// publishes through), and available to a reload path that wants the live keymap to catch up.
    /// </summary>
    public static void Reload()
    {
        if (bound is null)
        {
            return;
        }

        overrides = ParseOverrides(bound.KeymapOverrides);
        resolved = BuildResolved(overrides);
        Changed?.Invoke();
    }

    /// <summary>The resolved gesture for <paramref name="actionId"/>: the stored override when it parses, otherwise the catalog default.</summary>
    public static KeyGesture Gesture(string actionId) =>
        resolved.TryGetValue(actionId, out var gesture) ? gesture : KeymapCatalog.DefaultGesture(actionId);

    /// <summary>The human-friendly gesture text for <paramref name="actionId"/>, following the language-agnostic gesture spelling.</summary>
    public static string DisplayGesture(string actionId) => KeymapGesture.Format(Gesture(actionId));

    /// <summary>
    /// The fully resolved gesture map (override or default for every catalog action), used by the settings page to detect
    /// conflicts against what keys actually do. It is the snapshot the applier already holds, so this is a read, not a build.
    /// </summary>
    public static IReadOnlyDictionary<string, KeyGesture> ResolveAll() => resolved;

    /// <summary>The id of the action in <paramref name="actionId"/>'s key chain that already uses <paramref name="candidate"/>, or null.</summary>
    public static string? FindConflict(string actionId, KeyGesture candidate) =>
        KeymapGesture.FindConflict(actionId, candidate, resolved);

    /// <summary>The window's four key bindings, built from the catalog and overrides; the window owns adding them to its collection.</summary>
    public static IReadOnlyList<KeyBinding> WindowKeyBindings(
        ICommand newTerminal,
        ICommand openFilePalette,
        ICommand openCommandPalette,
        ICommand openSearchPalette) =>
    [
        new KeyBinding { Gesture = Gesture(KeymapActionIds.NewTerminal), Command = newTerminal },
        new KeyBinding { Gesture = Gesture(KeymapActionIds.OpenFilePalette), Command = openFilePalette },
        new KeyBinding { Gesture = Gesture(KeymapActionIds.OpenCommandPalette), Command = openCommandPalette },
        new KeyBinding { Gesture = Gesture(KeymapActionIds.OpenSearchPalette), Command = openSearchPalette },
    ];

    private static void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (bound is null || !string.Equals(e.PropertyName, nameof(AppPreferences.KeymapOverrides), StringComparison.Ordinal))
        {
            return;
        }

        overrides = ParseOverrides(bound.KeymapOverrides);
        resolved = BuildResolved(overrides);
        Changed?.Invoke();
    }

    /// <summary>
    /// The one resolution pass: every catalog action maps to its override when that parses and to its catalog default
    /// otherwise, so an unknown id or an unparsable gesture is simply absent-or-default rather than an error at key time.
    /// </summary>
    private static Dictionary<string, KeyGesture> BuildResolved(IReadOnlyDictionary<string, string> parsed)
    {
        var map = new Dictionary<string, KeyGesture>(KeymapCatalog.Actions.Count, StringComparer.Ordinal);
        foreach (var action in KeymapCatalog.Actions)
        {
            map[action.Id] =
                parsed.TryGetValue(action.Id, out var text) && KeymapGesture.TryParse(text, out var gesture) && gesture is not null
                    ? gesture
                    : action.DefaultGesture;
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ParseOverrides(IReadOnlyList<string> entries)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var separator = entry.IndexOf(AppPreferences.KeymapOverrideSeparator);
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            parsed[entry[..separator]] = entry[(separator + 1)..];
        }

        return parsed;
    }
}
