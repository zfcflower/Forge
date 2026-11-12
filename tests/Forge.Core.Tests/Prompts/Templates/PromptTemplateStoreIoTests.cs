using Forge.Abstractions;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.Core.Tests.Prompts.Templates;

public class PromptTemplateStoreIoTests
{
    private static PromptTemplateRef Global(PromptTemplateKind kind, string id) => new(kind, PromptTemplateScope.Global, id);

    private static PromptTemplateRef Project(PromptTemplateKind kind, string id) => new(kind, PromptTemplateScope.Project, id);

    private static PromptTemplateRef BuiltIn(PromptTemplateKind kind, string id) => new(kind, PromptTemplateScope.BuiltIn, id);

    private const string Review = "review";

    [Fact]
    public void Read_returns_null_when_the_scope_has_no_such_file()
    {
        using var fixture = new TemplateFixture();

        Assert.Null(fixture.Store.Read(Global(PromptTemplateKind.Command, "nothing"), fixture.Workspace));
        Assert.Null(fixture.Store.Read(Project(PromptTemplateKind.Command, "nothing"), fixture.Workspace));
        Assert.Null(fixture.Store.Read(Project(PromptTemplateKind.Command, "nothing"), null));
        Assert.Null(fixture.Store.Read(Global(PromptTemplateKind.Command, "../escape"), fixture.Workspace));
        Assert.Null(fixture.Store.Read(BuiltIn(PromptTemplateKind.Command, "nothing"), fixture.Workspace));
    }

    [Fact]
    public void Read_of_a_file_returns_its_text_path_and_a_revision_that_a_save_accepts()
    {
        using var fixture = new TemplateFixture();
        var path = fixture.Global(PromptTemplateKind.Command, "mine", "---\ndescription: d\n---\nBody\n");

        var text = fixture.Store.Read(Global(PromptTemplateKind.Command, "mine"), null)!;

        Assert.Equal("---\ndescription: d\n---\nBody\n", text.Text);
        Assert.Equal(path, text.Path);
        Assert.False(string.IsNullOrEmpty(text.Revision));
        var saved = fixture.Store.Save(text.Ref, null, "Changed", text.Revision);
        Assert.Equal(PromptTemplateSaveStatus.Saved, saved.Status);
        Assert.NotEqual(text.Revision, saved.Revision);
    }

    [Fact]
    public void Read_of_a_built_in_has_no_revision_and_no_path()
    {
        using var fixture = new TemplateFixture();

        var text = fixture.Store.Read(BuiltIn(PromptTemplateKind.Command, Review), null)!;

        Assert.Null(text.Revision);
        Assert.Null(text.Path);
        Assert.Equal(PromptTemplateStore.BuiltInText(BuiltInCommands.Review), text.Text);
        Assert.StartsWith("---\ndescription: ", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_reads_the_project_scope_from_the_workspace_forge_directory()
    {
        using var fixture = new TemplateFixture();
        var path = fixture.Project(PromptTemplateKind.Agent, "ant", "role");

        var text = fixture.Store.Read(Project(PromptTemplateKind.Agent, "ant"), fixture.Workspace)!;

        Assert.Equal(path, text.Path);
        Assert.Equal(Path.Combine(fixture.Workspace, ".forge", "agents", "ant.md"), text.Path);
        Assert.Equal(Path.Combine(fixture.User, "prompts"), fixture.Store.DirectoryFor(PromptTemplateKind.Slot, PromptTemplateScope.Global, null));
    }

    [Fact]
    public void Save_with_a_null_revision_creates_the_file_and_creates_missing_directories()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "fresh");

        var result = fixture.Store.Save(reference, null, "---\ndescription: d\n---\nBody", null);

        Assert.Equal(PromptTemplateSaveStatus.Saved, result.Status);
        Assert.Empty(result.Problems);
        var path = Path.Combine(fixture.User, "commands", "fresh.md");
        Assert.Equal("---\ndescription: d\n---\nBody", File.ReadAllText(path));
        Assert.Equal(fixture.Store.Read(reference, null)!.Revision, result.Revision);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void Saving_a_non_ascii_text_round_trips_as_utf8_without_a_bom()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "uni");

        fixture.Store.Save(reference, null, "café → ok", null);

