using System.ComponentModel;

namespace Forge.App.Tests;

/// <summary>
/// Records the property names a view model or preference object raises, in order, so a test can assert on exactly
/// which notifications its change caused — including "none".
/// </summary>
internal static class PropertyChangeLog
{
    /// <summary>Subscribes to <paramref name="source"/> and returns the list every later notification appends to.</summary>
    public static List<string?> For(INotifyPropertyChanged source)
    {
        var names = new List<string?>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        return names;
    }
}
