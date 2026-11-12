using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.Core.Tests.Prompts.Templates;

public class PromptTemplateIdsAndRegistryTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("9lives")]
    [InlineData("a.b")]
    [InlineData("a_b-c.d")]
    [InlineData("compaction.summarizer")]
    [InlineData("console")]
    [InlineData("nullable")]
    [InlineData("com10")]
    public void Valid_ids_are_accepted(string id) => Assert.True(PromptTemplateIds.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Review")]
    [InlineData("reVIEW")]
    [InlineData(".hidden")]
    [InlineData("-dash")]
    [InlineData("_under")]
    [InlineData("trailing.")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../x")]
    [InlineData("a:b")]
    [InlineData("café")]
    [InlineData("con")]
    [InlineData("nul.x")]
    [InlineData("aux")]
    [InlineData("prn.txt")]
    [InlineData("com1")]
    [InlineData("lpt9.md")]
    public void Invalid_ids_are_rejected(string? id) => Assert.False(PromptTemplateIds.IsValid(id));

    [Fact]
    public void Length_limit_is_64_characters()
    {
        Assert.True(PromptTemplateIds.IsValid(new string('a', 64)));
        Assert.False(PromptTemplateIds.IsValid(new string('a', 65)));
        Assert.Equal(64, PromptTemplateIds.MaxLength);
    }

    [Fact]
    public void FromAgentName_lowercases_and_replaces_spaces()
    {
        Assert.Equal("code-reviewer", PromptTemplateIds.FromAgentName("  Code Reviewer "));
        Assert.Equal("scout", PromptTemplateIds.FromAgentName("Scout"));
    }

    [Fact]
    public void Core_registry_lists_agents_then_commands_then_slots_in_registration_order()
    {
        var expected = BuiltInAgents.All.Select(agent => (PromptTemplateKind.Agent, PromptTemplateIds.FromAgentName(agent.Name)))
            .Concat(BuiltInCommands.All.Select(command => (PromptTemplateKind.Command, command.Id)))
            .Concat([
                (PromptTemplateKind.Slot, PromptSlotIds.System),
                (PromptTemplateKind.Slot, PromptSlotIds.CompactionSummarizer),
                (PromptTemplateKind.Slot, PromptSlotIds.HandoverSummary),
            ])
            .ToArray();

        Assert.Equal(expected, PromptTemplateRegistry.Core.All.Select(template => (template.Kind, template.Id)).ToArray());
        Assert.Equal("system", PromptSlotIds.System);
        Assert.Equal("compaction.summarizer", PromptSlotIds.CompactionSummarizer);
        Assert.Equal("handover.summary", PromptSlotIds.HandoverSummary);
    }

    [Fact]
    public void Core_slots_carry_the_compiled_in_texts_and_agents_carry_their_definition()
    {
        var registry = PromptTemplateRegistry.Core;

        Assert.Equal(BuiltInPrompts.System, registry.Find(PromptTemplateKind.Slot, PromptSlotIds.System)!.Body);
        Assert.Equal(SummaryCompactor.SummarizerPrompt, registry.Find(PromptTemplateKind.Slot, PromptSlotIds.CompactionSummarizer)!.Body);
        Assert.Equal(HandoverSummary.Prompt, registry.Find(PromptTemplateKind.Slot, PromptSlotIds.HandoverSummary)!.Body);
        Assert.Same(BuiltInAgents.Scout, registry.Find(PromptTemplateKind.Agent, "scout")!.Agent);
        Assert.Equal(BuiltInAgents.Scout.Prompt, registry.Find(PromptTemplateKind.Agent, "scout")!.Body);
        Assert.All(registry.All.Where(template => template.Kind == PromptTemplateKind.Slot), slot => Assert.Empty(slot.Placeholders));
    }

    [Fact]
    public void Lookup_is_per_kind_and_misses_return_null_or_false()
    {
        var registry = PromptTemplateRegistry.Core;

        Assert.True(registry.TryGet(PromptTemplateKind.Command, "review", out var found));
        Assert.Equal("review", found.Id);
        Assert.False(registry.TryGet(PromptTemplateKind.Agent, "review", out var wrongKind));
        Assert.Null(wrongKind);
        Assert.Null(registry.Find(PromptTemplateKind.Command, "Review"));
        Assert.Null(registry.Find(PromptTemplateKind.Slot, "nope"));
    }

    [Fact]
    public void With_appends_after_existing_templates_and_leaves_the_original_untouched()
    {
        var before = PromptTemplateRegistry.Core.All.Count;
        var extra = PromptTemplateRegistry.Slot("pr.workflow", "PR", "desc", "Hello {pr}", "pr");

        var extended = PromptTemplateRegistry.Core.With([extra]);

        Assert.Equal(before + 1, extended.All.Count);
        Assert.Same(extra, extended.All[^1]);
        Assert.Equal(PromptTemplateKind.Slot, extra.Kind);
        Assert.Equal(["pr"], extra.Placeholders);
        Assert.Equal(before, PromptTemplateRegistry.Core.All.Count);
        Assert.Null(PromptTemplateRegistry.Core.Find(PromptTemplateKind.Slot, "pr.workflow"));
        Assert.Same(extra, extended.Find(PromptTemplateKind.Slot, "pr.workflow"));
    }

    [Fact]
    public void With_throws_on_a_duplicate_id_of_the_same_kind()
    {
        var duplicate = PromptTemplateRegistry.Slot(PromptSlotIds.System, "x", "x", "x");
        Assert.Throws<ArgumentException>(() => PromptTemplateRegistry.Core.With([duplicate]));

        var first = PromptTemplateRegistry.Slot("one", "x", "x", "x");
        var second = PromptTemplateRegistry.Slot("one", "y", "y", "y");
        Assert.Throws<ArgumentException>(() => PromptTemplateRegistry.Core.With([first, second]));
    }

    [Fact]
    public void With_allows_the_same_id_under_a_different_kind()
    {
        var command = new BuiltInPromptTemplate(PromptTemplateKind.Command, "scout", "scout", "d", "body", []);

        var extended = PromptTemplateRegistry.Core.With([command]);

        Assert.Equal(PromptTemplateKind.Command, extended.Find(PromptTemplateKind.Command, "scout")!.Kind);
        Assert.NotNull(extended.Find(PromptTemplateKind.Agent, "scout"));
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("")]
    [InlineData("con")]
    [InlineData("a/b")]
    public void With_throws_on_an_invalid_id(string id)
    {
        var template = new BuiltInPromptTemplate(PromptTemplateKind.Slot, id, "x", "x", "x", []);
        Assert.Throws<ArgumentException>(() => PromptTemplateRegistry.Core.With([template]));
    }
}
