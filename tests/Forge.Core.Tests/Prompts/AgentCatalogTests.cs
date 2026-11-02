using Forge.Abstractions;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class AgentCatalogTests
{
    private static AgentDefinition Agent(string name, string prompt = "prompt") =>
        new(name, "description", prompt, new ModelSettings("default", ThinkingEffort.Low));

    [Fact]
    public void Default_catalog_exposes_the_built_in_agents_in_order()
    {
        var catalog = new AgentCatalog();

        Assert.Equal(["Scout", "Worker", "Plan", "Reviewer"], catalog.All.Select(a => a.Name));
        Assert.Equal(4, catalog.Count);
        Assert.Same(BuiltInAgents.Scout, catalog.Get("Scout"));
        Assert.Equal(ThinkingEffort.Low, catalog.Get("Scout").Model.Thinking);
        Assert.Equal(ThinkingEffort.Medium, catalog.Get("Worker").Model.Thinking);
        Assert.Equal(["read", "grep", "find", "ls", "edit", "write", "bash"], catalog.Get("Worker").ToolNames);
        Assert.DoesNotContain("edit", catalog.Get("Reviewer").ToolNames!);
    }

    [Fact]
    public void Loaded_definition_overrides_built_in_with_the_same_name()
    {
        var custom = Agent("worker", "custom prompt");
        var catalog = new AgentCatalog([custom]);

        Assert.Equal(4, catalog.Count);
        Assert.Same(custom, catalog.Get("Worker"));
        Assert.Equal(["Scout", "worker", "Plan", "Reviewer"], catalog.All.Select(a => a.Name));
    }

    [Fact]
    public void Lookups_are_case_insensitive()
    {
        var catalog = new AgentCatalog();

        Assert.True(catalog.TryGet("scOUT", out var scout));
        Assert.NotNull(scout);
        Assert.Equal("Scout", scout.Name);
        Assert.NotNull(catalog.Find("REVIEWER"));
        Assert.Equal("Plan", catalog.Get("plan").Name);
        Assert.Null(catalog.Find("missing"));
        Assert.False(catalog.TryGet("missing", out _));
    }

    [Fact]
    public void Get_throws_for_unknown_name() =>
        Assert.Throws<KeyNotFoundException>(() => new AgentCatalog().Get("missing"));

    [Fact]
    public void Unknown_definitions_are_appended_in_input_order()
    {
        var catalog = new AgentCatalog([Agent("Beta"), Agent("alpha")]);

        Assert.Equal(["Scout", "Worker", "Plan", "Reviewer", "Beta", "alpha"], catalog.All.Select(a => a.Name));
        Assert.Equal(6, catalog.Count);
    }

    [Fact]
    public void Duplicate_names_collapse_to_the_last_definition()
    {
        var second = Agent("Dup", "second");
        var catalog = new AgentCatalog([Agent("Dup", "first"), second, Agent("dup", "third")]);

        Assert.Equal(5, catalog.Count);
        Assert.Equal(["Scout", "Worker", "Plan", "Reviewer", "dup"], catalog.All.Select(a => a.Name));
        Assert.Equal("third", catalog.Get("DUP").Prompt);
        Assert.Equal(["Scout", "Worker", "Plan", "Reviewer"], catalog.All.Take(4).Select(a => a.Name));
    }

    [Fact]
    public void With_layers_definitions_on_top_of_the_current_catalog()
    {
        var first = new AgentCatalog([Agent("Custom", "one")]);
        var second = first.With([Agent("custom", "two"), Agent("Extra")]);

        Assert.Equal(5, first.Count);
        Assert.Equal("one", first.Get("Custom").Prompt);
        Assert.Equal("two", second.Get("custom").Prompt);
        Assert.Equal(6, second.Count);
        Assert.NotNull(second.Find("Extra"));
    }

    [Fact]
    public void Definitions_without_a_name_are_ignored()
    {
        var catalog = new AgentCatalog([Agent(" "), Agent("Named")]);

        Assert.Equal(5, catalog.Count);
        Assert.False(catalog.TryGet(" ", out _));
        Assert.NotNull(catalog.Find("Named"));
    }

    [Fact]
    public void Null_definitions_yield_the_built_ins() =>
        Assert.Equal(4, new AgentCatalog(null).Count);
}
