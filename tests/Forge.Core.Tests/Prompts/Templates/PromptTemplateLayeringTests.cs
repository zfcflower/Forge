using Forge.Abstractions;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.Core.Tests.Prompts.Templates;

public class PromptTemplateLayeringTests
{
    private const PromptTemplateKind Command = PromptTemplateKind.Command;
    private const PromptTemplateKind Agent = PromptTemplateKind.Agent;
    private const PromptTemplateKind Slot = PromptTemplateKind.Slot;

    private static string BuiltInBody(BuiltInPromptTemplate template) =>
        template.Body.ReplaceLineEndings("\n").Trim();

    private static AgentDefinition AgentNamed(PromptTemplateSet set, string name) => set.Agents.Single(a => a.Name == name);

    [Fact]
    public void Without_any_file_the_built_ins_resolve_unchanged()
    {
        using var fixture = new TemplateFixture();

        var set = fixture.Resolve();

        Assert.Equal(BuiltInBody(BuiltInCommands.Review), set.FindCommand("review")!.Body);
        Assert.Equal(PromptTemplateScope.BuiltIn, set.FindCommand("review")!.Scope);
        Assert.Equal(BuiltInCommands.Review.ArgumentHint, set.FindCommand("review")!.ArgumentHint);
        Assert.Equal(BuiltInAgents.All.Count, set.Agents.Count);
        Assert.Equal(BuiltInPrompts.System.ReplaceLineEndings("\n").Trim(), set.Slot(PromptSlotIds.System));
        Assert.False(set.IsSlotCustomized(PromptSlotIds.System));
    }

