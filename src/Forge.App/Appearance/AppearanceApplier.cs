using Avalonia.Controls;
using Forge.App.Preferences;

namespace Forge.App.Appearance;

/// <summary>
/// Pushes the font preferences into a resource dictionary (at runtime <c>Application.Current.Resources</c>) under
/// <see cref="FontResourceKeys"/>. UI thread only.
/// <para>The values live in one child dictionary owned by this class and merged into the target; every apply builds a new
/// child and swaps it in, so one preference change raises one resource-change notification instead of one per key. Only
/// the four font properties trigger an apply, and an apply whose values equal the current ones swaps nothing. A family
/// that does not parse keeps the previous valid family and is reported through <c>Trace</c>.</para>
/// </summary>
internal sealed class AppearanceApplier : IDisposable
{
    /// <summary>Subscribes to <paramref name="preferences"/> and applies the current values immediately.</summary>
    public AppearanceApplier(AppPreferences preferences, IResourceDictionary target) => throw new NotImplementedException();

    /// <summary>The child dictionary currently merged into the target; replaced (new instance) on every effective apply.</summary>
    internal ResourceDictionary Tokens => throw new NotImplementedException();

    /// <summary>
    /// Re-reads all four preferences and swaps the child dictionary if any resulting value differs. Called by the
    /// composition root after the preferences finished loading (the load raises no PropertyChanged).
    /// </summary>
    public void Apply() => throw new NotImplementedException();

    /// <summary>Unsubscribes; the last applied dictionary stays merged. Idempotent.</summary>
    public void Dispose() => throw new NotImplementedException();
}
