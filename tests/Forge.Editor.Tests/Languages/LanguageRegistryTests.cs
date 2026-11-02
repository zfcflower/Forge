using System.Text.Json.Nodes;
using Forge.Editor.Languages;
using Forge.Editor.Tests.Support;

namespace Forge.Editor.Tests.Languages;

public class LanguageRegistryTests
{
    [Fact]
    public void Built_in_defaults_describe_csharp_only()
    {
        var registry = LanguageRegistry.Default;

        Assert.Equal(new[] { "csharp" }, registry.Languages.Select(l => l.Id));
        Assert.Equal("roslyn-language-server", registry.GetById("csharp")!.Server!.Command);
        Assert.Equal(new[] { "--stdio" }, registry.GetById("csharp")!.Server!.Arguments);
        Assert.Equal("C#", registry.GetById("csharp")!.DisplayName);
        Assert.Empty(registry.LoadErrors);
        Assert.Null(registry.GetById(null));
        Assert.Null(registry.GetById(string.Empty));
        Assert.Null(registry.GetById("python"));
    }

    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("script.csx", "csharp")]
    [InlineData("LOUD.CS", "csharp")]
    [InlineData("/work/src/deep/Thing.cs", "csharp")]
    [InlineData("readme.md", null)]
    [InlineData("noextension", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Default_detection_uses_extensions_case_insensitively(string? path, string? expected) =>
        Assert.Equal(expected, LanguageRegistry.Default.DetectLanguageId(path));

    [Fact]
    public async Task Absent_config_files_leave_the_defaults_untouched()
    {
        using var dir = new TempDirectory();

        var registry = await LanguageRegistry.LoadAsync(dir.Root, dir.PathOf("missing.json"));

        Assert.Single(registry.Languages);
        Assert.Empty(registry.LoadErrors);
        Assert.Equal("csharp", registry.DetectLanguageId("a.cs"));
    }

    [Fact]
    public async Task Global_config_adds_languages_and_project_config_overrides_fields_per_language()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("user.json", """
            {"languages":{
              "csharp":{"server":{"command":"custom-csharp","args":["--trace"],"env":{"KEY":"VALUE"}}},
              "toml":{"extensions":[".toml"],"filenames":["Cargo.lock"],"displayName":"TOML"}
            }}
            """);
        var workspace = Path.Combine(dir.Root, "project");
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
        File.WriteAllText(Path.Combine(workspace, ".forge", "languages.json"), """
            {"csharp":{"displayName":"Project C#"},"toml":{"extensions":[".tml"]}}
            """);

        var registry = await LanguageRegistry.LoadAsync(workspace, user);

        var csharp = registry.GetById("csharp")!;
        Assert.Equal("custom-csharp", csharp.Server!.Command);
        Assert.Equal(new[] { "--trace" }, csharp.Server.Arguments);
        Assert.Equal("VALUE", csharp.Server.Environment["KEY"]);
        Assert.Equal("Project C#", csharp.DisplayName);
        Assert.Equal(new[] { ".cs", ".csx" }, csharp.Extensions);

        var toml = registry.GetById("toml")!;
        Assert.Equal(new[] { ".tml" }, toml.Extensions);
        Assert.Equal(new[] { "Cargo.lock" }, toml.FileNames);
        Assert.Null(toml.Server);
        Assert.Equal("toml", registry.DetectLanguageId("a.tml"));
        Assert.Null(registry.DetectLanguageId("a.toml"));
        Assert.Equal("toml", registry.DetectLanguageId("/x/Cargo.lock"));
        Assert.Equal(Path.Combine(workspace, ".forge", "languages.json"), LanguageRegistry.WorkspaceConfigPath(workspace));
    }

    [Fact]
    public async Task Disabled_language_is_removed_and_no_longer_detected()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("off.json", """{"languages":{"csharp":{"disabled":true}}}""");

        var registry = await LanguageRegistry.LoadAsync(null, user);

        Assert.Empty(registry.Languages);
        Assert.Null(registry.GetById("csharp"));
        Assert.Null(registry.DetectByPath("Program.cs"));
        Assert.Empty(registry.LoadErrors);
    }

    [Fact]
    public async Task Broken_json_is_reported_as_a_load_error_not_an_exception()
    {
        using var dir = new TempDirectory();
        var broken = dir.WriteText("broken.json", "{\"languages\": { \"csharp\": ");

        var registry = await LanguageRegistry.LoadAsync(null, broken);

        var error = Assert.Single(registry.LoadErrors);
        Assert.StartsWith(broken, error, StringComparison.Ordinal);
        Assert.NotNull(registry.GetById("csharp"));
    }

    [Fact]
    public async Task Broken_workspace_config_does_not_hide_the_user_config()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("ok.json", """{"gleam":{"extensions":[".gleam"]}}""");
        var workspace = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
        File.WriteAllText(Path.Combine(workspace, ".forge", "languages.json"), "[1,");

        var registry = await LanguageRegistry.LoadAsync(workspace, user);

        Assert.Equal("gleam", registry.DetectLanguageId("main.gleam"));
        Assert.Single(registry.LoadErrors);
    }

    [Theory]
    [InlineData("[1,2,3]", "The configuration root must be a JSON object.")]
    [InlineData("\"text\"", "The configuration root must be a JSON object.")]
    [InlineData("{\"go\":\"not an object\"}", "Language 'go' must be a JSON object.")]
    public void Structural_mistakes_are_collected_as_errors(string json, string expected)
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        LanguageRegistry.ApplyJson(JsonNode.Parse(json), languages, errors);

        Assert.Equal(expected, Assert.Single(errors));
        Assert.Empty(languages);
    }

    [Fact]
    public void Invalid_server_section_keeps_the_existing_server()
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = LanguageRegistry.DefaultCSharp,
        };
        var errors = new List<string>();

        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"csharp":{"server":{"command":"   "}}}"""), languages, errors);

        Assert.Equal("Language 'csharp' has an invalid server section; the existing server is kept.", Assert.Single(errors));
        Assert.Equal("roslyn-language-server", languages["csharp"].Server!.Command);
    }

    [Fact]
    public void Missing_server_property_keeps_the_server_and_null_removes_it()
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = LanguageRegistry.DefaultCSharp,
        };
        var errors = new List<string>();

        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"csharp":{"displayName":"Renamed"}}"""), languages, errors);
        Assert.NotNull(languages["csharp"].Server);
        Assert.Equal("Renamed", languages["csharp"].DisplayName);

        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"csharp":{"server":null}}"""), languages, errors);
        Assert.Null(languages["csharp"].Server);
        Assert.Empty(errors);
    }

    [Fact]
    public void Non_array_extension_lists_fall_back_to_the_existing_ones()
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = LanguageRegistry.DefaultCSharp,
        };
        var errors = new List<string>();

        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"csharp":{"extensions":".cs","filenames":42}}"""), languages, errors);

        Assert.Equal(new[] { ".cs", ".csx" }, languages["csharp"].Extensions);
        Assert.Empty(languages["csharp"].FileNames);
        Assert.Empty(errors);
    }

    [Fact]
    public void Languages_nested_under_a_languages_key_and_at_the_root_are_both_accepted()
    {
        var languages = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"languages":{"python":{"extensions":[".py"]}}}"""), languages, errors);
        LanguageRegistry.ApplyJson(JsonNode.Parse("""{"ruby":{"extensions":[".rb"],"server":{"command":"solargraph","args":["stdio"]}}}"""), languages, errors);

        Assert.Equal(new[] { "python", "ruby" }, languages.Keys.Order());
        Assert.Equal("solargraph", languages["ruby"].Server!.Command);
        Assert.Equal(new[] { "stdio" }, languages["ruby"].Server!.Arguments);
        Assert.Null(languages["python"].Server);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Longest_extension_wins_over_a_shorter_suffix()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("ext.json", """{"gzip":{"extensions":[".gz"]},"tarball":{"extensions":[".tar.gz"]}}""");

        var registry = await LanguageRegistry.LoadAsync(null, user);

        Assert.Equal("tarball", registry.DetectLanguageId("release.tar.gz"));
        Assert.Equal("gzip", registry.DetectLanguageId("notes.gz"));
    }

    [Fact]
    public async Task Exact_file_names_are_matched_case_insensitively()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("names.json", """{"make":{"filenames":["Makefile"]}}""");

        var registry = await LanguageRegistry.LoadAsync(null, user);

        Assert.Equal("make", registry.DetectLanguageId("/src/Makefile"));
        Assert.Equal("make", registry.DetectLanguageId("/SRC/makefile"));
        Assert.Equal("csharp", registry.DetectLanguageId("x.cs"));
        Assert.Null(registry.DetectLanguageId("x.csv"));
    }

    [Fact]
    public async Task Language_with_an_empty_extension_list_is_detected_by_name_only()
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("bare.json", """{"dockerfile":{"extensions":[],"filenames":["Dockerfile"]}}""");

        var registry = await LanguageRegistry.LoadAsync(null, user);

        Assert.Equal("dockerfile", registry.DetectLanguageId("ci/Dockerfile"));
        Assert.Null(registry.DetectLanguageId("ci/Dockerfile.bak"));
    }
}
