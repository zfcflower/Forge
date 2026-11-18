namespace Forge.Core.Tools;

/// <summary>
/// What the agent does with a <c>request changes</c> verdict of a step review: fix the findings itself (the auto-fix loop) or
/// hand them to the user, and how many finished non-approving reviews of one step the loop may spend before it must stop and
/// ask the user. Read on every review result, so a settings change applies to the next one.
/// </summary>
/// <param name="AutoFix">True: the agent fixes the findings and asks for another review; false: it reports them and waits.</param>
/// <param name="MaxRounds">Non-approving step reviews allowed before the agent stops and asks the user; at least 1.</param>
public sealed record ReviewFixPolicy(bool AutoFix, int MaxRounds)
{
    /// <summary>Fix automatically, up to three rounds.</summary>
    public static ReviewFixPolicy Default { get; } = new(true, 3);
}
