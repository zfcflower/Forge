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

    /// <summary>Absolute token count that starts compaction; null disables automatic compaction.</summary>
    public int? CompactionThresholdTokens { get; init; }

    /// <summary>Share of the threshold a compaction aims to leave in context, so it does not retrigger immediately.</summary>
    public double CompactionTargetRatio { get; init; } = 0.5;

    /// <summary>Tool output longer than this many characters moves into the artifact store.</summary>
    public int ArtifactThresholdChars { get; init; } = 32_000;

    public int ArtifactPreviewChars { get; init; } = 2_000;

    /// <summary>
    /// Prompt-cache lifetime asked for on the message breakpoint (<see cref="CacheHint.MessageTtl"/>): null or "5m" = the
    /// provider default, "1h" = the one-hour cache. Any other value is treated as the default.
    /// </summary>
    public string? MessageCacheTtl { get; init; }
}
