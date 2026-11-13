using System.Collections.Concurrent;

namespace Forge.Core.Providers;

/// <summary>
/// Which providers in this process already refused part of the request's shape, so the reduced shape is used from the start instead
/// of paying a wasted round trip per turn. A capability of the account (or of the gateway in front of it), not of one request.
/// </summary>
/// <remarks>
/// Process-wide on purpose and cleared by <see cref="Clear"/> when the account or its settings change — a re-signed-in credential or a
/// reloaded catalogue must not leave an account stuck on the reduced shape.
/// </remarks>
public static class ProviderDegradations
{
    private static readonly ConcurrentDictionary<string, byte> Degraded = new(StringComparer.Ordinal);

    public static bool Contains(string key) => Degraded.ContainsKey(key);

    public static void Add(string key) => Degraded.TryAdd(key, 0);

    public static void Clear() => Degraded.Clear();
}