        var bytes = File.ReadAllBytes(Path.Combine(fixture.User, "commands", "uni.md"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("café → ok", fixture.Store.Read(reference, null)!.Text);
    }

    [Fact]
    public void Save_to_the_project_scope_writes_under_the_workspace()
    {
        using var fixture = new TemplateFixture();

        var result = fixture.Store.Save(Project(PromptTemplateKind.Slot, PromptSlotIds.System), fixture.Workspace, "Mine", null);

        Assert.Equal(PromptTemplateSaveStatus.Saved, result.Status);
        Assert.True(File.Exists(Path.Combine(fixture.Workspace, ".forge", "prompts", "system.md")));
        Assert.Equal("Mine", fixture.Resolve().Slot(PromptSlotIds.System));
    }

    [Fact]
    public void Creating_over_an_existing_file_is_a_conflict_and_leaves_it_untouched()
    {
        using var fixture = new TemplateFixture();
        var path = fixture.Global(PromptTemplateKind.Command, "taken", "original");

        var result = fixture.Store.Save(Global(PromptTemplateKind.Command, "taken"), null, "overwrite", null);

        Assert.Equal(PromptTemplateSaveStatus.Conflict, result.Status);
        Assert.Null(result.Revision);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void A_stale_revision_is_a_conflict_and_the_fresh_one_saves()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "doc");
        fixture.Store.Save(reference, null, "v1", null);
        var read = fixture.Store.Read(reference, null)!;
        File.WriteAllText(read.Path!, "changed outside");

        var stale = fixture.Store.Save(reference, null, "mine", read.Revision);
        var fresh = fixture.Store.Save(reference, null, "mine", fixture.Store.Read(reference, null)!.Revision);

        Assert.Equal(PromptTemplateSaveStatus.Conflict, stale.Status);
        Assert.Equal(PromptTemplateSaveStatus.Saved, fresh.Status);
        Assert.Equal("mine", File.ReadAllText(read.Path!));
    }

