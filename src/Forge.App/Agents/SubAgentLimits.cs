namespace Forge.App.Agents;

/// <summary>
/// The two sub-agent dials of the <c>agents</c> settings section: how many agents of one session's spawn tree may run at
/// once, and whether a session may spawn sub-agents at all. Handed to <see cref="SessionRuntime"/> through
/// <see cref="AgentRuntimeServices.SubAgents"/> as a factory, so a value from the live settings reaches a running session
/// at its next turn instead of the next launch.
/// <para>The two are one value rather than two because they meet in the same place: switching spawning off removes
/// <c>spawn_agent</c> from the model's tool set, and the cap only means anything while spawning is on.</para>
/// </summary>
internal readonly record struct SubAgentLimits(int MaxParallel, bool Enabled)
{
    /// <summary>
    /// Smallest cap a host accepts. Zero would mean "never run an agent", which the switch says instead and more clearly,
    /// and a pool with no permit would deadlock the root agent itself.
    /// </summary>
    public const int MinParallel = 1;

    /// <summary>
    /// Largest cap the settings offer. This is a safety bound rather than a measured optimum: every active agent costs a
    /// streaming request and its own tool processes, so a tree wider than this competes with itself for provider quota and
    /// for the machine, and the extra agents finish later than they would in a narrower tree.
    /// </summary>
    public const int MaxParallelCeiling = 16;

    /// <summary>The cap <see cref="Forge.Core.Agents.AgentOptions.MaxParallelSubAgents"/> carried while it was hard-coded.</summary>
    public const int DefaultParallel = 4;

    /// <summary>Spawning allowed at the historical cap: what a runtime assembled without the settings store uses.</summary>
    public static SubAgentLimits Default { get; } = new(DefaultParallel, Enabled: true);

    /// <summary>Clamps a stored or typed cap into [<see cref="MinParallel"/>, <see cref="MaxParallelCeiling"/>].</summary>
    /// <remarks>
    /// The bounds and the default live here and nowhere else: the <c>agents</c> settings section stores a raw integer and hands it
    /// over, the runtime floors a host built from a hand-edited file, and both read these three constants instead of repeating
    /// 1, 16 and 4. A second copy of the range is how the offered choices and the accepted range drift apart.
    /// </remarks>
    public static int Clamp(int value) => Math.Clamp(value, MinParallel, MaxParallelCeiling);
}
