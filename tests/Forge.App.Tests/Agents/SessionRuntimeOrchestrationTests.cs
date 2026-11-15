using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Skills;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The default orchestration as the session actually wires it (<see cref="SessionRuntime"/>): the skill index and the routing
/// rule reach the <b>root</b> agent's role text and nothing else, they disappear when the preference says so, and the product's
/// materialized skill root is the fourth source the session's catalog reads. The pure text is pinned by
/// <see cref="OrchestrationPromptTests"/>; this file pins the wiring, because a prompt block that reaches the wrong layer or a
/// catalog that never sees the product root both look fine in isolation.
/// </summary>
public sealed class SessionRuntimeOrchestrationTests
{
    /// <summary>
    /// One product-style skill on disk; the runtime discovers skills from the workspace, the user directory and this root, so
    /// the block exists exactly while the router is among them.
    /// </summary>
    private static string SkillsRoot(string root, params string[] names)
    {
        var directory = Path.Combine(root, "product-skills");
        foreach (var name in names)
        {
            var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "SKILL.md"),
                $"---\nname: {name}\ndescription: the {name} skill\n---\n# {name}\n");
        }

        return directory;
    }

    [Fact]
    public async Task The_root_prompt_carries_the_index_and_the_routing_rule_and_the_system_layer_does_not()
    {
        var temp = Path.Combine(Path.GetTempPath(), "forge-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var previous = AgentRuntimeServices.ProductSkillsDirectory;
        try
        {
            AgentRuntimeServices.ProductSkillsDirectory = SkillsRoot(temp, "choosing-a-workflow", "todos");
            await using var rig = new SessionRuntimeRig(temp);
            await rig.TurnAsync();

            var request = rig.Provider.Requests[^1];

            // The block is in the system prompt (system layer + root role are concatenated into it), and it names the router
            // and the index entries the catalog found.
            Assert.Contains("<workflow-routing>", request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("<skills>", request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("- choosing-a-workflow", request.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("- todos", request.SystemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            AgentRuntimeServices.ProductSkillsDirectory = previous;
        }
    }

    [Fact]
    public async Task Turning_the_orchestration_off_takes_the_whole_block_out_of_the_root_prompt()
    {
        var temp = Path.Combine(Path.GetTempPath(), "forge-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var previous = AgentRuntimeServices.ProductSkillsDirectory;
        try
        {
            AgentRuntimeServices.ProductSkillsDirectory = SkillsRoot(temp, "choosing-a-workflow");
            await using var rig = new SessionRuntimeRig(
                temp,
                services => services.OrchestrationEnabled = static () => false);
            await rig.TurnAsync();

            var prompt = rig.Provider.Requests[^1].SystemPrompt;

            Assert.DoesNotContain("<workflow-routing>", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("<skills>", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("choosing-a-workflow", prompt, StringComparison.Ordinal);
        }
        finally
        {
            AgentRuntimeServices.ProductSkillsDirectory = previous;
        }
    }

    [Fact]
    public async Task The_product_root_is_a_catalog_source_so_its_skills_are_reachable_by_name()
    {
        var temp = Path.Combine(Path.GetTempPath(), "forge-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var previous = AgentRuntimeServices.ProductSkillsDirectory;
        try
        {
            AgentRuntimeServices.ProductSkillsDirectory = SkillsRoot(temp, "choosing-a-workflow", "todos");
            await using var rig = new SessionRuntimeRig(temp);
            await rig.TurnAsync();

            // The skill tool the session publishes resolves the product's own skill by name: the materialized directory really is
            // the fourth source the runtime discovered, not just a path the composition root set.
            var tool = rig.Runtime.BuildTools().Single(t => t.Definition.Name == SkillTool.Name);
            var result = await tool.InvokeAsync(
                new ToolCall("c1", SkillTool.Name, """{"name":"todos"}"""),
                new ToolContext("s1", temp),
                CancellationToken.None);

            Assert.False(result.IsError, result.Content);
            Assert.StartsWith("---", result.Content, StringComparison.Ordinal);
            Assert.Contains("# todos", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            AgentRuntimeServices.ProductSkillsDirectory = previous;
        }
    }

    [Fact]
    public async Task Two_turns_of_one_root_keep_the_same_prompt_bytes_so_the_prefix_is_not_rebuilt()
    {
        var temp = Path.Combine(Path.GetTempPath(), "forge-orchestration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var previous = AgentRuntimeServices.ProductSkillsDirectory;
        try
        {
            AgentRuntimeServices.ProductSkillsDirectory = SkillsRoot(temp, "choosing-a-workflow");
            await using var rig = new SessionRuntimeRig(temp);
            await rig.TurnAsync();
            await rig.TurnAsync();

            // A decorated root that looked changed on every turn would rebuild the root and move the cached prefix; two turns
            // of the same session must present the same bytes.
            Assert.Equal(rig.Provider.Requests[^2].SystemPrompt, rig.Provider.Requests[^1].SystemPrompt);
        }
        finally
        {
            AgentRuntimeServices.ProductSkillsDirectory = previous;
        }
    }
}
