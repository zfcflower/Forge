using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The orchestration prompt injection: the skill index plus the routing rule, built only when the router skill is
/// among the discovered ones, byte-stable for the same catalog (it feeds the cached prompt prefix), and offered only to
/// agent kinds that can actually load skills.
/// </summary>
public sealed class OrchestrationPromptTests
{
    private static SlashEntry Skill(string name, string description) =>
        new(name, description, IsSkill: true, Path.Combine("skills", name, "SKILL.md"));

    [Fact]
    public void No_skills_means_no_injection()
    {
        Assert.Equal(string.Empty, OrchestrationPrompt.Build([]));
    }

    [Fact]
    public void Skills_without_the_router_mean_no_injection()
    {
        var skills = new SlashEntry[]
        {
            Skill("brainstorming", "interview before building"),
            Skill("shipping-a-pr", "deliver as a pull request"),
        };

        Assert.Equal(string.Empty, OrchestrationPrompt.Build(skills));
    }

    [Fact]
    public void The_index_names_every_skill_and_the_routing_block_names_the_router()
    {
        var skills = new SlashEntry[]
        {
            Skill("brainstorming", "interview before building"),
            Skill("choosing-a-workflow", "route new work"),
            Skill("shipping-a-pr", "deliver as a pull request"),
            new("code-review", "a template, not a skill", IsSkill: false),
        };

        var text = OrchestrationPrompt.Build(skills);

        Assert.Contains("<skills>", text, StringComparison.Ordinal);
        Assert.Contains("</skills>", text, StringComparison.Ordinal);
        Assert.Contains("<workflow-routing>", text, StringComparison.Ordinal);
        Assert.Contains("</workflow-routing>", text, StringComparison.Ordinal);
        Assert.Contains("- brainstorming — interview before building", text, StringComparison.Ordinal);
        Assert.Contains("- choosing-a-workflow — route new work", text, StringComparison.Ordinal);
        Assert.Contains("- shipping-a-pr — deliver as a pull request", text, StringComparison.Ordinal);
        Assert.DoesNotContain("code-review", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_catalog_builds_the_same_bytes_twice()
    {
        var skills = new SlashEntry[]
        {
            Skill("brainstorming", "interview before building"),
            Skill("choosing-a-workflow", "route new work"),
        };

        Assert.Equal(OrchestrationPrompt.Build(skills), OrchestrationPrompt.Build(skills));
    }

    [Theory]
    [InlineData(null, true)]         // no definition at all: the default agent lists every tool
    [InlineData("", true)]           // a definition whose ToolNames is null (no whitelist)
    [InlineData("read,skill,edit", true)]
    [InlineData("read,edit", false)]
    public void Only_agent_kinds_whose_whitelist_has_the_skill_tool_are_told_about_skills(string? toolNames, bool expected)
    {
        AgentDefinition? definition = toolNames switch
        {
            null => null,
            "" => new AgentDefinition("Worker", "a worker", "worker prompt", new ModelSettings("worker-model")),
            _ => new AgentDefinition("Worker", "a worker", "worker prompt", new ModelSettings("worker-model"), toolNames.Split(',')),
        };

        Assert.Equal(expected, OrchestrationPrompt.CanLoadSkills(definition));
    }
}