    [Fact]
    public void A_global_replace_shadows_the_built_in_and_a_project_replace_shadows_both()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "review", "---\ndescription: global review\n---\nGlobal body");

        var withGlobal = fixture.Resolve().FindCommand("review")!;
        fixture.Project(Command, "review", "Project body");
        var withProject = fixture.Resolve().FindCommand("review")!;

        Assert.Equal("Global body", withGlobal.Body);
        Assert.Equal("global review", withGlobal.Description);
        Assert.Equal(PromptTemplateScope.Global, withGlobal.Scope);
        Assert.Equal("Project body", withProject.Body);
        Assert.Equal(PromptTemplateScope.Project, withProject.Scope);
        // Composition starts at the project replace file: metadata it leaves out comes from the built-in, not the shadowed global file.
        Assert.Equal(BuiltInCommands.Review.Description, withProject.Description);
        Assert.Equal(BuiltInCommands.Review.ArgumentHint, withProject.ArgumentHint);
    }

    [Fact]
    public void Project_and_global_files_for_different_ids_do_not_interfere()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "only-global", "G");
        fixture.Project(Command, "only-project", "P");

        var set = fixture.Resolve();

        Assert.Equal("G", set.FindCommand("only-global")!.Body);
        Assert.Equal("P", set.FindCommand("only-project")!.Body);
        Assert.Equal(PromptTemplateScope.Project, set.FindCommand("only-project")!.Scope);
    }

    [Fact]
    public void Project_scope_is_not_read_without_a_workspace()
    {
        using var fixture = new TemplateFixture();
        fixture.Project(Command, "proj-only", "P");
        fixture.Global(Command, "glob-only", "G");

        var set = fixture.Store.Resolve(workspace: null);

        Assert.Null(set.FindCommand("proj-only"));
        Assert.Equal("G", set.FindCommand("glob-only")!.Body);
        Assert.DoesNotContain(fixture.Store.List(null), entry => entry.Ref.Scope == PromptTemplateScope.Project);
    }

    [Fact]
    public void An_append_file_follows_the_built_in_body_after_a_blank_line_and_inherits_its_metadata()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "review", "---\nmode: append\n---\nAlso check the changelog.\n");

        var command = fixture.Resolve().FindCommand("review")!;

        Assert.Equal(BuiltInBody(BuiltInCommands.Review) + "\n\nAlso check the changelog.", command.Body);
        Assert.Equal(BuiltInCommands.Review.Description, command.Description);
        Assert.Equal(BuiltInCommands.Review.ArgumentHint, command.ArgumentHint);
        Assert.Equal(PromptTemplateScope.Global, command.Scope);
    }

    [Fact]
    public void Append_chains_keep_body_order_built_in_then_global_then_project()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "---\nmode: append\ndescription: global desc\n---\nGLOBAL");
        fixture.Project(Command, "fix", "---\nmode: append\nargument-hint: <proj hint>\n---\nPROJECT");

        var command = fixture.Resolve().FindCommand("fix")!;

        Assert.Equal(BuiltInBody(BuiltInCommands.Fix) + "\n\nGLOBAL\n\nPROJECT", command.Body);
        Assert.Equal("global desc", command.Description);
        Assert.Equal("<proj hint>", command.ArgumentHint);
        Assert.Equal(PromptTemplateScope.Project, command.Scope);
    }

    [Fact]
    public void A_project_replace_above_a_global_append_discards_everything_below_it()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "---\nmode: append\n---\nGLOBAL");
        fixture.Project(Command, "fix", "---\nmode: replace\n---\nPROJECT ONLY");

        Assert.Equal("PROJECT ONLY", fixture.Resolve().FindCommand("fix")!.Body);
    }

    [Fact]
    public void A_project_append_after_a_global_replace_extends_the_global_body_not_the_built_in()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "GLOBAL REPLACE");
        fixture.Project(Command, "fix", "---\nmode: append\n---\nPROJECT");

        var body = fixture.Resolve().FindCommand("fix")!.Body;

        Assert.Equal("GLOBAL REPLACE\n\nPROJECT", body);
        Assert.DoesNotContain(BuiltInBody(BuiltInCommands.Fix), body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_append_file_with_a_user_only_id_and_no_lower_file_is_just_its_body()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "mine", "---\nmode: append\n---\nOnly this");

        Assert.Equal("Only this", fixture.Resolve().FindCommand("mine")!.Body);
    }

    [Fact]
    public void An_append_file_with_nothing_below_the_frontmatter_is_an_empty_body_and_ignored()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "review", "---\nmode: append\ndescription: ignored\n---\n\n  \n");

        var set = fixture.Resolve();
        var entry = fixture.Entries(Command, "review").Single(e => e.Ref.Scope == PromptTemplateScope.Global);

        Assert.Equal(BuiltInBody(BuiltInCommands.Review), set.FindCommand("review")!.Body);
        Assert.Equal(BuiltInCommands.Review.Description, set.FindCommand("review")!.Description);
        Assert.Equal([PromptTemplateProblemCode.EmptyBody], entry.Problems.Select(p => p.Code).ToArray());
        Assert.Equal(PromptTemplateMode.Append, entry.Mode);
    }

    [Fact]
    public void An_invalid_higher_file_is_ignored_by_resolve_but_listed_with_its_problems()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "GLOBAL");
        fixture.Project(Command, "fix", "---\nbogus: 1\n---\nPROJECT");

        var set = fixture.Resolve();
        var entries = fixture.Entries(Command, "fix").ToList();

        Assert.Equal("GLOBAL", set.FindCommand("fix")!.Body);
        Assert.Equal(PromptTemplateScope.Global, set.FindCommand("fix")!.Scope);
        Assert.Equal(
            [PromptTemplateScope.BuiltIn, PromptTemplateScope.Global, PromptTemplateScope.Project],
            entries.Select(e => e.Ref.Scope).ToArray());
        var project = entries[2];
        Assert.False(project.IsValid);
        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, "bogus")], project.Problems.ToArray());
        // The invalid file shadows nothing and is not shadowed itself; the valid global one still wins.
        Assert.True(entries[0].IsShadowed);
        Assert.False(entries[1].IsShadowed);
        Assert.False(project.IsShadowed);
    }

    [Fact]
    public void An_invalid_replace_file_over_an_append_chain_leaves_the_append_intact()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "---\nmode: append\n---\nGLOBAL");
        fixture.Project(Command, "fix", "---\nthinking: low\n---\nPROJECT");

        var body = fixture.Resolve().FindCommand("fix")!.Body;

        Assert.Equal(BuiltInBody(BuiltInCommands.Fix) + "\n\nGLOBAL", body);
    }

    [Fact]
    public void Shadowed_and_has_built_in_flags_follow_the_replace_chain()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "G replace");
        fixture.Project(Command, "fix", "---\nmode: append\n---\nP append");
        fixture.Global(Command, "extra", "---\nmode: append\n---\nuser only");

        var fix = fixture.Entries(Command, "fix").ToList();
        var extra = fixture.Entries(Command, "extra").Single();
        var untouched = fixture.Entries(Command, "review").Single();

        Assert.Equal(3, fix.Count);
        Assert.True(fix[0].IsBuiltIn);
        Assert.Equal([true, false, false], fix.Select(e => e.IsShadowed).ToArray());
        Assert.All(fix, e => Assert.True(e.HasBuiltIn));
        Assert.Equal([PromptTemplateMode.Replace, PromptTemplateMode.Replace, PromptTemplateMode.Append], fix.Select(e => e.Mode).ToArray());
        Assert.False(extra.HasBuiltIn);
        Assert.False(extra.IsShadowed);
        Assert.False(extra.IsBuiltIn);
        Assert.True(untouched.HasBuiltIn);
        Assert.False(untouched.IsShadowed);
        Assert.Null(untouched.Path);
        Assert.NotNull(fix[1].Path);
    }

    [Fact]
    public void Listing_orders_kinds_then_built_ins_in_registry_order_then_user_ids_ordinally()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "zeta", "z");
        fixture.Global(Command, "alpha", "a");
        fixture.Global(Command, "mid", "m");
        fixture.Project(Command, "beta", "b");
        fixture.Project(Command, "alpha", "a2");
        fixture.Global(Agent, "zoo", "role");
        fixture.Project(Agent, "ant", "role");

        var entries = fixture.Store.List(fixture.Workspace);

        var commandIds = entries.Where(e => e.Ref.Kind == Command).Select(e => e.Ref.Id).Distinct().ToArray();
        var agentIds = entries.Where(e => e.Ref.Kind == Agent).Select(e => e.Ref.Id).Distinct().ToArray();
        var slotIds = entries.Where(e => e.Ref.Kind == Slot).Select(e => e.Ref.Id).Distinct().ToArray();
        Assert.Equal(
            [.. BuiltInCommands.All.Select(c => c.Id), "alpha", "beta", "mid", "zeta"],
            commandIds);
        Assert.Equal(
            [.. BuiltInAgents.All.Select(a => PromptTemplateIds.FromAgentName(a.Name)), "ant", "zoo"],
            agentIds);
        Assert.Equal([PromptSlotIds.System, PromptSlotIds.CompactionSummarizer, PromptSlotIds.HandoverSummary], slotIds);

        var kinds = entries.Select(e => e.Ref.Kind).ToList();
        Assert.Equal(kinds.OrderBy(k => (int)k).ToList(), kinds);
        // Members of one id are adjacent and ascending in scope.
        var alpha = entries.Where(e => e.Ref.Kind == Command && e.Ref.Id == "alpha").ToList();
        Assert.Equal([PromptTemplateScope.Global, PromptTemplateScope.Project], alpha.Select(e => e.Ref.Scope).ToArray());
        var firstAlpha = entries.ToList().IndexOf(alpha[0]);
        Assert.Same(alpha[1], entries[firstAlpha + 1]);
    }

    [Fact]
    public void Resolved_commands_and_agents_follow_the_same_order()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "zeta", "z");
        fixture.Global(Command, "alpha", "a");
        fixture.Global(Agent, "zoo", "role");
        fixture.Global(Agent, "ant", "role");

        var set = fixture.Resolve();

        Assert.Equal([.. BuiltInCommands.All.Select(c => c.Id), "alpha", "zeta"], set.Commands.Select(c => c.Name).ToArray());
        Assert.Equal([.. BuiltInAgents.All.Select(a => a.Name), "ant", "zoo"], set.Agents.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void A_built_in_agent_keeps_its_tools_and_thinking_when_an_append_file_omits_them()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Agent, "scout", "---\nmode: append\n---\nAlso mention test files.");

        var scout = AgentNamed(fixture.Resolve(), "Scout");

        Assert.Equal(BuiltInAgents.Scout.Prompt.ReplaceLineEndings("\n").Trim() + "\n\nAlso mention test files.", scout.Prompt);
        Assert.Equal(BuiltInAgents.Scout.ToolNames, scout.ToolNames);
        Assert.Equal(BuiltInAgents.Scout.Model, scout.Model);
        Assert.Equal(BuiltInAgents.Scout.Description, scout.Description);
    }

    [Fact]
    public void A_replace_file_for_a_built_in_agent_also_keeps_the_built_in_metadata_it_does_not_set()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Agent, "scout", "Fully new role.");

        var scout = AgentNamed(fixture.Resolve(), "Scout");

        Assert.Equal("Fully new role.", scout.Prompt);
        Assert.Equal(["read", "grep", "find", "ls"], scout.ToolNames);
        Assert.Equal(ThinkingEffort.Low, scout.Model.Thinking);
    }

    [Fact]
    public void Agent_metadata_set_in_a_file_overrides_and_the_last_file_wins()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Agent, "scout", "---\nthinking: high\ntools: [read]\nmodel: m1\n---\nrole");
        fixture.Project(Agent, "scout", "---\nmode: append\nthinking: max\nname: Scout\n---\nmore");

        var scout = AgentNamed(fixture.Resolve(), "Scout");

        Assert.Equal(new ModelSettings("m1", ThinkingEffort.Max), scout.Model);
        Assert.Equal(["read"], scout.ToolNames);
        Assert.Equal("role\n\nmore", scout.Prompt);
    }

    [Fact]
    public void A_slot_override_replaces_or_appends_and_reports_customized()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Slot, PromptSlotIds.System, "Custom system.");
        fixture.Global(Slot, PromptSlotIds.CompactionSummarizer, "---\nmode: append\n---\nBe terse.");

        var set = fixture.Resolve();

        Assert.Equal("Custom system.", set.Slot(PromptSlotIds.System));
        Assert.True(set.IsSlotCustomized(PromptSlotIds.System));
        Assert.Equal(
            Forge.Core.Agents.SummaryCompactor.SummarizerPrompt.ReplaceLineEndings("\n").Trim() + "\n\nBe terse.",
            set.Slot(PromptSlotIds.CompactionSummarizer));
        Assert.False(set.IsSlotCustomized(PromptSlotIds.HandoverSummary));
        Assert.False(set.IsSlotCustomized("nope"));
    }

    [Fact]
    public void A_slot_file_for_an_unknown_slot_never_creates_the_slot()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Slot, "invented", "Body");

        var set = fixture.Resolve();
        var entry = fixture.Entries(Slot, "invented").Single();

        Assert.Null(set.FindSlot("invented"));
        Assert.Equal([PromptTemplateProblemCode.UnknownSlot], entry.Problems.Select(p => p.Code).ToArray());
        Assert.False(entry.HasBuiltIn);
    }

    [Fact]
    public void Files_not_ending_in_md_and_subdirectories_are_ignored()
    {
        using var fixture = new TemplateFixture();
        var directory = Path.Combine(fixture.User, "commands");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "deep.md"), "deep");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "txt");
        File.WriteAllText(Path.Combine(directory, "real.md.tmp"), "tmp");
        fixture.Global(Command, "real", "ok");

        var ids = fixture.Store.List(null).Where(e => e.Ref.Kind == Command && !e.IsBuiltIn).Select(e => e.Ref.Id).ToArray();

        Assert.Equal(["real"], ids);
    }

    [Fact]
    public void A_store_without_a_user_directory_has_no_global_scope()
    {
        var store = new PromptTemplateStore(PromptTemplateRegistry.Core, "  ");

        Assert.Null(store.UserDirectory);
        Assert.Null(store.DirectoryFor(Command, PromptTemplateScope.Global, null));
        Assert.All(store.List(null), e => Assert.True(e.IsBuiltIn));
    }

    [Fact]
    public void Resolve_of_one_snapshot_does_not_change_when_files_change_afterwards()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(Command, "fix", "BEFORE");
        var snapshot = fixture.Resolve();

        fixture.Global(Command, "fix", "AFTER");

        Assert.Equal("BEFORE", snapshot.FindCommand("fix")!.Body);
        Assert.Equal("AFTER", fixture.Resolve().FindCommand("fix")!.Body);
    }
}
