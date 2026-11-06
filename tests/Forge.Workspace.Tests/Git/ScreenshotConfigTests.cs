using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The pure parts of the screenshot step that need no repository: <c>.forge/screenshots.json</c>, the GitHub address parser, the
/// assets branch name, the <c>## Screenshots</c> table and the user-chosen files.
/// </summary>
public sealed class ScreenshotConfigTests
{
    private static ScreenshotConfig Valid(string json)
    {
        var result = ScreenshotConfigLoader.Parse(json);
        Assert.True(result.State == ScreenshotConfigState.Valid, result.Error);
        Assert.Null(result.Error);
        return Assert.IsType<ScreenshotConfig>(result.Config);
    }

    private static string Invalid(string json)
    {
        var result = ScreenshotConfigLoader.Parse(json);
        Assert.Equal(ScreenshotConfigState.Invalid, result.State);
        Assert.Null(result.Config);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void Parse_accepts_command_and_output_and_defaults_the_timeout_to_five_minutes()
    {
        var config = Valid("""{"command":"npm run shots","output":"shots"}""");

        Assert.Equal("npm run shots", config.Command);
        Assert.Equal("shots", config.OutputDirectory);
        Assert.Equal(TimeSpan.FromSeconds(300), config.Timeout);
    }

    [Fact]
    public void Parse_trims_the_command_and_normalizes_the_output_directory()
    {
        var config = Valid("""{"command":"  dotnet test --filter Shots  ","output":".\\build\\\\shots\\","timeoutSeconds":45}""");

        Assert.Equal("dotnet test --filter Shots", config.Command);
        Assert.Equal("build/shots", config.OutputDirectory);
        Assert.Equal(TimeSpan.FromSeconds(45), config.Timeout);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(300)]
    [InlineData(3600)]
    public void Parse_accepts_the_whole_timeout_range(int seconds)
    {
        var config = Valid($$"""{"command":"x","output":"o","timeoutSeconds":{{seconds}}}""");

        Assert.Equal(TimeSpan.FromSeconds(seconds), config.Timeout);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    [InlineData(1000000)]
    public void Parse_rejects_a_timeout_outside_ten_to_thirty_six_hundred_seconds(int seconds)
    {
        var error = Invalid($$"""{"command":"x","output":"o","timeoutSeconds":{{seconds}}}""");

        Assert.Contains("timeoutSeconds", error, StringComparison.Ordinal);
        Assert.Contains("between 10 and 3600", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("99999999999")]
    public void Parse_rejects_a_timeout_that_is_not_a_whole_number(string value)
    {
        var error = Invalid($$"""{"command":"x","output":"o","timeoutSeconds":{{value}}}""");

        Assert.Contains("whole number", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"output":"o"}""")]
    [InlineData("""{"command":"","output":"o"}""")]
    [InlineData("""{"command":"   ","output":"o"}""")]
    public void Parse_requires_a_non_blank_command(string json)
    {
        Assert.Contains("\"command\" is required", Invalid(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"command":5,"output":"o"}""", "\"command\" must be a string")]
    [InlineData("""{"command":["a"],"output":"o"}""", "\"command\" must be a string")]
    [InlineData("""{"command":"x","output":5}""", "\"output\" must be a string")]
    [InlineData("""{"command":"x","output":null}""", "\"output\" must be a string")]
    public void Parse_rejects_values_of_the_wrong_type(string json, string expected)
    {
        Assert.Contains(expected, Invalid(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"command":"x"}""")]
    [InlineData("""{"command":"x","output":""}""")]
    [InlineData("""{"command":"x","output":"  "}""")]
    public void Parse_requires_an_output_directory(string json)
    {
        Assert.Contains("\"output\" is required", Invalid(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/abs/shots")]
    [InlineData("\\\\server\\share\\shots")]
    [InlineData("C:/shots")]
    [InlineData("C:\\\\shots")]
    [InlineData("../shots")]
    [InlineData("a/../../shots")]
    [InlineData("a/..")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".git")]
    [InlineData(".GIT/shots")]
    [InlineData("shots.")]
    [InlineData("a /shots")]
    public void Parse_refuses_an_output_directory_outside_the_repository(string output)
    {
        var error = Invalid($$"""{"command":"x","output":"{{System.Text.Json.JsonEncodedText.Encode(output)}}"}""");

        Assert.Contains("must be a directory inside the repository", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_names_an_unknown_property_instead_of_ignoring_a_misspelling()
    {
        var error = Invalid("""{"command":"x","output":"o","timeout":30}""");

        Assert.Contains("unknown property \"timeout\"", error, StringComparison.Ordinal);
        Assert.Contains("timeoutSeconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_is_case_sensitive_about_property_names()
    {
        Assert.Contains("unknown property \"Command\"", Invalid("""{"Command":"x","output":"o"}"""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"command\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Parse_requires_a_json_object(string json)
    {
        Assert.Contains("must be a JSON object", Invalid(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"command\":\"x\"")]
    [InlineData("not json")]
    public void Parse_reports_malformed_json_as_a_result(string json)
    {
        Assert.Contains("not valid JSON", Invalid(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_tolerates_comments_and_trailing_commas()
    {
        var config = Valid(
            """
            {
              // how the project takes its screenshots
              "command": "npm run shots", /* headless */
              "output": "shots",
            }
            """);

        Assert.Equal("npm run shots", config.Command);
    }

    [Fact]
    public void Parse_refuses_a_command_that_is_too_long_to_be_a_command()
    {
        var error = Invalid($$"""{"command":"{{new string('a', 9000)}}","output":"o"}""");

        Assert.Contains("longer than", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_returns_missing_when_the_project_declares_nothing()
    {
        using var temp = new TempDirectory();

        var result = ScreenshotConfigLoader.Load(temp.Path);

        Assert.Equal(ScreenshotConfigState.Missing, result.State);
        Assert.Null(result.Config);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Load_reads_the_declaration_from_the_worktree()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".forge"));
        File.WriteAllText(temp.Combine(".forge", "screenshots.json"), """{"command":"go","output":"out","timeoutSeconds":20}""", new UTF8Encoding(false));

        var result = ScreenshotConfigLoader.Load(temp.Path);

        Assert.Equal(ScreenshotConfigState.Valid, result.State);
        Assert.Equal(new ScreenshotConfig("go", "out", TimeSpan.FromSeconds(20)), result.Config);
    }

    [Fact]
    public void Load_reports_a_broken_file_with_its_path_in_the_message()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".forge"));
        File.WriteAllText(temp.Combine(".forge", "screenshots.json"), "{ nope", new UTF8Encoding(false));

        var result = ScreenshotConfigLoader.Load(temp.Path);

        Assert.Equal(ScreenshotConfigState.Invalid, result.State);
        Assert.Contains(".forge/screenshots.json", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_refuses_a_file_that_is_too_large_without_parsing_it()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine(".forge"));
        File.WriteAllText(temp.Combine(".forge", "screenshots.json"), new string(' ', 70 * 1024), new UTF8Encoding(false));

        var result = ScreenshotConfigLoader.Load(temp.Path);

        Assert.Equal(ScreenshotConfigState.Invalid, result.State);
        Assert.Contains("larger than 64 KiB", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("https://github.com/acme/forge", "github.com", "acme", "forge")]
    [InlineData("https://github.com/acme/forge/", "github.com", "acme", "forge")]
    [InlineData("http://github.com/acme/forge", "github.com", "acme", "forge")]
    [InlineData("https://user:ghp_secret@github.com/acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("git@github.com:acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("github.com:acme/forge", "github.com", "acme", "forge")]
    [InlineData("ssh://git@github.com/acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("ssh://git@github.com:22/acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("git://github.com/acme/forge.git", "github.com", "acme", "forge")]
    [InlineData("https://ghe.example.com/team/app.git", "ghe.example.com", "team", "app")]
    [InlineData("  https://github.com/acme/forge.git\n", "github.com", "acme", "forge")]
    [InlineData("https://github.com/Acme/my.repo_name-2.git", "github.com", "Acme", "my.repo_name-2")]
    public void GitHubRepository_parses_https_ssh_and_scp_like_addresses(string address, string host, string owner, string name)
    {
        Assert.Equal(new GitHubRepository(host, owner, name), GitHubRepository.Parse(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:/repos/forge.git")]
    [InlineData("C:\\repos\\forge")]
    [InlineData("/srv/git/forge.git")]
    [InlineData("./forge")]
    [InlineData("../forge.git")]
    [InlineData("~/forge")]
    [InlineData("file:///srv/git/acme/forge.git")]
    [InlineData("ftp://github.com/acme/forge")]
    [InlineData("https://github.com/forge.git")]
    [InlineData("https://gitlab.com/group/sub/repo.git")]
    [InlineData("git@github.com:forge.git")]
    [InlineData("https://github.com/acme/forge name")]
    [InlineData("https://github.com/acme/%2e%2e")]
    [InlineData("https://github.com/acme/for;ge")]
    [InlineData("https://git hub.com/acme/forge")]
    public void GitHubRepository_refuses_what_is_not_a_two_segment_github_address(string? address)
    {
        Assert.Null(GitHubRepository.Parse(address));
    }

    [Fact]
    public void GitHubRepository_builds_raw_image_links_pinned_to_a_commit()
    {
        var repository = new GitHubRepository("github.com", "acme", "forge");
        var sha = new string('a', 40);

        Assert.Equal(
            $"https://github.com/acme/forge/blob/{sha}/home-before.png?raw=true",
            repository.RawImageUrl(sha, "home-before.png"));
        Assert.Equal(
            $"https://github.com/acme/forge/blob/{sha}/a%20b%23.png?raw=true",
            repository.RawImageUrl(sha, "a b#.png"));
    }

    [Theory]
    [InlineData("forge/feature", "assets/forge-feature")]
    [InlineData("feature/Add UI!", "assets/feature-Add-UI")]
    [InlineData("user_name/fix-123", "assets/user_name-fix-123")]
    [InlineData("a//b---c", "assets/a-b-c")]
    [InlineData("-leading-and-trailing-", "assets/leading-and-trailing")]
    [InlineData("///", "assets/branch")]
    [InlineData("ünï/cödé", "assets/n-c-d")]
    public void AssetsBranchFor_keeps_letters_digits_and_underscores(string branch, string expected)
    {
        Assert.Equal(expected, ScreenshotPublisher.AssetsBranchFor(branch));
    }

    [Fact]
    public void AssetsBranchFor_limits_the_length_and_never_ends_with_a_separator()
    {
        var slug = ScreenshotPublisher.AssetsBranchFor(new string('x', 79) + "/" + new string('y', 50));

        Assert.StartsWith("assets/", slug, StringComparison.Ordinal);
        Assert.Equal("assets/".Length + 79, slug.Length);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void AssetsBranchFor_rejects_a_blank_branch()
    {
        Assert.ThrowsAny<ArgumentException>(() => ScreenshotPublisher.AssetsBranchFor(" "));
    }

    [Fact]
    public void Markdown_renders_a_table_with_one_row_per_screen_and_links_pinned_to_the_commit()
    {
        var sha = new string('b', 40);
        var repository = new GitHubRepository("github.com", "acme", "forge");
        var pairs = new[]
        {
            new ScreenshotPair("home", "/tmp/a.png", "/tmp/b.png"),
            new ScreenshotPair("settings", null, "/tmp/c.png"),
            new ScreenshotPair("legacy", "/tmp/d.png", null),
        };

        var markdown = ScreenshotMarkdown.Render(pairs, repository, sha, "Before: `origin/main`.");

        var expected =
            "| Screen | Before | After |\n" +
            "| --- | --- | --- |\n" +
            $"| home | ![home before](https://github.com/acme/forge/blob/{sha}/home-before.png?raw=true) | ![home after](https://github.com/acme/forge/blob/{sha}/home-after.png?raw=true) |\n" +
            $"| settings | _no image on the base_ | ![settings after](https://github.com/acme/forge/blob/{sha}/settings-after.png?raw=true) |\n" +
            $"| legacy | ![legacy before](https://github.com/acme/forge/blob/{sha}/legacy-before.png?raw=true) | _no image after the change_ |\n" +
            "\nBefore: `origin/main`.\n";
        Assert.Equal(expected, markdown);
    }

    [Fact]
    public void Markdown_without_a_caption_ends_with_the_last_row()
    {
        var markdown = ScreenshotMarkdown.Render(
            [new ScreenshotPair("home", "/a.png", "/b.png")],
            new GitHubRepository("github.com", "acme", "forge"),
            new string('c', 40));

        Assert.EndsWith("raw=true) |\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_rejects_missing_arguments()
    {
        var repository = new GitHubRepository("github.com", "acme", "forge");

        Assert.Throws<ArgumentNullException>(() => ScreenshotMarkdown.Render(null!, repository, "abc"));
        Assert.Throws<ArgumentNullException>(() => ScreenshotMarkdown.Render([], null!, "abc"));
        Assert.ThrowsAny<ArgumentException>(() => ScreenshotMarkdown.Render([], repository, " "));
    }

    [Fact]
    public void Files_pair_before_and_after_images_and_show_any_other_png_as_an_after_image()
    {
        using var temp = new TempDirectory();
        Png(temp, "home-before.png", "hb");
        Png(temp, "home-after.png", "ha");
        Png(temp, "settings.png", "s");
        Png(temp, "Login-BEFORE.PNG", "lb");

        var result = ScreenshotFiles.Build(
            temp.Path,
            ["settings.png", temp.Combine("home-after.png"), "home-before.png", "Login-BEFORE.PNG"]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["home", "Login", "settings"], result.Pairs.Select(static pair => pair.Name).ToArray());

        var home = result.Pairs[0];
        Assert.True(TestPaths.Same(temp.Combine("home-before.png"), home.BeforePath!));
        Assert.True(TestPaths.Same(temp.Combine("home-after.png"), home.AfterPath!));

        var login = result.Pairs[1];
        Assert.NotNull(login.BeforePath);
        Assert.Null(login.AfterPath);

        var settings = result.Pairs[2];
        Assert.Null(settings.BeforePath);
        Assert.True(TestPaths.Same(temp.Combine("settings.png"), settings.AfterPath!));
    }

    [Fact]
    public void Files_make_the_name_safe_for_a_tree_entry_and_a_link()
    {
        using var temp = new TempDirectory();
        Png(temp, "my screen (1).png", "x");

        var result = ScreenshotFiles.Build(temp.Path, ["my screen (1).png"]);

        Assert.True(result.Success, result.Error);
        Assert.Equal("my_screen__1_", Assert.Single(result.Pairs).Name);
    }

    [Fact]
    public void Files_refuse_an_empty_list_and_too_many_files()
    {
        using var temp = new TempDirectory();

        Assert.Equal("No files were given.", ScreenshotFiles.Build(temp.Path, []).Error);

        var many = Enumerable.Range(0, ScreenshotFiles.MaxFiles + 1).Select(static i => $"s{i}.png").ToArray();
        Assert.Contains("at most 80", ScreenshotFiles.Build(temp.Path, many).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_refuse_a_missing_file_a_wrong_extension_and_a_wrong_signature()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("notes.txt"), "text");
        File.WriteAllText(temp.Combine("fake.png"), "this is not a png at all");
        File.WriteAllBytes(temp.Combine("short.png"), [0x89, 0x50]);

        Assert.Contains("is not an existing file", ScreenshotFiles.Build(temp.Path, ["nope.png"]).Error, StringComparison.Ordinal);
        Assert.Contains("not a .png file", ScreenshotFiles.Build(temp.Path, ["notes.txt"]).Error, StringComparison.Ordinal);
        Assert.Contains("wrong file signature", ScreenshotFiles.Build(temp.Path, ["fake.png"]).Error, StringComparison.Ordinal);
        Assert.Contains("wrong file signature", ScreenshotFiles.Build(temp.Path, ["short.png"]).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_refuse_an_image_over_ten_mebibytes()
    {
        using var temp = new TempDirectory();
        using (var stream = new FileStream(temp.Combine("huge.png"), FileMode.Create))
        {
            stream.Write(ScreenshotTestData.Png("huge"));
            stream.SetLength(ScreenshotCapture.MaxImageBytes + 1);
        }

        var result = ScreenshotFiles.Build(temp.Path, ["huge.png"]);

        Assert.False(result.Success);
        Assert.Empty(result.Pairs);
        Assert.Contains("larger than 10 MiB", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_refuse_two_files_for_the_same_side_of_one_screen()
    {
        using var temp = new TempDirectory();
        Png(temp, "home.png", "a");
        Png(temp, "home-after.png", "b");
        Png(temp, "x-before.png", "c");
        Directory.CreateDirectory(temp.Combine("other"));
        File.WriteAllBytes(temp.Combine("other", "x-before.png"), ScreenshotTestData.Png("d"));

        var after = ScreenshotFiles.Build(temp.Path, ["home.png", "home-after.png"]);
        var before = ScreenshotFiles.Build(temp.Path, ["x-before.png", "other/x-before.png"]);

        Assert.Contains("Two files map to the after image of 'home'", after.Error, StringComparison.Ordinal);
        Assert.Contains("Two files map to the before image of 'x'", before.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b.png")]
    public void Files_refuse_an_empty_or_malformed_path(string path)
    {
        using var temp = new TempDirectory();

        var result = ScreenshotFiles.Build(temp.Path, [path]);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error, StringComparison.Ordinal);
    }

    private static void Png(TempDirectory temp, string name, string tag) =>
        File.WriteAllBytes(temp.Combine(name), ScreenshotTestData.Png(tag));
}
