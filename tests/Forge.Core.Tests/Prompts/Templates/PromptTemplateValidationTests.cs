using System.Text;
using Forge.Abstractions;
using Forge.Core.Prompts;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.Core.Tests.Prompts.Templates;

/// <summary>Frontmatter parsing (observed through the store, the parser is internal) and per-kind validation.</summary>
public class PromptTemplateValidationTests
{
    private static readonly PromptTemplateRegistry Registry = PromptTemplateRegistry.Core.With(
        [PromptTemplateRegistry.Slot("test.slot", "Test slot", "d", "Hello {pr} on {branch}", "pr", "branch")]);

    private static IReadOnlyList<PromptTemplateProblem> Validate(PromptTemplateKind kind, string id, string text) =>
        new PromptTemplateStore(Registry, null).Validate(kind, id, text);

    private static PromptTemplateProblem[] Problems(PromptTemplateKind kind, string id, string text) =>
        [.. Validate(kind, id, text)];

    // ---- frontmatter, observed through Resolve ----

    [Fact]
    public void Without_frontmatter_the_whole_file_is_the_body()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "plain", "Just a body.\nSecond line.\n");

        var command = fixture.Resolve().FindCommand("plain")!;

        Assert.Equal("Just a body.\nSecond line.", command.Body);
        Assert.Equal(string.Empty, command.Description);
        Assert.Null(command.ArgumentHint);
        Assert.Equal(PromptTemplateScope.Global, command.Scope);
    }

    [Fact]
    public void A_leading_utf8_bom_does_not_hide_the_frontmatter()
    {
        using var fixture = new TemplateFixture();
        var text = "---\ndescription: with bom\nargument-hint: <x>\n---\nBody $ARGUMENTS\n";
        fixture.GlobalBytes(PromptTemplateKind.Command, "bom", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)]);

        var command = fixture.Resolve().FindCommand("bom")!;

        Assert.Equal("with bom", command.Description);
        Assert.Equal("<x>", command.ArgumentHint);
        Assert.Equal("Body $ARGUMENTS", command.Body);
    }

    [Fact]
    public void A_bom_inside_the_text_is_also_skipped_by_the_parser()
    {
        // A U+FEFF char at the start of the string (as opposed to file bytes) must not defeat the leading "---".
        var problems = Problems(PromptTemplateKind.Command, "x", "\uFEFF---\nbogus: 1\n---\nbody");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, "bogus")], problems);
    }

    [Fact]
    public void Crlf_files_parse_and_the_body_is_normalized_to_lf()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "crlf", "---\r\ndescription: crlf desc\r\n---\r\nline one\r\nline two\r\n");

        var command = fixture.Resolve().FindCommand("crlf")!;

        Assert.Equal("crlf desc", command.Description);
        Assert.Equal("line one\nline two", command.Body);
    }

    [Fact]
    public void Delimiter_lines_may_carry_surrounding_whitespace()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "spaced", "  ---  \ndescription: d\n\t---\t\nBody");

        var command = fixture.Resolve().FindCommand("spaced")!;

        Assert.Equal("d", command.Description);
        Assert.Equal("Body", command.Body);
    }

    [Fact]
    public void An_unclosed_frontmatter_block_is_reported_and_the_whole_file_counts_as_body()
    {
        // keys inside the unclosed block are dropped (no UnknownKey) and the file is non-empty body (no EmptyBody).
        var problems = Problems(PromptTemplateKind.Command, "x", "---\nbogus: 1\ndescription: d\nsome text\n");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnclosedFrontmatter)], problems);
    }

    [Fact]
    public void An_unclosed_block_file_is_ignored_by_resolve_but_listed_with_the_problem()
    {
        using var fixture = new TemplateFixture();
        var path = fixture.Global(PromptTemplateKind.Command, "review", "---\ndescription: broken\nno closing line");

        var set = fixture.Resolve();
        var entry = fixture.Entries(PromptTemplateKind.Command, "review").Single(e => e.Ref.Scope == PromptTemplateScope.Global);

        Assert.Equal(BuiltInCommands.Review.Description, set.FindCommand("review")!.Description);
        Assert.Equal(PromptTemplateScope.BuiltIn, PromptScope(set, "review"));
        Assert.Equal([PromptTemplateProblemCode.UnclosedFrontmatter], entry.Problems.Select(p => p.Code).ToArray());
        Assert.False(entry.IsValid);
        Assert.Equal(path, entry.Path);
    }

    private static PromptTemplateScope PromptScope(PromptTemplateSet set, string command) => set.FindCommand(command)!.Scope;

    [Fact]
    public void A_dashes_line_inside_the_body_after_the_closing_delimiter_stays_in_the_body()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "dashes", "---\ndescription: d\n---\nabove\n---\nbelow\n");

        Assert.Equal("above\n---\nbelow", fixture.Resolve().FindCommand("dashes")!.Body);
    }

    [Fact]
    public void Quoted_values_keep_hashes_and_colons_and_unquoted_trailing_comments_are_stripped()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(
            PromptTemplateKind.Command,
            "quoted",
            "---\ndescription: \"a: b # c\"\nargument-hint: '<x #y>'\n---\nBody");
        fixture.Global(PromptTemplateKind.Command, "commented", "---\ndescription: hello # trailing comment\nargument-hint: <a#b>\n---\nBody");

        var set = fixture.Resolve();

        Assert.Equal("a: b # c", set.FindCommand("quoted")!.Description);
        Assert.Equal("<x #y>", set.FindCommand("quoted")!.ArgumentHint);
        Assert.Equal("hello", set.FindCommand("commented")!.Description);
        Assert.Equal("<a#b>", set.FindCommand("commented")!.ArgumentHint);
    }

    [Fact]
    public void Double_quoted_values_unescape_and_a_value_may_contain_a_colon_unquoted()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "esc", "---\ndescription: \"say \\\"hi\\\"\\tnow\"\nargument-hint: http://x/y\n---\nBody");

        var command = fixture.Resolve().FindCommand("esc")!;

        Assert.Equal("say \"hi\"\tnow", command.Description);
        Assert.Equal("http://x/y", command.ArgumentHint);
    }

    [Fact]
    public void Keys_are_case_insensitive_and_a_repeated_key_keeps_its_last_value()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "dup", "---\nDescription: first\ndescription: second\nDESCRIPTION: third\n---\nBody");

        Assert.Equal("third", fixture.Resolve().FindCommand("dup")!.Description);
        Assert.Empty(Validate(PromptTemplateKind.Command, "dup", "---\nDescription: a\ndescription: b\n---\nBody"));
    }

    [Fact]
    public void Comment_and_blank_lines_inside_the_block_are_ignored()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "cmt", "---\n# a comment\n\ndescription: kept\n   # indented comment\n---\nBody");

        Assert.Equal("kept", fixture.Resolve().FindCommand("cmt")!.Description);
    }

    [Fact]
    public void An_empty_scalar_falls_back_to_the_built_in_description()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "review", "---\ndescription:\nargument-hint: \"\"\n---\nMy review $ARGUMENTS");

        var command = fixture.Resolve().FindCommand("review")!;

        Assert.Equal(BuiltInCommands.Review.Description, command.Description);
        Assert.Equal(BuiltInCommands.Review.ArgumentHint, command.ArgumentHint);
        Assert.Equal("My review $ARGUMENTS", command.Body);
    }

    [Fact]
    public void Inline_and_block_lists_both_feed_the_agent_tool_whitelist()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "inline", "---\ntools: [read, \"grep\", 'ls', ]\n---\nrole");
        fixture.Global(PromptTemplateKind.Agent, "block", "---\ntools:\n  - read\n  # note\n  - \"edit\"\n\n  - bash\n---\nrole");
        fixture.Global(PromptTemplateKind.Agent, "scalar", "---\ntools: read\n---\nrole");

        var agents = fixture.Resolve().Agents.ToDictionary(a => a.Name);

        Assert.Equal(["read", "grep", "ls"], agents["inline"].ToolNames);
        Assert.Equal(["read", "edit", "bash"], agents["block"].ToolNames);
        Assert.Equal(["read"], agents["scalar"].ToolNames);
    }

    [Theory]
    [InlineData("---\ntools: []\n---\nrole")]
    [InlineData("---\ntools:\n---\nrole")]
    [InlineData("---\ntools:\n  # - read\n---\nrole")]
    public void An_empty_tool_list_is_invalid_rather_than_every_tool(string text)
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "empty", text);
        fixture.Global(PromptTemplateKind.Agent, "scout", text);

        Assert.Contains(fixture.Store.Validate(PromptTemplateKind.Agent, "empty", text), p => p.Code == PromptTemplateProblemCode.InvalidValue && p.Detail == "tools");
        var agents = fixture.Resolve().Agents;
        Assert.DoesNotContain(agents, a => a.Name == "empty");
        // The invalid Scout override is left out, so Scout keeps its built-in read-only whitelist.
        var scout = agents.Single(a => a.Name == "Scout");
        Assert.Equal(BuiltInAgents.Scout.ToolNames, scout.ToolNames);
    }

    [Fact]
    public void A_built_in_agent_override_cannot_rename_it()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "scout", "---\nname: Explorer\n---\nExplore.");
        fixture.Global(PromptTemplateKind.Agent, "worker", "---\nname: worker\n---\nWork.");

        var entry = fixture.Entries(PromptTemplateKind.Agent, "scout").Single(e => e.Ref.Scope == PromptTemplateScope.Global);
        Assert.Contains(entry.Problems, p => p.Code == PromptTemplateProblemCode.InvalidValue && p.Detail == "name");
        var agents = fixture.Resolve().Agents;
        Assert.DoesNotContain(agents, a => a.Name == "Explorer");
        Assert.Equal(BuiltInAgents.Scout.Prompt, agents.Single(a => a.Name == "Scout").Prompt);
        // The same name in another case is the same identity, and the built-in spelling is kept.
        Assert.Equal("Work.", agents.Single(a => a.Name == BuiltInAgents.Worker.Name).Prompt);
    }

    [Theory]
    [InlineData("---\nname: Scout\n---\nI am not Scout.")]
    [InlineData("---\nname: REVIEWER\n---\nr")]
    [InlineData("---\nname: plan\n---\np")]
    public void A_user_agent_cannot_take_a_built_in_name(string text)
    {
        using var fixture = new TemplateFixture();
        fixture.Project(PromptTemplateKind.Agent, "helper", text);

        Assert.Contains(fixture.Store.Validate(PromptTemplateKind.Agent, "helper", text), p => p.Code == PromptTemplateProblemCode.InvalidValue && p.Detail == "name");
        var set = fixture.Resolve();
        Assert.Equal(BuiltInAgents.All.Count, set.Agents.Count);
        Assert.Equal(BuiltInAgents.Scout.ToolNames, set.FindAgent("scout")!.Definition.ToolNames);
    }

    [Fact]
    public void Two_user_agents_with_one_name_keep_the_first_id()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "b-helper", "---\nname: Helper\n---\nsecond");
        fixture.Project(PromptTemplateKind.Agent, "a-helper", "---\nname: helper\n---\nfirst");

        var set = fixture.Resolve();

        var helper = Assert.Single(set.ResolvedAgents, a => a.Definition.Name.Equals("helper", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("a-helper", helper.Id);
        Assert.Equal("first", helper.Definition.Prompt);
    }

    [Fact]
    public void Resolved_agents_carry_the_file_id_and_whether_thinking_was_set()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "researcher", "---\nname: 研究员\nthinking: high\n---\nr");
        fixture.Global(PromptTemplateKind.Agent, "plain", "---\nname: Plain\n---\np");

        var set = fixture.Resolve();

        var researcher = set.FindAgent("研究员")!;
        Assert.Equal("researcher", researcher.Id);
        Assert.True(researcher.ExplicitThinking);
        Assert.Equal(ThinkingEffort.High, researcher.Definition.Model.Thinking);
        Assert.False(set.FindAgent("plain")!.ExplicitThinking);
        Assert.Equal("scout", set.FindAgent("Scout")!.Id);
        Assert.False(set.FindAgent("Scout")!.ExplicitThinking);
    }

    [Fact]
    public void A_list_item_line_without_a_preceding_list_key_is_ignored()
    {
        // "- x" right after a scalar key must not be attached to it.
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "orphan", "---\nthinking: low\n- stray\ntools: read\n---\nrole");

        var agent = fixture.Resolve().Agents.Single(a => a.Name == "orphan");

        Assert.Equal(["read"], agent.ToolNames);
        Assert.Equal(ThinkingEffort.Low, agent.Model.Thinking);
    }

    [Fact]
    public void A_user_agent_takes_name_model_and_thinking_from_its_frontmatter()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Agent, "researcher", "---\nname: Deep Researcher\ndescription: finds things\nmodel: gpt-x\nthinking: HIGH\n---\nYou research.");
        fixture.Global(PromptTemplateKind.Agent, "bare", "Just a role.");

        var agents = fixture.Resolve().Agents;
        var researcher = agents.Single(a => a.Name == "Deep Researcher");
        var bare = agents.Single(a => a.Name == "bare");

        Assert.Equal("finds things", researcher.Description);
        Assert.Equal("You research.", researcher.Prompt);
        Assert.Equal(new ModelSettings("gpt-x", ThinkingEffort.High), researcher.Model);
        Assert.Null(researcher.ToolNames);
        Assert.Equal(new ModelSettings("default", ThinkingEffort.Off), bare.Model);
        Assert.Equal(string.Empty, bare.Description);
    }

    // ---- validation per kind ----

    [Fact]
    public void A_valid_file_has_no_problems()
    {
        Assert.Empty(Validate(PromptTemplateKind.Command, "ok", "---\ndescription: d\nargument-hint: h\nmode: append\n---\nBody"));
        Assert.Empty(Validate(PromptTemplateKind.Agent, "ok", "---\nname: N\ndescription: d\nmodel: m\nthinking: max\ntools: [read]\n---\nBody"));
        Assert.Empty(Validate(PromptTemplateKind.Slot, "system", "---\ndescription: d\nmode: replace\n---\nBody"));
    }

    [Theory]
    [InlineData(PromptTemplateKind.Agent, "argument-hint")]
    [InlineData(PromptTemplateKind.Slot, "argument-hint")]
    [InlineData(PromptTemplateKind.Command, "thinking")]
    [InlineData(PromptTemplateKind.Command, "tools")]
    [InlineData(PromptTemplateKind.Command, "model")]
    [InlineData(PromptTemplateKind.Command, "name")]
    [InlineData(PromptTemplateKind.Slot, "tools")]
    [InlineData(PromptTemplateKind.Slot, "thinking")]
    [InlineData(PromptTemplateKind.Command, "color")]
    public void Keys_a_kind_does_not_know_are_reported_with_the_key(PromptTemplateKind kind, string key)
    {
        var id = kind == PromptTemplateKind.Slot ? "system" : "x";

        var problems = Problems(kind, id, $"---\n{key}: value\n---\nBody");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, key)], problems);
    }

    [Fact]
    public void Several_unknown_keys_are_all_reported_in_file_order()
    {
        var problems = Problems(PromptTemplateKind.Command, "x", "---\nzeta: 1\nalpha: 2\nZETA: 3\n---\nBody");

        Assert.Equal(
            [
                new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, "zeta"),
                new PromptTemplateProblem(PromptTemplateProblemCode.UnknownKey, "alpha"),
            ],
            problems);
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("prepend")]
    [InlineData("\"append now\"")]
    public void A_bad_mode_is_an_invalid_value(string mode)
    {
        var problems = Problems(PromptTemplateKind.Command, "x", $"---\nmode: {mode}\n---\nBody");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.InvalidValue, "mode")], problems);
    }

    [Theory]
    [InlineData("APPEND")]
    [InlineData("Replace")]
    public void Mode_is_case_insensitive(string mode) =>
        Assert.Empty(Validate(PromptTemplateKind.Command, "x", $"---\nmode: {mode}\n---\nBody"));

    [Theory]
    [InlineData("loud")]
    [InlineData("extra-high")]
    [InlineData("1")]
    public void A_bad_thinking_level_on_an_agent_is_an_invalid_value(string thinking)
    {
        var problems = Problems(PromptTemplateKind.Agent, "x", $"---\nthinking: {thinking}\n---\nBody");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.InvalidValue, "thinking")], problems);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData("XHigh")]
    public void Every_thinking_level_name_is_accepted(string thinking) =>
        Assert.Empty(Validate(PromptTemplateKind.Agent, "x", $"---\nthinking: {thinking}\n---\nBody"));

    [Theory]
    [InlineData("")]
    [InlineData("\n\n  \n")]
    [InlineData("---\ndescription: d\n---\n")]
    [InlineData("---\ndescription: d\n---\n \t\r\n\r\n")]
    public void An_empty_body_is_reported(string text)
    {
        var problems = Problems(PromptTemplateKind.Command, "x", text);

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.EmptyBody)], problems);
    }

    [Fact]
    public void A_slot_file_for_an_unregistered_slot_is_an_unknown_slot()
    {
        var problems = Problems(PromptTemplateKind.Slot, "nope", "Body");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.UnknownSlot, "nope")], problems);
    }

    [Fact]
    public void An_invalid_slot_id_is_only_an_invalid_id_not_also_an_unknown_slot()
    {
        var problems = Problems(PromptTemplateKind.Slot, "Nope", "Body");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.InvalidId, "Nope")], problems);
    }

    [Theory]
    [InlineData(PromptTemplateKind.Command, "Review")]
    [InlineData(PromptTemplateKind.Agent, "con")]
    [InlineData(PromptTemplateKind.Command, "")]
    [InlineData(PromptTemplateKind.Command, "-x")]
    public void An_invalid_id_is_reported(PromptTemplateKind kind, string id)
    {
        var problems = Problems(kind, id, "Body");

        Assert.Equal([new PromptTemplateProblem(PromptTemplateProblemCode.InvalidId, id)], problems);
    }

    [Fact]
    public void A_slot_with_placeholders_rejects_names_it_does_not_provide_once_each()
    {
        var problems = Problems(PromptTemplateKind.Slot, "test.slot", "{pr} {other} {branch} {other} {third_1}");

        Assert.Equal(
            [
                new PromptTemplateProblem(PromptTemplateProblemCode.UnknownPlaceholder, "other"),
                new PromptTemplateProblem(PromptTemplateProblemCode.UnknownPlaceholder, "third_1"),
            ],
            problems);
    }

    [Fact]
    public void A_slot_with_placeholders_accepts_json_braces_and_non_identifier_braces()
    {
        var text = "Use {pr} here.\n{\"type\": \"object\", \"x\": {\"y\": 1}}\n{} { } {Upper} {a-b} {1abc} {open";

        Assert.Empty(Validate(PromptTemplateKind.Slot, "test.slot", text));
    }

    [Fact]
    public void A_slot_without_placeholders_accepts_any_braces()
    {
        Assert.Empty(Validate(PromptTemplateKind.Slot, "system", "Reply with {name} and {\"json\": true} and {pr}."));
        Assert.Empty(Validate(PromptTemplateKind.Slot, PromptSlotIds.CompactionSummarizer, "Keep {anything}."));
    }

    [Fact]
    public void Placeholders_are_not_checked_for_commands_and_agents()
    {
        Assert.Empty(Validate(PromptTemplateKind.Command, "x", "Do {whatever} with {this}"));
        Assert.Empty(Validate(PromptTemplateKind.Agent, "x", "Role {x}"));
    }

    [Fact]
    public void Text_over_256_kib_is_too_large_and_exactly_256_kib_is_not()
    {
        var atLimit = new string('a', PromptTemplateStore.MaxFileBytes);
        var over = atLimit + "a";

        Assert.Empty(Validate(PromptTemplateKind.Command, "x", atLimit));
        Assert.Equal(
            [new PromptTemplateProblem(PromptTemplateProblemCode.TooLarge)],
            Problems(PromptTemplateKind.Command, "x", over));
        Assert.Equal(256 * 1024, PromptTemplateStore.MaxFileBytes);
    }

    [Fact]
    public void The_size_limit_counts_utf8_bytes_not_characters()
    {
        var multibyte = new string('\u00e9', PromptTemplateStore.MaxFileBytes / 2 + 1);

        Assert.Equal(
            [new PromptTemplateProblem(PromptTemplateProblemCode.TooLarge)],
            Problems(PromptTemplateKind.Command, "x", multibyte));
    }

    [Fact]
    public void A_file_over_the_limit_on_disk_is_ignored_and_listed_as_too_large()
    {
        using var fixture = new TemplateFixture();
        var path = fixture.Global(PromptTemplateKind.Command, "huge", new string('a', PromptTemplateStore.MaxFileBytes + 1));

        var set = fixture.Resolve();
        var entry = fixture.Entries(PromptTemplateKind.Command, "huge").Single();

        Assert.Null(set.FindCommand("huge"));
        Assert.Equal([PromptTemplateProblemCode.TooLarge], entry.Problems.Select(p => p.Code).ToArray());
        Assert.Equal(path, entry.Path);
    }

    [Fact]
    public void A_file_that_is_not_valid_utf8_is_unreadable_and_ignored()
    {
        using var fixture = new TemplateFixture();
        fixture.GlobalBytes(PromptTemplateKind.Command, "latin", [0x42, 0x6F, 0x64, 0x79, 0xC3, 0x28, 0x0A]);

        var set = fixture.Resolve();
        var entry = fixture.Entries(PromptTemplateKind.Command, "latin").Single();

        Assert.Null(set.FindCommand("latin"));
        var problem = Assert.Single(entry.Problems);
        Assert.Equal(PromptTemplateProblemCode.Unreadable, problem.Code);
        Assert.False(string.IsNullOrEmpty(problem.Detail));
    }

    [Fact]
    public void A_file_with_an_invalid_id_on_disk_is_listed_with_invalid_id_and_never_resolved()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "Bad_Name", "Body");
        fixture.Global(PromptTemplateKind.Command, "nul", "Body");

        var set = fixture.Resolve();

        Assert.Null(set.FindCommand("Bad_Name"));
        Assert.Null(set.FindCommand("nul"));
        Assert.Equal(
            [PromptTemplateProblemCode.InvalidId],
            fixture.Entries(PromptTemplateKind.Command, "Bad_Name").Single().Problems.Select(p => p.Code).ToArray());
        Assert.Equal(
            [PromptTemplateProblemCode.InvalidId],
            fixture.Entries(PromptTemplateKind.Command, "nul").Single().Problems.Select(p => p.Code).ToArray());
    }

    [Fact]
    public void Multiple_problems_are_all_reported()
    {
        var problems = Problems(PromptTemplateKind.Agent, "x", "---\nargument-hint: h\nmode: nope\nthinking: loud\n---\n");

        Assert.Equal(
            [
                PromptTemplateProblemCode.UnknownKey,
                PromptTemplateProblemCode.InvalidValue,
                PromptTemplateProblemCode.InvalidValue,
                PromptTemplateProblemCode.EmptyBody,
            ],
            problems.Select(p => p.Code).ToArray());
    }

    [Fact]
    public void Validate_rejects_null_text()
    {
        Assert.Throws<ArgumentNullException>(() => new PromptTemplateStore(Registry, null).Validate(PromptTemplateKind.Command, "x", null!));
    }

    [Fact]
    public void Read_refuses_an_oversized_file_instead_of_loading_it()
    {
        using var fixture = new TemplateFixture();
        var big = new string('x', PromptTemplateStore.MaxFileBytes + 1);
        fixture.Global(PromptTemplateKind.Command, "big", big);

        Assert.Throws<IOException>(() => fixture.Store.Read(new PromptTemplateRef(PromptTemplateKind.Command, PromptTemplateScope.Global, "big"), fixture.Workspace));
    }

    [Fact]
    public void Read_refuses_invalid_utf8_so_a_save_cannot_overwrite_unseen_bytes()
    {
        using var fixture = new TemplateFixture();
        fixture.GlobalBytes(PromptTemplateKind.Command, "latin", [0x68, 0x69, 0xE9, 0xFF]);

        Assert.Throws<IOException>(() => fixture.Store.Read(new PromptTemplateRef(PromptTemplateKind.Command, PromptTemplateScope.Global, "latin"), fixture.Workspace));
    }
}
