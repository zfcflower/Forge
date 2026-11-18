using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests;

public sealed class SkillWorkflowFlagTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-skills-" + Guid.NewGuid().ToString("n"));

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

    private void Skill(string scopeFolder, string name, string frontMatter)
    {
        var folder = Path.Combine(root, scopeFolder, "skills", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\n{frontMatter}description: d\n---\nbody\n");
    }

    [Theory]
    [InlineData("---\nname: a\nworkflow: true\n---\n", true)]
    [InlineData("---\r\nname: a\r\nworkflow: \"True\"\r\n---\r\n", true)]
    [InlineData("---\nname: a\nworkflow: false\n---\n", false)]
    [InlineData("---\nname: a\n---\nworkflow: true\n", false)]
    [InlineData("---\nname: a\ndescription: >-\n  workflow: true\n---\n", false)]
    [InlineData("workflow: true\n", false)]
    public void Only_a_front_matter_key_marks_a_workflow(string text, bool expected) =>
        Assert.Equal(expected, SkillCatalog.HasWorkflowFlag(text));

    [Fact]
    public void Workflow_skills_of_the_user_and_the_workspace_are_discovered_with_the_flag()
    {
        Skill("user", "hotfix", "workflow: true\n");
        Skill("user", "notes", string.Empty);
        var workspace = Path.Combine(root, "ws");
        var folder = Path.Combine(workspace, ".forge", "skills", "release");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), "---\nname: release\nworkflow: true\ndescription: d\n---\n");

        var found = SkillCatalog.Discover(workspace, Path.Combine(root, "user"));

        Assert.True(found.Single(e => e.Name == "hotfix").IsWorkflow);
        Assert.True(found.Single(e => e.Name == "release").IsWorkflow);
        Assert.False(found.Single(e => e.Name == "notes").IsWorkflow);
    }
}