    [Fact]
    public void A_stale_revision_save_does_not_write_even_when_the_text_differs()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "doc");
        var path = fixture.Global(PromptTemplateKind.Command, "doc", "theirs");

        var result = fixture.Store.Save(reference, null, "mine", "0123456789abcdef01234567");

        Assert.Equal(PromptTemplateSaveStatus.Conflict, result.Status);
        Assert.Equal("theirs", File.ReadAllText(path));
    }

    [Fact]
    public void Saving_with_a_revision_when_the_file_no_longer_exists_is_a_conflict()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "gone");
        fixture.Store.Save(reference, null, "v1", null);
        var revision = fixture.Store.Read(reference, null)!.Revision;
        File.Delete(Path.Combine(fixture.User, "commands", "gone.md"));

        var result = fixture.Store.Save(reference, null, "v2", revision);

        Assert.Equal(PromptTemplateSaveStatus.Conflict, result.Status);
        Assert.False(File.Exists(Path.Combine(fixture.User, "commands", "gone.md")));
    }

    [Fact]
    public void Saving_identical_text_gives_the_same_revision()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "same");
        var first = fixture.Store.Save(reference, null, "text", null);

        var second = fixture.Store.Save(reference, null, "text", first.Revision);

        Assert.Equal(PromptTemplateSaveStatus.Saved, second.Status);
        Assert.Equal(first.Revision, second.Revision);
    }

    [Fact]
    public void Built_ins_are_read_only()
    {
        using var fixture = new TemplateFixture();

        var save = fixture.Store.Save(BuiltIn(PromptTemplateKind.Command, Review), fixture.Workspace, "x", null);
        var delete = fixture.Store.Delete(BuiltIn(PromptTemplateKind.Command, Review), fixture.Workspace, "rev");

        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, save.Status);
        Assert.Null(save.Revision);
        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, delete);
        Assert.Empty(Directory.GetFiles(fixture.User, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void The_project_scope_without_a_workspace_is_read_only()
    {
        using var fixture = new TemplateFixture();

        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, fixture.Store.Save(Project(PromptTemplateKind.Command, "x"), null, "Body", null).Status);
        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, fixture.Store.Save(Project(PromptTemplateKind.Command, "x"), "  ", "Body", null).Status);
        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, fixture.Store.Delete(Project(PromptTemplateKind.Command, "x"), null, "rev"));
    }

    [Fact]
    public void The_global_scope_without_a_user_directory_is_read_only()
    {
        var store = new PromptTemplateStore(PromptTemplateRegistry.Core, null);

        var save = store.Save(Global(PromptTemplateKind.Command, "x"), null, "Body", null);

        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, save.Status);
        Assert.Equal(PromptTemplateSaveStatus.ReadOnly, store.Delete(Global(PromptTemplateKind.Command, "x"), null, "rev"));
        Assert.Null(store.Read(Global(PromptTemplateKind.Command, "x"), null));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("Upper")]
    [InlineData("con")]
    [InlineData("")]
    public void An_invalid_id_is_rejected_and_nothing_is_written(string id)
    {
        using var fixture = new TemplateFixture();

        var result = fixture.Store.Save(Global(PromptTemplateKind.Command, id), null, "Body", null);

        Assert.Equal(PromptTemplateSaveStatus.Invalid, result.Status);
        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.InvalidId, id)], result.Problems.ToArray());
        Assert.Empty(Directory.GetFiles(fixture.User, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(fixture.User, "escape.md")));
    }

    [Fact]
    public void Invalid_text_is_not_written_and_does_not_replace_an_existing_file()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "doc");
        var path = fixture.Global(PromptTemplateKind.Command, "doc", "good");
        var revision = fixture.Store.Read(reference, null)!.Revision;

        var created = fixture.Store.Save(Global(PromptTemplateKind.Command, "new"), null, "---\nbogus: 1\n---\nBody", null);
        var updated = fixture.Store.Save(reference, null, "   ", revision);

        Assert.Equal(PromptTemplateSaveStatus.Invalid, created.Status);
        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, "bogus")], created.Problems.ToArray());
        Assert.False(File.Exists(Path.Combine(fixture.User, "commands", "new.md")));
        Assert.Equal(PromptTemplateSaveStatus.Invalid, updated.Status);
        Assert.Equal([PromptTemplateProblemCode.EmptyBody], updated.Problems.Select(p => p.Code).ToArray());
        Assert.Equal("good", File.ReadAllText(path));
    }

    [Fact]
    public void An_unknown_slot_cannot_be_saved()
    {
        using var fixture = new TemplateFixture();

        var result = fixture.Store.Save(Global(PromptTemplateKind.Slot, "invented"), null, "Body", null);

        Assert.Equal(PromptTemplateSaveStatus.Invalid, result.Status);
        Assert.Equal([PromptTemplateProblemCode.UnknownSlot], result.Problems.Select(p => p.Code).ToArray());
    }

    [Fact]
    public void Delete_with_the_matching_revision_removes_the_file_and_the_built_in_is_restored()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, Review);
        fixture.Store.Save(reference, null, "Override body", null);
        Assert.Equal("Override body", fixture.Resolve().FindCommand(Review)!.Body);
        var revision = fixture.Store.Read(reference, null)!.Revision!;

        var status = fixture.Store.Delete(reference, null, revision);

        Assert.Equal(PromptTemplateSaveStatus.Saved, status);
        Assert.Null(fixture.Store.Read(reference, null));
        var restored = fixture.Resolve().FindCommand(Review)!;
        Assert.Equal(PromptTemplateScope.BuiltIn, restored.Scope);
        Assert.Equal(BuiltInCommands.Review.Body.ReplaceLineEndings("\n").Trim(), restored.Body);
    }

    [Fact]
    public void Delete_with_a_stale_revision_is_a_conflict_and_keeps_the_file()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "doc");
        fixture.Store.Save(reference, null, "v1", null);
        var revision = fixture.Store.Read(reference, null)!.Revision!;
        fixture.Global(PromptTemplateKind.Command, "doc", "edited elsewhere");

        var status = fixture.Store.Delete(reference, null, revision);

        Assert.Equal(PromptTemplateSaveStatus.Conflict, status);
        Assert.True(File.Exists(Path.Combine(fixture.User, "commands", "doc.md")));
    }

    [Fact]
    public void Delete_of_a_missing_file_is_a_conflict()
    {
        using var fixture = new TemplateFixture();

        Assert.Equal(PromptTemplateSaveStatus.Conflict, fixture.Store.Delete(Global(PromptTemplateKind.Command, "ghost"), null, "abc"));
    }

    [Fact]
    public void Delete_validates_its_arguments()
    {
        using var fixture = new TemplateFixture();

        Assert.Throws<ArgumentNullException>(() => fixture.Store.Delete(null!, null, "rev"));
        Assert.Throws<ArgumentNullException>(() => fixture.Store.Delete(Global(PromptTemplateKind.Command, "x"), null, null!));
        Assert.Throws<ArgumentNullException>(() => fixture.Store.Save(Global(PromptTemplateKind.Command, "x"), null, null!, null));
        Assert.Throws<ArgumentNullException>(() => fixture.Store.Read(null!, null));
    }

    [Fact]
    public void Concurrent_creates_of_the_same_file_let_exactly_one_win()
    {
        using var fixture = new TemplateFixture();
        var reference = Global(PromptTemplateKind.Command, "race");

        var results = Enumerable.Range(0, 16)
            .AsParallel()
            .Select(i => fixture.Store.Save(reference, null, $"writer {i}", null).Status)
            .ToArray();

        Assert.Equal(1, results.Count(s => s == PromptTemplateSaveStatus.Saved));
        Assert.Equal(15, results.Count(s => s == PromptTemplateSaveStatus.Conflict));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.User, "commands")));
    }

    // ---- BuiltInText round trip ----

    public static TheoryData<PromptTemplateKind, string> BuiltInKeys()
    {
        var data = new TheoryData<PromptTemplateKind, string>();
        foreach (var template in PromptTemplateRegistry.Core.All)
        {
            data.Add(template.Kind, template.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInKeys))]
    public void BuiltInText_saved_to_global_is_valid_and_resolves_to_the_same_template(PromptTemplateKind kind, string id)
    {
        using var fixture = new TemplateFixture();
        var builtIn = PromptTemplateRegistry.Core.Find(kind, id)!;
        var baseline = PromptTemplateSet.BuiltIn();
        var text = PromptTemplateStore.BuiltInText(builtIn);

        Assert.Empty(fixture.Store.Validate(kind, id, text));
        var saved = fixture.Store.Save(Global(kind, id), null, text, null);
        var resolved = fixture.Resolve();

        Assert.Equal(PromptTemplateSaveStatus.Saved, saved.Status);
        switch (kind)
        {
            case PromptTemplateKind.Command:
                var before = baseline.FindCommand(id)!;
                var after = resolved.FindCommand(id)!;
                Assert.Equal(before.Body, after.Body);
                Assert.Equal(before.Description, after.Description);
                Assert.Equal(before.ArgumentHint, after.ArgumentHint);
                Assert.Equal(PromptTemplateScope.Global, after.Scope);
                break;
            case PromptTemplateKind.Agent:
                var agentBefore = baseline.Agents.Single(a => PromptTemplateIds.FromAgentName(a.Name) == id);
                var agentAfter = resolved.Agents.Single(a => PromptTemplateIds.FromAgentName(a.Name) == id);
                Assert.Equal(agentBefore.Name, agentAfter.Name);
                Assert.Equal(agentBefore.Description, agentAfter.Description);
                Assert.Equal(agentBefore.Prompt, agentAfter.Prompt);
                Assert.Equal(agentBefore.Model, agentAfter.Model);
                Assert.Equal(builtIn.Agent!.ToolNames, agentAfter.ToolNames);
                Assert.Equal(builtIn.Agent.Model.Thinking, agentAfter.Model.Thinking);
                break;
            default:
                Assert.Equal(baseline.Slot(id), resolved.Slot(id));
                Assert.True(resolved.IsSlotCustomized(id));
                break;
        }
    }

    [Fact]
    public void BuiltInText_of_an_agent_writes_name_description_thinking_and_an_inline_tool_list()
    {
        var text = PromptTemplateStore.BuiltInText(PromptTemplateRegistry.Core.Find(PromptTemplateKind.Agent, "scout")!);

        Assert.StartsWith("---\nname: Scout\n", text, StringComparison.Ordinal);
        Assert.Contains("\nthinking: low\n", text, StringComparison.Ordinal);
        Assert.Contains("\ntools: [read, grep, find, ls]\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void BuiltInText_of_a_slot_is_the_bare_body_and_of_a_command_has_description_and_hint()
    {
        var slot = PromptTemplateStore.BuiltInText(PromptTemplateRegistry.Core.Find(PromptTemplateKind.Slot, PromptSlotIds.System)!);
        var command = PromptTemplateStore.BuiltInText(BuiltInCommands.Explain);

        Assert.Equal(PromptTemplateSet.BuiltIn().Slot(PromptSlotIds.System) + "\n", slot);
        Assert.StartsWith("---\ndescription: ", command, StringComparison.Ordinal);
        Assert.Contains("\nargument-hint: <file, symbol or feature>\n---\n", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInText_of_a_slot_with_placeholders_is_valid_and_keeps_them()
    {
        var registry = PromptTemplateRegistry.Core.With(
            [PromptTemplateRegistry.Slot("pr.test", "PR", "d", "Review {pr} on {branch}: {\"json\": true}", "pr", "branch")]);
        using var fixture = new TemplateFixture(registry);
        var builtIn = registry.Find(PromptTemplateKind.Slot, "pr.test")!;

        var text = PromptTemplateStore.BuiltInText(builtIn);
        var saved = fixture.Store.Save(Global(PromptTemplateKind.Slot, "pr.test"), null, text, null);

        Assert.Equal(PromptTemplateSaveStatus.Saved, saved.Status);
        Assert.Equal("Review {pr} on {branch}: {\"json\": true}", fixture.Resolve().Slot("pr.test"));
    }
}
