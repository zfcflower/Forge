using Forge.App.Agents;
using Forge.App.Services;
using Forge.App.Skills;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The product's own skills, end to end: the embedded resources materialized to a version directory, discovered from it
/// as the fourth locale, and the orchestration text the root agent is told. A skill folder renamed or a front matter key
/// mistyped disappears from all three without failing anything else, and the route it stood for silently stops existing.
/// </summary>
public sealed class OrchestrationSkillsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-builtin-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
        }
    }

    private IReadOnlyList<SlashEntry> DiscoverBuiltIn()
    {
        var directory = BuiltInSkills.Materialize(root);
        Assert.NotNull(directory);
        return SkillCatalog.Discover(workspace: null, userDirectory: null, productDirectory: directory);
    }

    [Fact]
    public void The_product_ships_every_skill_the_default_orchestration_names()
    {
        var skills = DiscoverBuiltIn();

        Assert.Equal(
            ["brainstorming", "choosing-a-workflow", "hardening-tests", "shipping-a-pr", "todos"],
            skills.Select(skill => skill.Name));
        Assert.Equal(
            ["brainstorming", "hardening-tests", "shipping-a-pr"],
            skills.Where(skill => skill.IsWorkflow).Select(skill => skill.Name));
        Assert.All(skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Description)));
    }

    [Fact]
    public void The_routing_text_sends_work_on_the_tests_to_hardening_tests()
    {
        var text = OrchestrationPrompt.Build(DiscoverBuiltIn());

        Assert.Contains("goes through hardening-tests", text, StringComparison.Ordinal);
        Assert.Contains("- hardening-tests — ", text, StringComparison.Ordinal);
        Assert.Contains("goes through brainstorming first", text, StringComparison.Ordinal);
        Assert.Contains("goes through shipping-a-pr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_router_skill_offers_the_test_hardening_route()
    {
        var router = DiscoverBuiltIn().Single(skill => skill.Name == "choosing-a-workflow");

        var text = File.ReadAllText(router.Path);

        Assert.Contains("| `hardening-tests` |", text, StringComparison.Ordinal);
        Assert.Contains("`skill {\"name\": \"hardening-tests\"}`", text, StringComparison.Ordinal);
    }
}
