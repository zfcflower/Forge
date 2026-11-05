using Forge.Abstractions;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class LayerSourcesTests
{
    [Fact]
    public void Load_reads_global_and_project_layers()
    {
        using var user = new TempDirectory();
        using var workspace = new TempDirectory();
        user.Write("AGENTS.md", "global text");
        workspace.Write("AGENTS.md", "project text");

        var layers = LayerSources.Load(user.Path, workspace.Path);

        Assert.Equal("global text", layers.GlobalAgent);
        Assert.Equal("project text", layers.ProjectAgent);
    }

    [Fact]
    public void Load_falls_back_to_the_forge_directory_for_the_project_layer()
    {
        using var workspace = new TempDirectory();
        workspace.Write(".forge/AGENTS.md", "fallback project text");

        Assert.Equal("fallback project text", LayerSources.LoadProjectAgent(workspace.Path));
    }

    [Fact]
    public void Load_prefers_the_workspace_root_over_the_forge_directory()
    {
        using var workspace = new TempDirectory();
        workspace.Write("AGENTS.md", "root text");
        workspace.Write(".forge/AGENTS.md", "fallback text");

        Assert.Equal("root text", LayerSources.LoadProjectAgent(workspace.Path));
    }

    [Fact]
    public void Missing_or_blank_files_yield_null()
    {
        using var user = new TempDirectory();
        using var workspace = new TempDirectory();
        user.Write("AGENTS.md", "   \n\t\n");
        workspace.Write("AGENTS.md", "\r\n");

        var layers = LayerSources.Load(user.Path, workspace.Path);

        Assert.Null(layers.GlobalAgent);
        Assert.Null(layers.ProjectAgent);
        Assert.Null(LayerSources.LoadGlobalAgent(null));
        Assert.Null(LayerSources.LoadProjectAgent(null));
        Assert.Null(LayerSources.LoadGlobalAgent(System.IO.Path.Combine(workspace.Path, "absent")));
        Assert.Equal(AgentLayerText.Empty, layers);
    }

    [Fact]
    public void Resolve_builds_prompt_layers_with_the_given_system_text()
    {
        using var user = new TempDirectory();
        using var workspace = new TempDirectory();
        user.Write("AGENTS.md", "global text");
        workspace.Write(".forge/AGENTS.md", "project text");

        var layers = LayerSources.Resolve("system text", user.Path, workspace.Path);

        Assert.Equal(new PromptLayers("system text", "global text", "project text"), layers);
    }

    [Fact]
    public async Task LoadAsync_matches_the_synchronous_result()
    {
        using var user = new TempDirectory();
        using var workspace = new TempDirectory();
        user.Write("AGENTS.md", "global text");
        workspace.Write(".forge/AGENTS.md", "project text");

        var sync = LayerSources.Load(user.Path, workspace.Path);
        var async_result = await LayerSources.LoadAsync(user.Path, workspace.Path, CancellationToken.None);
        var resolved = await LayerSources.ResolveAsync("system", user.Path, workspace.Path, CancellationToken.None);

        Assert.Equal(sync, async_result);
        Assert.Equal(new PromptLayers("system", "global text", "project text"), resolved);
    }

    [Fact]
    public void Resolved_layers_compose_with_the_composer()
    {
        using var user = new TempDirectory();
        using var workspace = new TempDirectory();
        user.Write("AGENTS.md", "global text");
        workspace.Write("AGENTS.md", "project text");

        var layers = LayerSources.Resolve("", user.Path, workspace.Path);
        var prompt = new PromptComposer().Compose(new PromptInput(layers, BuiltInAgents.Scout.Prompt, "dynamic"));

        Assert.Contains(PromptComposer.GlobalAgentHeading + "\n\nglobal text", prompt.StablePrefix, StringComparison.Ordinal);
        Assert.Contains(PromptComposer.ProjectAgentHeading + "\n\nproject text", prompt.StablePrefix, StringComparison.Ordinal);
        Assert.EndsWith(PromptSourceText.Lf(BuiltInAgents.Scout.Prompt), prompt.StablePrefix, StringComparison.Ordinal);
        Assert.Equal("dynamic", prompt.DynamicSuffix);
    }
}
