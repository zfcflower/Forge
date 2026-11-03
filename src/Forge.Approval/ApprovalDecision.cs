namespace Forge.Approval;

/// <summary>What a <see cref="ApprovalDecision"/> permits.</summary>
public enum ApprovalOutcome
{
    /// <summary>The recorded approval covers the current content exactly; the plugin may run.</summary>
    Allowed,

    /// <summary>The plugin must not run until the user approves the current content.</summary>
    RequiresApproval,

    /// <summary>The plugin must not run at all in its current state; re-approval does not lift this.</summary>
    Blocked,
}

/// <summary>Result of <see cref="ApprovalEngine.Evaluate(PluginRecord, ApprovalStore)"/> plus a stable reason string.</summary>
public sealed record ApprovalDecision(ApprovalOutcome Outcome, string Reason)
{
    public bool IsAllowed => Outcome == ApprovalOutcome.Allowed;

    public static ApprovalDecision Allow(string reason) => new(ApprovalOutcome.Allowed, reason);

    public static ApprovalDecision Require(string reason) => new(ApprovalOutcome.RequiresApproval, reason);

    public static ApprovalDecision Block(string reason) => new(ApprovalOutcome.Blocked, reason);

    public override string ToString() => $"{Outcome}: {Reason}";
}
