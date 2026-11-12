using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>The composer's <c>/</c> and <c>@</c> pickers, the skill discovery behind them and the model list.</summary>
public sealed class ComposerSuggestionTests
{
    [Theory]
    [InlineData("/rev", 4, (int)ComposerTokenKind.Slash, 0, "rev")]
    [InlineData("please /re", 10, (int)ComposerTokenKind.Slash, 7, "re")]
    [InlineData("look at @src/Fo", 15, (int)ComposerTokenKind.Mention, 8, "src/Fo")]
    [InlineData("@", 1, (int)ComposerTokenKind.Mention, 0, "")]
    [InlineData("a/b", 3, (int)ComposerTokenKind.None, -1, "")]
    [InlineData("me@host", 7, (int)ComposerTokenKind.None, -1, "")]
    [InlineData("/done ", 6, (int)ComposerTokenKind.None, -1, "")]
    [InlineData("/a/b", 4, (int)ComposerTokenKind.None, -1, "")]
    public void The_token_before_the_caret_opens_the_matching_picker(string text, int caret, int kindValue, int start, string query)
    {
        var kind = (ComposerTokenKind)kindValue;
        var token = ComposerSuggestions.FindToken(text, caret);

        Assert.Equal(kind, token.Kind);
        if (kind != ComposerTokenKind.None)
        {
            Assert.Equal(start, token.Start);
            Assert.Equal(query, token.Query);
        }
    }

    [Fact]
    public void Slash_entries_filter_by_name_with_prefix_matches_first()
    {
        SlashEntry[] entries =
        [
            new("code-review", "review", true),
            new("review-pr", "pr", true),
            new("build", "build", true),
        ];

        var filtered = ComposerSuggestions.FilterSlash(entries, "rev");

        Assert.Equal(["review-pr", "code-review"], filtered.Select(entry => entry.Name));
        Assert.Equal(3, ComposerSuggestions.FilterSlash(entries, string.Empty).Count);
    }

    [Fact]
    public void Files_rank_name_prefix_then_name_then_path_then_fuzzy_and_are_capped()
    {
        string[] files =
        [
            "src/Forge.App/Views/SessionView.axaml",
            "docs/session-notes.md",
            "src/session/readme.md",
            "tests/SeXsXsion.txt",
            "other.txt",
        ];

        var filtered = ComposerSuggestions.FilterFiles(files, "session");

        Assert.Equal(
            ["docs/session-notes.md", "src/Forge.App/Views/SessionView.axaml", "src/session/readme.md", "tests/SeXsXsion.txt"],
            filtered);
        Assert.DoesNotContain("other.txt", ComposerSuggestions.FilterFiles(files, "sssion"));

        var many = Enumerable.Range(0, 200).Select(i => "dir/file" + i + ".cs");
        Assert.Equal(ComposerSuggestions.MaxFileResults, ComposerSuggestions.FilterFiles(many, "file").Count);
    }

    [Fact]
    public void Accepting_replaces_the_token_and_adds_one_space()
    {
        var token = ComposerSuggestions.FindToken("see @Ses and more", 8);

        var (text, caret) = ComposerSuggestions.Apply("see @Ses and more", token, "src/SessionView.axaml");

        Assert.Equal("see @src/SessionView.axaml and more", text);
        Assert.Equal("see @src/SessionView.axaml ".Length, caret);

        var slash = ComposerSuggestions.FindToken("/rev", 4);
        Assert.Equal(("/review-pr ", 11), ComposerSuggestions.Apply("/rev", slash, "review-pr"));
    }

    [Fact]
    public void Front_matter_gives_name_and_folded_description()
    {
        const string text = "---\nname: \"pdf-tools\"\ndescription: >-\n  Works with PDF\n  files.\nother: x\n---\n# body\n";

        var (name, description) = SkillCatalog.ParseFrontMatter(text);

        Assert.Equal("pdf-tools", name);
        Assert.Equal("Works with PDF files.", description);
        Assert.Equal((null, null), SkillCatalog.ParseFrontMatter("# no front matter"));
    }

    [Fact]
    public void Skills_are_found_under_the_workspace_and_user_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(root, "ws");
            var user = Path.Combine(root, "user");
            Write(Path.Combine(workspace, ".github", "skills", "alpha", "SKILL.md"), "---\nname: alpha-skill\ndescription: first\n---\n");
            Write(Path.Combine(workspace, ".forge", "skills", "beta", "SKILL.md"), "no front matter");
            Write(Path.Combine(user, "skills", "gamma", "SKILL.md"), "---\ndescription: from the user folder\n---\n");
            Directory.CreateDirectory(Path.Combine(user, "skills", "empty"));

            var skills = SkillCatalog.Discover(workspace, user);

