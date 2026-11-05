namespace Forge.App.Appearance;

/// <summary>
/// Installed font family names for the appearance settings. The first request enumerates on the thread pool; the same
/// task is returned afterwards (no second enumeration). Names are distinct and sorted ordinal-ignore-case. A failing
/// enumeration yields an empty list and a <c>Trace</c> line, never an exception: the user can still type a family.
/// </summary>
internal sealed class SystemFontCatalog
{
    /// <param name="source">Enumerates family names; called at most once, on a pool thread.</param>
    public SystemFontCatalog(Func<IEnumerable<string>> source) => throw new NotImplementedException();

    /// <summary>The catalog over <c>FontManager.Current.SystemFonts</c>.</summary>
    public static SystemFontCatalog CreateDefault() => throw new NotImplementedException();

    public Task<IReadOnlyList<string>> GetFamiliesAsync() => throw new NotImplementedException();
}
