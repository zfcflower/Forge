using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.Preferences;
using Forge.App.ViewModels;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Appearance;

/// <summary>
/// Pushes the appearance preferences into the app: the font values into a resource dictionary (at runtime
/// <c>Application.Current.Resources</c>) under <see cref="FontResourceKeys"/>, and the motion switch into the global
/// <see cref="TreeMotion"/> gate that both the tree and the chat timeline read. UI thread only.
/// <para>The values live in one child dictionary owned by this class and merged into the target; every apply builds a new
/// child and swaps it in place, so a preference change raises at most two whole-dictionary notifications (Avalonia
/// notifies once when the old child lets go of the host and once when the new one takes it) instead of one per key. Only
/// the four font properties and the motion switch trigger an apply, and a font apply whose values equal the current ones
/// swaps nothing. A family that does not parse keeps the previous valid family and is reported through <c>Trace</c>.</para>
/// </summary>
internal sealed class AppearanceApplier : IDisposable
{
    /// <summary>Fluent's <c>Window</c> and popup control themes take the family from this key.</summary>
    private const string ThemeFontFamilyKey = "ContentControlThemeFontFamily";

    /// <summary>... and the size from this one; both are overridden so popups and flyouts follow the interface font too.</summary>
    private const string ThemeFontSizeKey = "ControlContentThemeFontSize";

    private readonly AppPreferences preferences;
    private readonly IResourceDictionary target;
    private readonly PropertyChangedEventHandler preferenceChanged;
    private ResourceDictionary tokens = new();
    private Applied? applied;
    private string? rejectedUi;
    private string? rejectedCode;
    private bool disposed;

    /// <summary>Subscribes to <paramref name="preferences"/> and applies the current values immediately.</summary>
    public AppearanceApplier(AppPreferences preferences, IResourceDictionary target)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        preferenceChanged = OnPreferenceChanged;
        preferences.PropertyChanged += preferenceChanged;
        Apply();
    }

    /// <summary>The child dictionary currently merged into the target; replaced (new instance) on every effective apply.</summary>
    internal ResourceDictionary Tokens => tokens;

    /// <summary>
    /// Re-reads all four preferences and swaps the child dictionary if any resulting value differs. Called by the
    /// composition root after the preferences finished loading (the load raises no PropertyChanged).
    /// </summary>
    public void Apply()
    {
        if (disposed)
        {
            return;
        }

        var ui = Resolve(preferences.UiFontFamily, applied?.Ui, "interface", AppPreferences.DefaultUiFontFamily, ref rejectedUi);
        var code = Resolve(preferences.CodeFontFamily, applied?.Code, "code", AppPreferences.DefaultCodeFontFamily, ref rejectedCode);
        var sizes = FontScale.Compute(preferences.UiFontSize);
        var codeSize = preferences.CodeFontSize;

        // Not a resource: the animation gate is read by live controls (the tree rows and the chat timeline's gutters), which
        // subscribe to TreeMotion.Changed; setting it before the early return keeps the switch live even when no font changed.
        TreeMotion.ReducedMotion = preferences.ReduceMotion;

        // Compare the resolved values, not the raw text: a family that failed to parse resolved to the previous one,
        // so re-reporting the same broken text is not a change.
        if (applied is { } last
            && last.Ui.Text.Equals(ui.Text, StringComparison.Ordinal)
            && last.Code.Text.Equals(code.Text, StringComparison.Ordinal)
            && last.Sizes == sizes
            && last.CodeSize.Equals(codeSize))
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            [FontResourceKeys.Ui] = ui.Value,
            [FontResourceKeys.Mono] = code.Value,
            [FontResourceKeys.SizeXs] = sizes.Xs,
            [FontResourceKeys.SizeSm] = sizes.Sm,
            [FontResourceKeys.SizeBase] = sizes.Base,
            [FontResourceKeys.SizeMd] = sizes.Md,
            [FontResourceKeys.SizeLg] = sizes.Lg,
            [FontResourceKeys.SizeXl] = sizes.Xl,
            [FontResourceKeys.SizeCode] = codeSize,
            [ThemeFontFamilyKey] = ui.Value,
            [ThemeFontSizeKey] = sizes.Base,
        };

        var merged = target.MergedDictionaries;
        var index = IndexOfTokens(merged);
        if (index >= 0)
        {
            // One replace of the whole child instead of ten per-key writes.
            merged[index] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }

        tokens = dictionary;
        applied = new Applied(ui, code, sizes, codeSize);
    }

    /// <summary>Unsubscribes; the last applied dictionary stays merged. Idempotent.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        preferences.PropertyChanged -= preferenceChanged;
    }

    /// <summary>
    /// Reads a fallback chain into a <see cref="FontFamily"/>. A chain that does not parse (<see cref="FontFamily.Parse"/>
    /// throws on an empty string) or that carries no usable family name ("," or " ") keeps the previous family, so a typo
    /// can never blank out the interface; the problem goes to <c>Trace</c>. The same text is never parsed twice, which is
    /// what lets the caller compare effective values by text.
    /// </summary>
    private static ResolvedFamily Resolve(string text, ResolvedFamily? previous, string which, string defaultText, ref string? rejected)
    {
        if (previous is { } last && last.Text.Equals(text, StringComparison.Ordinal))
        {
            return last;
        }

        try
        {
            var family = FontFamily.Parse(text);
            if (HasFamilyName(family))
            {
                rejected = null;
                return new ResolvedFamily(text, family);
            }

            Reject(text, which, "has no usable family name", ref rejected);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        {
            Reject(text, which, "does not parse (" + exception.Message + ")", ref rejected);
        }

        return previous ?? new ResolvedFamily(defaultText, FontFamily.Parse(defaultText));
    }

    /// <summary>Traces a rejected text once; the same text arriving again (every Apply re-reads it) stays silent.</summary>
    private static void Reject(string text, string which, string reason, ref string? rejected)
    {
        if (string.Equals(rejected, text, StringComparison.Ordinal))
        {
            return;
        }

        rejected = text;
        AppLogs.Appearance.ZLogWarning($"the {which} font family \"{text}\" {reason}; keeping the previous family.");
    }

    /// <summary>A chain of only empty segments ("," or " ") parses to a family with no name that nothing can render.</summary>
    private static bool HasFamilyName(FontFamily family)
    {
        foreach (var name in family.FamilyNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                return true;
            }
        }

        return false;
    }

    private int IndexOfTokens(IList<IResourceProvider> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (ReferenceEquals(merged[i], tokens))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An empty name means "every property changed", which includes the fonts.
        if (e.PropertyName is not { Length: > 0 } name || IsAppliedPreference(name))
        {
            Apply();
        }
    }

    private static bool IsAppliedPreference(string name) =>
        string.Equals(name, nameof(AppPreferences.UiFontFamily), StringComparison.Ordinal)
        || string.Equals(name, nameof(AppPreferences.UiFontSize), StringComparison.Ordinal)
        || string.Equals(name, nameof(AppPreferences.CodeFontFamily), StringComparison.Ordinal)
        || string.Equals(name, nameof(AppPreferences.CodeFontSize), StringComparison.Ordinal)
        || string.Equals(name, nameof(AppPreferences.ReduceMotion), StringComparison.Ordinal);

    /// <summary>A resolved chain: the text it came from (the change key) and the family it produced.</summary>
    private readonly record struct ResolvedFamily(string Text, FontFamily Value);

    /// <summary>The values the merged dictionary currently carries; the applier swaps nothing while these reproduce.</summary>
    private readonly record struct Applied(ResolvedFamily Ui, ResolvedFamily Code, FontSizeTokens Sizes, double CodeSize);
}
