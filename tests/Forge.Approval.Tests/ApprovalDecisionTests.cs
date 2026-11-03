using Xunit;

namespace Forge.Approval.Tests;

public class ApprovalDecisionTests
{
    [Fact]
    public void Each_factory_sets_its_outcome_and_only_allow_reports_allowed()
    {
        var allowed = ApprovalDecision.Allow("hashes match");
        var required = ApprovalDecision.Require("no record");
        var blocked = ApprovalDecision.Block("disabled");

        Assert.Equal(ApprovalOutcome.Allowed, allowed.Outcome);
        Assert.Equal(ApprovalOutcome.RequiresApproval, required.Outcome);
        Assert.Equal(ApprovalOutcome.Blocked, blocked.Outcome);
        Assert.True(allowed.IsAllowed);
        Assert.False(required.IsAllowed);
        Assert.False(blocked.IsAllowed);
    }

    [Fact]
    public void Factories_keep_the_reason_verbatim()
    {
        Assert.Equal("hashes match", ApprovalDecision.Allow("hashes match").Reason);
    }

    [Fact]
    public void To_string_joins_outcome_and_reason()
    {
        Assert.Equal("Blocked: plugin acme is disabled", ApprovalDecision.Block("plugin acme is disabled").ToString());
    }
}
