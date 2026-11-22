using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// A per-spawn model override as the caller asked for it, plus the settings the kind would run with without one.
/// The app resolves it against its model catalogue; Core only carries the request, so a host without an app behind
/// it (a script, a test) simply has no override parameters on <c>spawn_agent</c>.
/// </summary>
/// <param name="Model">The model the user named: an id, or words a provider's list can be matched against. Null or
/// blank keeps <paramref name="Current"/>'s model, which is how a thinking-only or provider-only override works.</param>
/// <param name="Provider">A connected provider id or display name to narrow the model lookup. Null or blank accepts
/// every connected provider.</param>
/// <param name="Thinking">A thinking-step name (off/minimal/low/medium/high/xhigh/max). Null or blank keeps the step
/// the kind would have used, clamped to the model's accepted steps.</param>
/// <param name="Current">What the kind runs with today, already resolved from <c>settings.json</c> by the host build.</param>
public sealed record SpawnModelRequest(string? Model, string? Provider, string? Thinking, ModelSettings Current);

/// <summary>
/// What a per-spawn override resolved to: the complete settings to run the child with, or why it cannot.
/// </summary>
/// <param name="Settings">The child's settings (model, provider, thinking and output ceiling); null on refusal.</param>
/// <param name="Error">Model-facing refusal text; null when <paramref name="Settings"/> is set.</param>
/// <param name="Note">One line the spawn receipt appends, such as a clamp. Never an error.</param>
public sealed record SpawnModelChoice(ModelSettings? Settings, string? Error = null, string? Note = null)
{
    /// <summary>A resolved override; <paramref name="note"/> explains a clamp or a filled-in provider.</summary>
    public static SpawnModelChoice Resolved(ModelSettings settings, string? note = null) => new(settings, Note: note);

    /// <summary>A refusal the model can act on: unknown id, candidates, unknown provider, a step the model rejects.</summary>
    public static SpawnModelChoice Refused(string error) => new(null, error);
}