            Assert.Equal(["alpha-skill", "beta", "gamma"], skills.Select(skill => skill.Name));
            Assert.Equal("first", skills[0].Description);
            Assert.Equal("from the user folder", skills[2].Description);
            Assert.All(skills, skill => Assert.True(skill.IsSkill));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_composer_picker_suggests_skills_and_files_and_inserts_the_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-composer-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var composer = NewComposer(root, out _, out var holder);
            using (holder)
            {
                composer.UpdateSuggestions("/be", 3);
                Assert.True(composer.IsSuggestionOpen);
                Assert.Equal(["/beta"], composer.Suggestions.Select(item => item.Title));
                Assert.Equal(("/beta ", 6), composer.Accept());
                Assert.False(composer.IsSuggestionOpen);

                composer.UpdateSuggestions("@", 1);
                Assert.True(composer.IsSuggestionOpen);
                Assert.Equal(2, composer.Suggestions.Count);
                composer.MoveSelection(1);
                Assert.Equal(1, composer.SelectedSuggestionIndex);
                composer.MoveSelection(1);
                Assert.Equal(0, composer.SelectedSuggestionIndex);

                composer.UpdateSuggestions("@view", 5);
                Assert.Equal(("@src/View.axaml ", 16), composer.Accept());

                // Escape keeps the token closed until the caret leaves it.
                composer.UpdateSuggestions("/a", 2);
                Assert.True(composer.IsSuggestionOpen);
                composer.CloseSuggestions(dismissed: true);
                composer.UpdateSuggestions("/al", 3);
                Assert.False(composer.IsSuggestionOpen);
                composer.UpdateSuggestions("/al x /a", 8);
                Assert.True(composer.IsSuggestionOpen);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Models_list_the_configured_and_extra_models_with_pinned_first()
    {
        // Rewritten for multi-provider: the old single-provider build sorted the default model first even when it
        // was listed second; the new catalogue keeps the stored order (trimmed), so the fixture lists the default
        // first explicitly. Trimming (" model-c ") is still verified.
        var root = Path.Combine(Path.GetTempPath(), "forge-composer-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var composer = NewComposer(root, out var preferences, out var services);
            using var _ = services;

            Assert.Equal(["model-a", "model-b", "model-c"], composer.Models.Select(model => model.Name));
            Assert.True(composer.Models[0].IsSelected);

            composer.Models[2].TogglePinCommand.Execute(null);
            Assert.Equal(["model-c", "model-a", "model-b"], composer.Models.Select(model => model.Name));
            Assert.True(preferences.IsPinned("model-c"));

            composer.ModelSearch = "B";
            Assert.Equal(["model-b"], composer.Models.Select(model => model.Name));
            composer.Models[0].SelectCommand.Execute(null);
            Assert.Equal("model-b", composer.Model);
            Assert.Equal("model-b", composer.CurrentDefinition()?.Model.Model);

            composer.EffortValue = (double)ThinkingEffort.Medium;
            Assert.Equal(ThinkingEffort.Medium, composer.Effort);
            composer.SaveDefaultsCommand.Execute(null);
            Assert.Equal("model-b", preferences.DefaultModel);
            Assert.Equal(ThinkingEffort.Medium, preferences.DefaultEffort);

            // A new session starts from the saved defaults.
            var next = new ComposerViewModel(
                services.Services,
                _ => { },
                preferences,
                string.Empty,
                skillSource: () => [],
                thinkingLevels: static _ => EveryStep);
            Assert.Equal("model-b", next.Model);
            Assert.Equal(ThinkingEffort.Medium, next.Effort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Every step, for a fixture whose model ids are not in the model table.</summary>
    private static readonly IReadOnlyList<ThinkingEffort> EveryStep =
        [ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh, ThinkingEffort.Max];

    private static ComposerViewModel NewComposer(string root, out ComposerPreferences preferences, out TestServices services)
    {
        preferences = new ComposerPreferences();
        services = TestServices.Create(root);
        return new ComposerViewModel(
            services.Services,
            _ => { },
            preferences,
            string.Empty,
            fileSource: _ => Task.FromResult<IReadOnlyList<string>>(["src/View.axaml", "readme.md"]),
            skillSource: () => [new SlashEntry("alpha", "a", true), new SlashEntry("beta", "b", true)],
            // The fixture's model ids are invented, so the composer is given the steps to offer instead of asking the
            // model table (which knows no "model-b" and would offer Off alone).
            thinkingLevels: static _ => EveryStep);
    }

    /// <summary>One connected provider behind a temp <c>settings.json</c>; owns its <c>HttpClient</c> for the test.</summary>
    private sealed class TestServices : IDisposable
    {
        private TestServices(ProviderServices inner, HttpClient http) { Services = inner; Http = http; }

        public ProviderServices Services { get; }

        private HttpClient Http { get; }

        public static TestServices Create(string root)
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(
                settingsPath,
                """
                {
                  "providers": {
                    "version": 1,
                    "defaultId": "test",
                    "items": [
                      {
                        "id": "test",
                        "preset": "anthropic",
                        "name": "Test",
                        "protocol": "anthropic",
                        "baseUrl": "https://example.invalid",
                        "auth": { "kind": "api-key" },
                        "models": ["model-a", "model-b", " model-c "],
                        "defaultModel": "model-a"
                      }
                    ]
                  }
                }
                """);
            var dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var http = new HttpClient(new ThrowingHandler());
            return new TestServices(new ProviderServices(settingsPath, dataDir, http), http);
        }

        public void Dispose() => Http.Dispose();

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("no network in this test");
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
