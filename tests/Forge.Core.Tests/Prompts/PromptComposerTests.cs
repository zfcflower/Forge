using Forge.Abstractions;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class PromptComposerTests
{
    private static readonly PromptLayers FullLayers = new("Project-specific system text.", "Global agent text.", "Project agent text.");

    [Fact]
    public void Compose_orders_layers_system_global_project_role()
    {
        var prompt = new PromptComposer().Compose(new PromptInput(FullLayers, "Role text.", null));

        var system = prompt.StablePrefix.IndexOf(PromptComposer.SystemHeading, StringComparison.Ordinal);
        var global = prompt.StablePrefix.IndexOf(PromptComposer.GlobalAgentHeading, StringComparison.Ordinal);
        var project = prompt.StablePrefix.IndexOf(PromptComposer.ProjectAgentHeading, StringComparison.Ordinal);
        var role = prompt.StablePrefix.IndexOf(PromptComposer.RoleHeading, StringComparison.Ordinal);

        Assert.True(system == 0);
        Assert.True(system < global);
        Assert.True(global < project);
        Assert.True(project < role);
        // PromptLayers.System is the whole system layer: it replaces the built-in text instead of extending it.
        Assert.DoesNotContain(BuiltInPrompts.System, prompt.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("You are a coding agent", prompt.StablePrefix, StringComparison.Ordinal);
        Assert.StartsWith(PromptComposer.SystemHeading + "\n\nProject-specific system text.\n\n", prompt.StablePrefix, StringComparison.Ordinal);
        Assert.EndsWith("Role text.", prompt.StablePrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_system_text_replaces_the_built_in_and_appears_exactly_once()
    {
        var prompt = new PromptComposer().Compose(new PromptInput(new PromptLayers("CUSTOM-SYSTEM-MARKER", null, null), null, null));

        Assert.Equal(PromptComposer.SystemHeading + "\n\nCUSTOM-SYSTEM-MARKER", prompt.StablePrefix);
        Assert.Equal(1, CountOf(prompt.StablePrefix, "CUSTOM-SYSTEM-MARKER"));
        Assert.Equal(1, CountOf(prompt.StablePrefix, PromptComposer.SystemHeading));
    }

    [Fact]
    public void The_two_built_in_system_texts_differ_only_in_who_they_say_is_asking()
    {
        // The claim decides which one is used; if they drifted apart in more than that name, a user toggling the claim would
        // silently change the agent's rules as well.
        Assert.StartsWith("You are a coding agent", BuiltInPrompts.System, StringComparison.Ordinal);
        Assert.StartsWith("You are Forge, a coding agent", BuiltInPrompts.SystemNamed, StringComparison.Ordinal);
        Assert.Equal(BuiltInPrompts.System, BuiltInPrompts.SystemNamed.Replace("Forge, ", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void The_built_in_text_passed_as_the_system_layer_is_not_doubled()
    {
        var composer = new PromptComposer();

        var explicitBuiltIn = composer.Compose(new PromptInput(new PromptLayers(BuiltInPrompts.System, null, null), null, null));
        var blank = composer.Compose(new PromptInput(new PromptLayers("", null, null), null, null));

        Assert.Equal(1, CountOf(explicitBuiltIn.StablePrefix, "You are a coding agent"));
        Assert.Equal(blank.StablePrefix, explicitBuiltIn.StablePrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t\r\n")]
    public void A_blank_system_layer_falls_back_to_the_built_in_text(string system)
    {
        var prompt = new PromptComposer().Compose(new PromptInput(new PromptLayers(system, null, null), null, null));

        Assert.StartsWith(PromptComposer.SystemHeading + "\n\n", prompt.StablePrefix, StringComparison.Ordinal);
        var body = prompt.StablePrefix[(PromptComposer.SystemHeading.Length + 2)..];
        Assert.Equal(BuiltInPrompts.System.ReplaceLineEndings("\n").Trim(), body);
    }

    [Fact]
    public void A_custom_system_layer_is_normalized_like_any_other_layer()
    {
        var prompt = new PromptComposer().Compose(new PromptInput(new PromptLayers("\r\nline one   \r\nline two\r\n\r\n", null, null), null, null));

        Assert.Equal(PromptComposer.SystemHeading + "\n\nline one\nline two", prompt.StablePrefix);
    }

    [Fact]
    public void Changing_the_system_layer_changes_the_prefix_key()
    {
        var composer = new PromptComposer();

        var builtIn = composer.Compose(new PromptInput(new PromptLayers("", null, null), null, null));
        var custom = composer.Compose(new PromptInput(new PromptLayers("custom", null, null), null, null));

        Assert.NotEqual(PromptComposer.PrefixKey(builtIn), PromptComposer.PrefixKey(custom));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Compose_skips_null_empty_and_whitespace_layers()
    {
        var composer = new PromptComposer();

        var bare = composer.Compose(new PromptInput(new PromptLayers("", null, "   "), null, null));
        var bareWithNewlines = composer.Compose(new PromptInput(new PromptLayers("\r\n\t\n", "", "\n"), "  \n ", null));

        Assert.DoesNotContain(PromptComposer.GlobalAgentHeading, bare.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptComposer.ProjectAgentHeading, bare.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptComposer.RoleHeading, bare.StablePrefix, StringComparison.Ordinal);
        Assert.Equal(bare.StablePrefix, bareWithNewlines.StablePrefix);
        Assert.StartsWith(PromptComposer.SystemHeading, bare.StablePrefix, StringComparison.Ordinal);
        Assert.StartsWith(PromptComposer.SystemHeading + "\n\n", bare.StablePrefix, StringComparison.Ordinal);
        Assert.Contains("You are a coding agent", bare.StablePrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_is_byte_identical_for_identical_inputs()
    {
        var composer = new PromptComposer();

        var first = composer.Compose(new PromptInput(FullLayers, "Role text.", "dynamic one"));
        var second = composer.Compose(new PromptInput(FullLayers, "Role text.", "dynamic two"));

        Assert.Equal(first.StablePrefix, second.StablePrefix);
        Assert.Equal(first.StablePrefix, new PromptComposer().Compose(new PromptInput(FullLayers, "Role text.", null)).StablePrefix);
        Assert.Equal(PromptComposer.PrefixKey(first), PromptComposer.PrefixKey(second));
    }

    [Fact]
    public void Compose_normalizes_crlf_and_trailing_whitespace()
    {
        var composer = new PromptComposer();
        var lf = composer.Compose(new PromptInput(new PromptLayers("line one\nline two\n", "global\n", "project"), "role", null));
        var crlf = composer.Compose(new PromptInput(
            new PromptLayers("line one\r\nline two\r\n\r\n", "global   \r\n", "project\r\n"),
            "role\t",
            null));

        Assert.Equal(lf.StablePrefix, crlf.StablePrefix);
        Assert.DoesNotContain('\r', crlf.StablePrefix);
        Assert.DoesNotContain("line   ", crlf.StablePrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", crlf.StablePrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_keeps_dynamic_context_out_of_the_prefix()
    {
        var dynamicText = "current time 2026-01-01; workspace has 3 modified files";
        var composer = new PromptComposer();

        var withDynamic = composer.Compose(new PromptInput(FullLayers, "Role text.", dynamicText));
        var withoutDynamic = composer.Compose(new PromptInput(FullLayers, "Role text.", null));

        Assert.DoesNotContain(dynamicText, withDynamic.StablePrefix, StringComparison.Ordinal);
        Assert.Equal(dynamicText, withDynamic.DynamicSuffix);
        Assert.Equal(string.Empty, withoutDynamic.DynamicSuffix);
        Assert.Equal(withoutDynamic.StablePrefix, withDynamic.StablePrefix);
        Assert.Equal(PromptComposer.PrefixKey(withoutDynamic), PromptComposer.PrefixKey(withDynamic));
        Assert.Equal(withDynamic.StablePrefix + "\n\n" + dynamicText, withDynamic.Full);
    }

    [Fact]
    public void Compose_whitespace_only_dynamic_context_becomes_empty()
    {
        var prompt = new PromptComposer().Compose(new PromptInput(new PromptLayers("system", null, null), null, " \r\n\t "));

        Assert.Equal(string.Empty, prompt.DynamicSuffix);
        Assert.Equal(prompt.StablePrefix, prompt.Full);
    }

    [Fact]
    public void PrefixKey_depends_only_on_the_prefix()
    {
        var composer = new PromptComposer();
        var first = composer.Compose(new PromptInput(FullLayers, "Role text.", "a"));
        var second = composer.Compose(new PromptInput(FullLayers, "Role text.", "b"));
        var different = composer.Compose(new PromptInput(FullLayers, "Other role.", "a"));

        Assert.Equal(
            PromptComposer.PrefixKey(first),
            PromptComposer.PrefixKey(new ComposedPrompt(first.StablePrefix, "unrelated suffix")));
        Assert.NotEqual(PromptComposer.PrefixKey(first), PromptComposer.PrefixKey(different));

        var key = PromptComposer.PrefixKey(first);
        Assert.Equal(32, key.Length);
        Assert.True(key.All(c => char.IsAsciiHexDigitLower(c)));
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.Equal(key, PromptComposer.PrefixKey(second));
    }

    [Fact]
    public void PrefixKey_handles_long_prefixes()
    {
        var text = new string('x', 4096);
        var composer = new PromptComposer();
        var prompt = composer.Compose(new PromptInput(new PromptLayers(text, text, text), text, null));

        var key = PromptComposer.PrefixKey(prompt);

        Assert.Equal(32, key.Length);
        Assert.Equal(key, PromptComposer.PrefixKey(prompt));
        Assert.NotEqual(key, PromptComposer.PrefixKey(prompt with { StablePrefix = prompt.StablePrefix + "y" }));
    }

    [Fact]
    public void Compose_rejects_missing_input()
    {
        Assert.Throws<ArgumentNullException>(() => new PromptComposer().Compose(null!));
    }
}
