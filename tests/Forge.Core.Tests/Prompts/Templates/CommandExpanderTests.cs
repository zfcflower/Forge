using Forge.Abstractions;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.Core.Tests.Prompts.Templates;

public class CommandExpanderTests
{
    [Theory]
    [InlineData("/review x", "review", "x")]
    [InlineData("  /Review", "review", "")]
    [InlineData("/REVIEW   a  b  ", "review", "a  b")]
    [InlineData("/a.b", "a.b", "")]
    [InlineData("/fix\nsecond line", "fix", "second line")]
    [InlineData("\t\n/fix it", "fix", "it")]
    [InlineData("/fix \"quoted arg\"", "fix", "\"quoted arg\"")]
    public void TryParse_splits_a_slash_message_into_a_lower_case_name_and_trimmed_arguments(string message, string name, string arguments)
    {
        Assert.True(CommandExpander.TryParse(message, out var parsedName, out var parsedArguments));
        Assert.Equal(name, parsedName);
        Assert.Equal(arguments, parsedArguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("  /  ")]
    [InlineData("review x")]
    [InlineData("/ review")]
    [InlineData("/usr/bin/x")]
    [InlineData("/-dash")]
    [InlineData("/a.")]
    [InlineData("/con")]
    [InlineData("//double")]
    [InlineData("x /review")]
    public void TryParse_rejects_text_that_is_not_a_command_invocation(string? message)
    {
        Assert.False(CommandExpander.TryParse(message, out var name, out var arguments));
        Assert.Equal(string.Empty, name);
        Assert.Equal(string.Empty, arguments);
    }

    [Theory]
    [InlineData("Run $ARGUMENTS now", "  a  b  ", "Run a  b now")]
    [InlineData("Run $@ now", "a b", "Run a b now")]
    [InlineData("$ARGUMENTS|$@|$ARGUMENTS", "x", "x|x|x")]
    [InlineData("[$ARGUMENTS]", "", "[]")]
    [InlineData("[$@]", null, "[]")]
    [InlineData("$1 and $2", "one two", "one and two")]
    [InlineData("$2 $1", "one two", "two one")]
    [InlineData("$9", "1 2 3 4 5 6 7 8 nine", "nine")]
    [InlineData("[$1][$2][$3]", "a", "[a][][]")]
    [InlineData("[$1]", "", "[]")]
    [InlineData("$1|$2", "\"hello world\" 'x y'", "hello world|x y")]
    [InlineData("$1|$2", "\"\" b", "|b")]
    [InlineData("$1", "\"unterminated quote here", "unterminated quote here")]
    [InlineData("$1 $2", "a\tb", "a b")]
    [InlineData("$1,$1", "x", "x,x")]
    [InlineData("$1.", "x", "x.")]
    [InlineData("$1x", "a", "ax")]
    public void Expand_substitutes_the_placeholders(string body, string? arguments, string expected)
    {
        Assert.Equal(expected, CommandExpander.Expand(body, arguments));
    }

    [Fact]
    public void Dollar_ten_is_not_dollar_one_followed_by_a_zero()
    {
        // "$10" is no placeholder, so the body counts as using none: the argument is appended after a blank line.
        Assert.Equal("$10\n\na b", CommandExpander.Expand("$10", "a b"));
        Assert.Equal("$10 a", CommandExpander.Expand("$10 $1", "a"));
        Assert.Equal("$12 and a", CommandExpander.Expand("$12 and $1", "a b"));
    }

    [Fact]
    public void A_body_without_placeholders_gets_the_arguments_after_a_blank_line()
    {
        Assert.Equal("Do it\n\nthe thing", CommandExpander.Expand("Do it", "the thing"));
        Assert.Equal("Do it\n\nthe thing", CommandExpander.Expand("Do it\n\n\n", "  the thing "));
        Assert.Equal("the thing", CommandExpander.Expand("   \n", "the thing"));
        Assert.Equal("$0 and $ and $x\n\nz", CommandExpander.Expand("$0 and $ and $x", "z"));
        Assert.Equal("cost $\n\nz", CommandExpander.Expand("cost $", "z"));
        Assert.Equal("$arguments\n\nz", CommandExpander.Expand("$arguments", "z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void A_body_without_placeholders_is_unchanged_when_there_are_no_arguments(string? arguments)
    {
        Assert.Equal("Do it\n", CommandExpander.Expand("Do it\n", arguments));
    }

    [Fact]
    public void An_argument_that_looks_like_a_placeholder_is_never_expanded_again()
    {
        Assert.Equal("Hi $2 oops and b", CommandExpander.Expand("Hi $1 and $2", "'$2 oops' b"));
        Assert.Equal("$1 x $1", CommandExpander.Expand("$ARGUMENTS $1", "$1 x"));
        Assert.Equal("$ARGUMENTS|$ARGUMENTS", CommandExpander.Expand("$@|$1", "$ARGUMENTS"));
        Assert.Equal("$@", CommandExpander.Expand("$ARGUMENTS", "$@"));
    }

    [Fact]
    public void Expand_rejects_a_null_body()
    {
        Assert.Throws<ArgumentNullException>(() => CommandExpander.Expand(null!, "x"));
    }

    [Fact]
    public void Quotes_group_words_into_one_positional_argument_and_adjacent_quotes_join()
    {
        Assert.Equal("a|b c|d e|f", CommandExpander.Expand("$1|$2|$3|$4", "a \"b c\" 'd e'   f"));
        Assert.Equal("it's", CommandExpander.Expand("$1", "\"it's\""));
        Assert.Equal("ab|", CommandExpander.Expand("$1|$2", "a\"\"b"));
    }

    // ---- PromptTemplateSet ----

    [Fact]
    public void TryExpandCommand_expands_a_known_command_with_its_arguments()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "greet", "Hello $1, you said: $ARGUMENTS");
        var set = fixture.Resolve();

        Assert.True(set.TryExpandCommand("/Greet Bob 'and more'", out var expanded));
        Assert.Equal("Hello Bob, you said: Bob 'and more'", expanded);
    }

    [Fact]
    public void TryExpandCommand_expands_a_built_in_in_place_of_its_placeholder()
    {
        var set = PromptTemplateSet.BuiltIn();

        Assert.True(set.TryExpandCommand("/explain src/App.cs", out var expanded));

        Assert.Contains("Explain how this works in this repository: src/App.cs", expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("$ARGUMENTS", expanded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/nope x")]
    [InlineData("just text")]
    [InlineData("/usr/bin/ls")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExpandCommand_leaves_unknown_commands_and_plain_text_alone(string? message)
    {
        var set = PromptTemplateSet.BuiltIn();

        Assert.False(set.TryExpandCommand(message, out var expanded));
        Assert.Equal(string.Empty, expanded);
    }

    [Fact]
    public void TryExpandCommand_uses_the_overriding_body_not_the_built_in()
    {
        using var fixture = new TemplateFixture();
        fixture.Global(PromptTemplateKind.Command, "fix", "MY FIX: $ARGUMENTS");

        Assert.True(fixture.Resolve().TryExpandCommand("/fix the bug", out var expanded));
        Assert.Equal("MY FIX: the bug", expanded);
    }

    // ---- FormatSlot / Slot lookup ----

    private static readonly PromptTemplateRegistry WithSlot = PromptTemplateRegistry.Core.With(
        [PromptTemplateRegistry.Slot("test.slot", "Test", "d", "Hello {pr} on {branch}; {unknown} {pr} { } {} {\"j\": 1}", "pr", "branch")]);

    private static PromptTemplateSet SetWith(string? slotOverride)
    {
        var fixture = new TemplateFixture(WithSlot);
        if (slotOverride is not null)
        {
            fixture.Global(PromptTemplateKind.Slot, "test.slot", slotOverride);
        }

        var set = fixture.Resolve();
        fixture.Dispose();
        return set;
    }

    [Fact]
    public void FormatSlot_replaces_provided_names_and_leaves_other_braces_as_typed()
    {
        var set = SetWith(null);

        var text = set.FormatSlot("test.slot", new Dictionary<string, string> { ["pr"] = "#7", ["branch"] = "main" });

        Assert.Equal("Hello #7 on main; {unknown} #7 { } {} {\"j\": 1}", text);
    }

    [Fact]
    public void FormatSlot_is_single_pass_a_value_containing_a_placeholder_is_not_expanded_again()
    {
        var set = SetWith(null);

        var text = set.FormatSlot("test.slot", new Dictionary<string, string> { ["pr"] = "{branch}", ["branch"] = "{pr}" });

        Assert.Equal("Hello {branch} on {pr}; {unknown} {branch} { } {} {\"j\": 1}", text);
    }

    [Fact]
    public void FormatSlot_uses_the_user_override_text_and_ignores_names_without_a_value()
    {
        var set = SetWith("{pr} only, {branch} missing, { {pr} }, tail {");

        var text = set.FormatSlot("test.slot", new Dictionary<string, string> { ["pr"] = "42" });

        Assert.Equal("42 only, {branch} missing, { 42 }, tail {", text);
    }

    [Fact]
    public void FormatSlot_with_no_values_returns_the_slot_text()
    {
        var set = SetWith("Plain {pr} text");

        Assert.Equal("Plain {pr} text", set.FormatSlot("test.slot", new Dictionary<string, string>()));
    }

    [Fact]
    public void Slot_lookup_distinguishes_throwing_and_nullable_forms()
    {
        var set = PromptTemplateSet.BuiltIn();

        Assert.NotNull(set.FindSlot(PromptSlotIds.System));
        Assert.Equal(set.FindSlot(PromptSlotIds.System), set.Slot(PromptSlotIds.System));
        Assert.Null(set.FindSlot("nope"));
        Assert.Throws<KeyNotFoundException>(() => set.Slot("nope"));
        Assert.Throws<KeyNotFoundException>(() => set.FormatSlot("nope", new Dictionary<string, string>()));
        Assert.Throws<ArgumentNullException>(() => set.FormatSlot(PromptSlotIds.System, null!));
    }

    [Fact]
    public void BuiltIn_set_has_the_core_commands_agents_and_slots_and_no_customization()
    {
        var set = PromptTemplateSet.BuiltIn();

        Assert.Equal(["review", "explain", "tests", "fix"], set.Commands.Select(c => c.Name).ToArray());
        Assert.Equal(["Scout", "Worker", "Plan", "Reviewer"], set.Agents.Select(a => a.Name).ToArray());
        Assert.All(set.Commands, c => Assert.Equal(PromptTemplateScope.BuiltIn, c.Scope));
        Assert.False(set.IsSlotCustomized(PromptSlotIds.CompactionSummarizer));
        Assert.Null(set.FindCommand("Review"));
        Assert.NotNull(set.FindCommand("review"));
    }

    [Fact]
    public void BuiltIn_set_accepts_an_extended_registry()
    {
        var set = PromptTemplateSet.BuiltIn(WithSlot);

        Assert.NotNull(set.FindSlot("test.slot"));
        Assert.Contains("{pr}", set.Slot("test.slot"), StringComparison.Ordinal);
    }
}
