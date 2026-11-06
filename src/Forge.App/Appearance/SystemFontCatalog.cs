using System.Diagnostics;
using Avalonia.Media;

namespace Forge.App.Appearance;

/// <summary>
/// Installed font family names for the appearance settings. The first request enumerates on the thread pool; the same
/// task is returned afterwards (no second enumeration). Names are distinct and sorted ordinal-ignore-case. A failing
/// enumeration yields an empty list and a <c>Trace</c> line, never an exception: the user can still type a family.
/// </summary>
internal sealed class SystemFontCatalog
{
    private readonly Func<IEnumerable<string>> source;
    private readonly Lock gate = new();
    private Task<IReadOnlyList<string>>? families;

    /// <param name="source">Enumerates family names; called at most once, on a pool thread.</param>
    public SystemFontCatalog(Func<IEnumerable<string>> source) =>
        this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The catalog over <c>FontManager.Current.SystemFonts</c>.</summary>
    public static SystemFontCatalog CreateDefault() => new(EnumerateSystemFamilies);

    public Task<IReadOnlyList<string>> GetFamiliesAsync()
    {
        lock (gate)
        {
            // Started inside the lock so two callers cannot race into two enumerations.
            return families ??= Task.Run(Load);
        }
    }

    private IReadOnlyList<string> Load()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in source())
            {
                var trimmed = name?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    names.Add(trimmed);
                }
            }

            var sorted = new List<string>(names);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted;
        }
        catch (Exception exception)
        {
            // A font backend problem must not take the settings page down; an empty list only means the user types.
            Trace.WriteLine("[appearance] the system font families could not be enumerated: " + exception.Message);
            return [];
        }
    }

    /// <summary>Touches <see cref="FontManager"/> only when enumerated, never when the catalog is created.</summary>
    private static IEnumerable<string> EnumerateSystemFamilies()
    {
        foreach (var family in FontManager.Current.SystemFonts)
        {
            yield return family.Name;
        }
    }
}
