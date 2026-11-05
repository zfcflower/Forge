using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Forge.App.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base. The project has no MVVM package on purpose: a source-generated or
/// reflection-driven framework is not worth an extra dependency here, and hand-written setters stay AOT-clean.
/// </summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>
    /// One event-args instance per property name. Property change notifications fire on drag and resize paths, so the
    /// args must not be allocated on every notification. Read-mostly: the dictionary grows only for property names that
    /// are actually raised, and is thread-safe because view models are also observed off the UI thread.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> EventArgsCache = new(StringComparer.Ordinal);
    private static readonly PropertyChangedEventArgs EmptyEventArgs = new(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, propertyName is null
            ? EmptyEventArgs
            : EventArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name)));
}
