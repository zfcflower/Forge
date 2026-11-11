using Forge.Abstractions;

namespace Forge.App.Providers;

/// <summary>
/// The seven reasoning-effort steps as the app stores and shows them: the lowercase names in <c>settings.json</c> and
/// <c>composer.json</c>, and the clamp to the steps a model accepts. One place, so a provider's stored default, the
/// composer's saved default and an agent's default can never disagree about what <c>"xhigh"</c> means.
/// </summary>
internal static class ThinkingEfforts
{
    /// <summary>The stored and wire-facing name of one step.</summary>
    public static string Name(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Minimal => "minimal",
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.XHigh => "xhigh",
        ThinkingEffort.Max => "max",
        _ => "off",
    };

    /// <summary>Reads a stored name; an unknown or missing name is not a step.</summary>
    public static bool TryParse(string? text, out ThinkingEffort effort)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "off":
                effort = ThinkingEffort.Off;
                return true;
            case "minimal":
                effort = ThinkingEffort.Minimal;
                return true;
            case "low":
                effort = ThinkingEffort.Low;
                return true;
            case "medium":
                effort = ThinkingEffort.Medium;
                return true;
            case "high":
                effort = ThinkingEffort.High;
                return true;
            case "xhigh":
                effort = ThinkingEffort.XHigh;
                return true;
            case "max":
                effort = ThinkingEffort.Max;
                return true;
            default:
                effort = ThinkingEffort.Off;
                return false;
        }
    }

    /// <summary>
    /// The nearest accepted step at or below <paramref name="effort"/>; never a step the model does not accept. The
    /// accepted list is ascending and starts with <see cref="ThinkingEffort.Off"/>, so Off is always a safe answer.
    /// </summary>
    public static ThinkingEffort Clamp(ThinkingEffort effort, IReadOnlyList<ThinkingEffort> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (accepted.Count == 0 || accepted.Contains(effort))
        {
            return accepted.Count == 0 ? ThinkingEffort.Off : effort;
        }

        var nearest = ThinkingEffort.Off;
        var found = false;
        foreach (var step in accepted)
        {
            if (step <= effort)
            {
                nearest = step;
                found = true;
            }
        }

        // A table without Off is not supposed to exist; if it does, the lowest accepted step is still better than a step
        // the model rejects.
        return found ? nearest : accepted[0];
    }

    /// <summary>The zero-based position of <paramref name="effort"/> in the accepted steps; 0 when it is not one.</summary>
    public static int IndexOf(ThinkingEffort effort, IReadOnlyList<ThinkingEffort> accepted)
    {
        for (var index = 0; index < accepted.Count; index++)
        {
            if (accepted[index] == effort)
            {
                return index;
            }
        }

        return 0;
    }
}
