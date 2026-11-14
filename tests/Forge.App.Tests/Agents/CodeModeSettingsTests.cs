using Forge.App.Agents;
using Forge.App.Preferences;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The four <c>run_code</code> dials of the tools section (wave W14): the visible-tool list parsing, the visibility
/// rule, the projection onto the proxy's execution bounds, and the read from the live preferences.
/// </summary>
public sealed class CodeModeSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; \n\t ")]
    public void A_blank_or_separator_only_list_parses_to_nothing(string? text)
    {
        Assert.Empty(CodeModeSettings.ParseTools(text));
    }

    [Fact]
    public void Every_separator_splits_and_entries_are_trimmed()
    {
        var parsed = CodeModeSettings.ParseTools("  read  , ls; find\ngrep\tbash web_fetch");

        Assert.Equal(["read", "ls", "find", "grep", "bash", "web_fetch"], parsed);
    }

    [Fact]
    public void Duplicates_are_dropped_ignoring_case_and_keep_the_first_spelling()
    {
        Assert.Equal(["Read"], CodeModeSettings.ParseTools("Read, READ, read"));
    }

    [Fact]
    public void An_empty_list_admits_every_tool()
    {
        var settings = new CodeModeSettings(8, 40_000, 20_000, []);

        Assert.True(settings.Allows("read"));
        Assert.True(settings.Allows("bash"));
        Assert.True(settings.Allows("mcp__server__tool"));
    }

    [Fact]
    public void An_exact_name_admits_its_tool_ignoring_case_but_no_other()
    {
        var settings = new CodeModeSettings(8, 40_000, 20_000, ["Grep"]);

        Assert.True(settings.Allows("grep"));
        Assert.True(settings.Allows("Grep"));
        Assert.False(settings.Allows("grepx"));

        // Without a wildcard there is no substring match: a prefix of a name admits nothing.
        Assert.False(new CodeModeSettings(8, 40_000, 20_000, ["gre"]).Allows("grep"));
    }

    [Fact]
    public void A_trailing_star_admits_its_prefix_and_nothing_merely_similar()
    {
        var settings = new CodeModeSettings(8, 40_000, 20_000, ["mcp__*"]);

        Assert.True(settings.Allows("mcp__a"));
        Assert.True(settings.Allows("mcp__server__tool"));
        Assert.False(settings.Allows("xmcp__a"));
    }

    [Fact]
    public void ToExecution_overrides_only_the_three_script_dials()
    {
        var execution = new CodeModeSettings(3, 1500, 700, ["read"]).ToExecution();
        var defaults = new CodeExecutionOptions();

        Assert.Equal(3, execution.MaxConcurrentToolCalls);
        Assert.Equal(1500, execution.MaxResultChars);
        Assert.Equal(700, execution.MaxInnerResultChars);
        Assert.Equal(defaults.MaxScriptChars, execution.MaxScriptChars);
        Assert.Equal(defaults.MaxLogChars, execution.MaxLogChars);
        Assert.Equal(defaults.MaxDiagnosticChars, execution.MaxDiagnosticChars);
        Assert.Equal(defaults.MaxDiagnosticCount, execution.MaxDiagnosticCount);
        Assert.Equal(defaults.CompilationCacheCapacity, execution.CompilationCacheCapacity);
    }

    [Fact]
    public void From_reads_the_four_code_script_preferences_and_reads_them_again_after_a_change()
    {
        var preferences = new AppPreferences
        {
            CodeScriptMaxConcurrentCalls = 5,
            CodeScriptResultChars = 2000,
            CodeScriptInnerResultChars = 600,
            CodeScriptTools = "read, mcp__*",
        };

        var settings = CodeModeSettings.From(preferences);
        Assert.Equal(5, settings.MaxConcurrentCalls);
        Assert.Equal(2000, settings.ResultChars);
        Assert.Equal(600, settings.InnerResultChars);
        Assert.Equal(["read", "mcp__*"], settings.VisibleTools);

        preferences.CodeScriptMaxConcurrentCalls = 9;
        preferences.CodeScriptTools = "grep";

        var again = CodeModeSettings.From(preferences);
        Assert.Equal(9, again.MaxConcurrentCalls);
        Assert.Equal(2000, again.ResultChars);
        Assert.Equal(600, again.InnerResultChars);
        Assert.Equal(["grep"], again.VisibleTools);
    }
}
