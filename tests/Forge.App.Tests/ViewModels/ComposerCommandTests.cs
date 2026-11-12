using Forge.Abstractions;
using Forge.App.Prompts;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>Slash commands and user agents from the prompt templates, as the composer shows and expands them.</summary>
public sealed class ComposerCommandTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-composer-cmd-");
    private readonly string userDir;
    private readonly ComposerSuggestionTests.TestServices services;

    public ComposerCommandTests()
    {
        Directory.CreateDirectory(root);
        userDir = Path.Combine(root, "user");
        Directory.CreateDirectory(userDir);
        services = ComposerSuggestionTests.TestServices.Create(root);
    }

    public void Dispose()
    {
        services.Dispose();
        Directory.Delete(root, recursive: true);
    }

    private ComposerViewModel NewComposer(params SlashEntry[] skills) =>
        new(
            services.Services,
            _ => { },
            new ComposerPreferences(),
            string.Empty,
            skillSource: () => skills,
            thinkingLevels: static (_, _) => ComposerSuggestionTests.EveryStep,
            templateSource: () => new PromptTemplateService(userDir).Resolve(null));

    private void WriteUser(string relative, string text)
    {
        var path = Path.Combine(userDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string[] SlashNames(ComposerViewModel composer, string text)
    {
        composer.UpdateSuggestions(text, text.Length);
        return composer.Suggestions.Select(item => item.Value).ToArray();
    }

    [Fact]
    public void Commands_come_before_skills_and_a_skill_with_a_command_name_is_hidden()
    {
        WriteUser("commands/zeta.md", "---\ndescription: Z command\nargument-hint: <x>\n---\nDo $ARGUMENTS");
        WriteUser("commands/dup.md", "---\ndescription: dup command\n---\nbody");
        var composer = NewComposer(new SlashEntry("alpha", "skill a", true), new SlashEntry("dup", "skill dup", true));

        composer.UpdateSuggestions("/", 1);
        var items = composer.Suggestions.ToList();

        var names = items.Select(item => item.Value).ToList();
        Assert.Equal(1, names.Count(name => name == "dup"));
        Assert.Equal("dup command", items.First(item => item.Value == "dup").Detail);
        var zeta = items.First(item => item.Value == "zeta");
        Assert.Contains("<x>", zeta.Detail);
        Assert.Contains("Z command", zeta.Detail);
        var lastCommand = Math.Max(names.IndexOf("zeta"), names.IndexOf("dup"));
        Assert.True(names.IndexOf("alpha") > lastCommand, "skills follow commands");
    }

    [Fact]
    public void A_command_expands_with_its_arguments_and_other_text_is_left_alone()
    {
        WriteUser("commands/fix.md", "Fix this: $ARGUMENTS (first: $1)");
        var composer = NewComposer();

        Assert.Equal("Fix this: the bug now (first: the)", composer.ExpandCommand("/fix the bug now"));
        Assert.Equal("/nosuchcommand a b", composer.ExpandCommand("/nosuchcommand a b"));
        Assert.Equal("/usr/bin/env x", composer.ExpandCommand("/usr/bin/env x"));
        Assert.Equal("plain text", composer.ExpandCommand("plain text"));
        Assert.Equal(string.Empty, composer.ExpandCommand(string.Empty));
    }

    [Fact]
    public void A_user_agent_file_appears_after_the_built_ins_in_the_picker()
    {
        var baseline = NewComposer().Agents.Select(agent => agent.Name).ToList();
        WriteUser("agents/helper.md", "---\ndescription: A helper\n---\nYou help.");

        var agents = NewComposer().Agents;

        Assert.Null(agents[0].Definition);
        Assert.Equal("helper", agents[^1].Name);
        Assert.Equal(baseline.Count + 1, agents.Count);
        Assert.Equal(baseline, agents.Take(baseline.Count).Select(agent => agent.Name));
        Assert.Contains(agents, agent => agent.Key == "scout" || agent.Definition is not null);
    }

    [Fact]
    public void A_new_slash_token_picks_up_a_command_added_since_the_last_one()
    {
        var composer = NewComposer();
        Assert.DoesNotContain("later", SlashNames(composer, "/"));

        WriteUser("commands/later.md", "---\ndescription: added later\n---\nbody");
        composer.CloseSuggestions();
        composer.UpdateSuggestions("", 0);

        Assert.Contains("later", SlashNames(composer, "/"));
        Assert.Equal("body", composer.ExpandCommand("/later"));
    }
}
