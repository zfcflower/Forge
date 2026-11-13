using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>Runtime limits for every agent the host runs.</summary>
public sealed record AgentOptions
{
    /// <summary>Model for the root agent when no definition is supplied.</summary>
    public ModelSettings DefaultModel { get; init; } = new("default");

    public int MaxTurns { get; init; } = 100;

    /// <summary>
    /// How many agents may be actively running at once, counted over the whole spawn tree rather than per
    /// depth: an agent is active while it streams a model turn or executes tools. An agent that is only
    /// waiting for the results of its own spawn_agent calls gives its slot back and takes it again before
    /// continuing, so the bound never starves children of the agents that spawned them and never deadlocks.
    /// </summary>
    public int MaxParallelSubAgents { get; init; } = 4;

    public int MaxSubAgentDepth { get; init; } = 3;

    /// <summary>
    /// When automatic compaction runs, relative to each model's window. Without a <see cref="CompactionPolicy.ContextWindow"/>
    /// resolver no window is known and only the overflow fallback and manual compaction apply.
    /// </summary>
    public CompactionPolicy Compaction { get; init; } = new();

    /// <summary>Tool output longer than this many characters moves into the artifact store.</summary>
    public int ArtifactThresholdChars { get; init; } = 32_000;

    public int ArtifactPreviewChars { get; init; } = 2_000;

    /// <summary>
    /// Prompt-cache lifetime policy (<see cref="CacheHint.Ttl"/>): <c>"5m"</c>, <c>"1h"</c>, or <c>"auto"</c>/null — start at the
    /// short lifetime and move to the hour once this instance has actually paused for longer than
    /// <see cref="CacheTtlPromotionGap"/>. The move happens on a request that was going to write anyway, so promoting never pays
    /// for an extra write, and it is never reversed mid-session (reversing it would force one).
    /// </summary>
    /// <remarks>
    /// Why a pause is the signal: an hour-long write costs twice the input price against the short one's 1.25×, and a short-lived
    /// entry is refreshed by any read. So the hour only ever loses when a session never pauses for five minutes — exactly the
    /// unattended, turn-after-turn shape — and it wins from the first five-to-sixty-minute pause onward.
    /// </remarks>
    public string? CacheTtl { get; init; }

    /// <summary>How long a gap makes <c>"auto"</c> move to the hour; the short lifetime's own expiry, with a little slack.</summary>
    public TimeSpan CacheTtlPromotionGap { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Clock for the cache-lifetime decision only; tests move it instead of sleeping.</summary>
    public TimeProvider CacheTtlClock { get; init; } = TimeProvider.System;
}
