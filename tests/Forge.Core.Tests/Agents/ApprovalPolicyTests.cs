using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public class ApprovalPolicyTests
{
    private static string Workspace => Path.GetFullPath(Path.GetTempPath());

    [Fact]
    public void ReadOnly_tools_are_allowed_by_default()
    {
        var policy = new DefaultApprovalPolicy();

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(new ApprovalRequest("read", "{}", ToolSideEffect.ReadOnly, Workspace)));
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(new ApprovalRequest("bash", "{}", ToolSideEffect.Execute, Workspace)));
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(new ApprovalRequest("edit", "{}", ToolSideEffect.Write, Workspace)));
    }

    [Fact]
    public void Explicit_rules_win_over_every_other_rule()
    {
        var policy = new DefaultApprovalPolicy
        {
            TrustedDirectories = [Workspace],
            ToolRules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
            {
                ["read"] = ApprovalMode.Deny,
                ["bash"] = ApprovalMode.Allow,
            },
        };

        Assert.Equal(ApprovalMode.Deny, policy.Evaluate(new ApprovalRequest("read", "{}", ToolSideEffect.ReadOnly, Workspace)));
        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(new ApprovalRequest("bash", "{}", ToolSideEffect.Execute, Workspace)));
    }

    [Fact]
    public void Trusted_directories_allow_side_effects_inside_them_only()
    {
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var inside = Path.Combine(Workspace, "src");
        var outside = Path.GetFullPath(Path.Combine(Workspace, ".."));

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(new ApprovalRequest("edit", "{}", ToolSideEffect.Write, inside)));
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(new ApprovalRequest("edit", "{}", ToolSideEffect.Write, outside)));
    }

    [Fact]
    public void Directory_comparison_follows_the_platform_case_sensitivity()
    {
        var policy = new DefaultApprovalPolicy { TrustedDirectories = [Workspace] };
        var marker = Path.Combine(Workspace, "CaseSensitiveProbe");
        var flipped = OperatingSystem.IsWindows() ? marker.ToUpperInvariant() : marker.ToLowerInvariant();
        if (string.Equals(marker, flipped, StringComparison.Ordinal))
        {
            return;
        }

        var mode = policy.Evaluate(new ApprovalRequest("edit", "{}", ToolSideEffect.Write, flipped));

        Assert.Equal(OperatingSystem.IsWindows() ? ApprovalMode.Allow : ApprovalMode.Ask, mode);
    }

    [Fact]
    public void AutoAllowReadOnly_can_be_turned_off()
    {
        var policy = new DefaultApprovalPolicy { AutoAllowReadOnly = false };

        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(new ApprovalRequest("read", "{}", ToolSideEffect.ReadOnly, Workspace)));
    }
}

public class TokenCounterTests
{
    [Fact]
    public void Counts_text_and_messages()
    {
        ITokenCounter counter = new ApproximateTokenCounter();

        Assert.Equal(0, counter.Count(string.Empty));
        Assert.Equal(1, counter.Count("abc"));
        Assert.True(counter.Count(new string('x', 400)) >= 100);

        var messages = new List<ChatMessage>
        {
            ChatMessage.User(new string('x', 400)),
            new(MessageRole.Assistant, [new ToolCallBlock("id", "read", """{"path":"a"}""")]),
        };
        Assert.True(counter.Count(messages) > counter.Count(new[] { messages[0] }));
    }
}
