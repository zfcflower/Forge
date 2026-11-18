using System.Collections.Concurrent;

namespace Forge.Core.Providers;

/// <summary>
/// Which providers in this process already refused part of the request's shape, so the reduced shape is used from the start instead
/// of paying a wasted round trip per turn. A capability of the account (or of the gateway in front of it), not of one request.
/// </summary>
/// <remarks>
/// Process-wide on purpose and cleared by <see cref="Clear"/> when the account or its settings change — a re-signed-in credential or a
/// reloaded catalogue must not leave an account stuck on the reduced shape. An entry also expires after <see cref="Lifetime"/>: the
/// key does not name the model, so one model's refusal would otherwise keep every other model behind the same gateway on the
/// reduced shape until the process ends.
/// </remarks>
public static class ProviderDegradations
{
    /// <summary>How long a refusal is remembered before the full shape is tried again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<string, long> Degraded = new(StringComparer.Ordinal);

    public static bool Contains(string key)
    {
        if (!Degraded.TryGetValue(key, out var since))
        {
            return false;
        }

        if (Environment.TickCount64 - since < (long)Lifetime.TotalMilliseconds)
        {
            return true;
        }

        Degraded.TryRemove(new KeyValuePair<string, long>(key, since));
        return false;
    }

    public static void Add(string key) => Degraded[key] = Environment.TickCount64;

    public static void Clear() => Degraded.Clear();
}
