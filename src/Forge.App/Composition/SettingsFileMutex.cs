using System.Collections.Concurrent;

namespace Forge.App.Composition;

/// <summary>
/// Serializes the read–merge–write cycle that more than one writer runs over <c>settings.json</c>. Each writer preserves the
/// sections it does not own by re-reading the file, so two writers that overlap would both merge into the same snapshot and
/// the later one would silently drop the earlier one's section. An atomic replace makes the file valid, not the merge
/// correct; this mutex is what makes the merge correct.
/// <para>Process-local by design: two app instances editing the same file is out of scope (each still writes a valid file),
/// and the lock is held only around the file work, never across an await.</para>
/// </summary>
internal static class SettingsFileMutex
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the lock for one settings path; dispose the result to release it.</summary>
    public static IDisposable Acquire(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        var gate = Gates.GetOrAdd(Path.GetFullPath(settingsPath), static _ => new object());
        Monitor.Enter(gate);
        return new Handle(gate);
    }

    private sealed class Handle(object gate) : IDisposable
    {
        private object? held = gate;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref held, null) is { } released)
            {
                Monitor.Exit(released);
            }
        }
    }
}
