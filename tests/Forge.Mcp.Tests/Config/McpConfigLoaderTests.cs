using Xunit;

namespace Forge.Mcp.Tests.Config;

public class McpConfigLoaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-mcp-config-" + Guid.NewGuid().ToString("N"));

    public McpConfigLoaderTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string>
    {
        ["TOKEN"] = "abc123",
        ["HOME_DIR"] = "/home/dev",
        ["EMPTY"] = "",
    };

    [Fact]
    public void Parse_reads_every_field_of_a_stdio_entry()
    {
        const string json = """
            {
              "mcpServers": {
                "files": {
                  "command": "npx",
                  "args": ["-y", "files-server", "", 3],
                  "env": { "API_KEY": "${TOKEN}", "COUNT": 7, "FLAG": true, "NOTHING": null },
                  "cwd": "/srv/files",
                  "readOnlyTools": ["read_file", "", "list"],
                  "timeoutSeconds": 12.5
                }
              }
            }
            """;

        var config = McpConfigLoader.Parse(json, "test.json", Env);

        var server = Assert.Single(config.Servers);
        Assert.Equal("files", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal(["-y", "files-server"], server.Args);
        Assert.Equal("abc123", server.Env["API_KEY"]);
        Assert.Equal("7", server.Env["COUNT"]);
        Assert.Equal("true", server.Env["FLAG"]);
        Assert.Equal(string.Empty, server.Env["NOTHING"]);
        Assert.Equal("/srv/files", server.WorkingDirectory);
        Assert.Equal(["read_file", "list"], server.ReadOnlyTools);
        Assert.Equal(TimeSpan.FromSeconds(12.5), server.Timeout);
        Assert.True(server.IsStdio);
        Assert.False(server.IsHttp);
        Assert.False(server.Disabled);
        Assert.Empty(config.Diagnostics);
    }

    [Fact]
    public void Parse_reads_an_http_entry_with_headers_and_disabled_flag()
    {
        const string json = """
            {"mcpServers":{"remote":{"url":"https://mcp.example.test/rpc","headers":{"Authorization":"Bearer ${TOKEN}"},"disabled":true}}}
            """;

        var config = McpConfigLoader.Parse(json, "test.json", Env);

        var server = Assert.Single(config.Servers);
        Assert.True(server.IsHttp);
        Assert.False(server.IsStdio);
        Assert.Equal("https://mcp.example.test/rpc", server.Url);
        Assert.Equal("Bearer abc123", server.Headers["Authorization"]);
        Assert.True(server.Disabled);
        Assert.Null(server.Timeout);
        Assert.Empty(config.Diagnostics);
    }

    [Fact]
    public void Disabled_is_only_honoured_for_the_literal_true_value()
    {
        const string json = """{"mcpServers":{"a":{"command":"x","disabled":"true"},"b":{"command":"y","disabled":1}}}""";

        var config = McpConfigLoader.Parse(json, "test.json", Env);

        Assert.All(config.Servers, server => Assert.False(server.Disabled));
    }

    [Fact]
    public void Environment_variables_expand_inside_strings_and_missing_ones_become_empty_with_a_diagnostic()
    {
        const string json = """
            {"mcpServers":{"s":{"command":"x","env":{"A":"${TOKEN}-${HOME_DIR}","B":"${MISSING}/tail","C":"no-vars","D":"unterminated ${TOKEN"}}}}
            """;

        var config = McpConfigLoader.Parse(json, "project.json", Env);

        var server = Assert.Single(config.Servers);
        Assert.Equal("abc123-/home/dev", server.Env["A"]);
        Assert.Equal("/tail", server.Env["B"]);
        Assert.Equal("no-vars", server.Env["C"]);
        Assert.Equal("unterminated ${TOKEN", server.Env["D"]);
        var diagnostic = Assert.Single(config.Diagnostics);
        Assert.Equal("project.json", diagnostic.Source);
        Assert.Equal("s", diagnostic.ServerName);
        Assert.Contains("'MISSING'", diagnostic.Message);
    }

    [Fact]
    public void ExpandVariables_uses_the_supplied_lookup_and_leaves_other_text_alone()
    {
        Assert.Equal("abc123 and $TOKEN and ${", McpConfigLoader.ExpandVariables("${TOKEN} and $TOKEN and ${", Env));
        Assert.Equal("", McpConfigLoader.ExpandVariables("${NOPE}", Env));
        Assert.Equal("plain", McpConfigLoader.ExpandVariables("plain", new Dictionary<string, string>()));
    }

    [Fact]
    public void Entries_that_are_not_objects_or_lack_command_and_url_produce_diagnostics_not_exceptions()
    {
        const string json = """
            {"mcpServers":{
              "ok":{"command":"x"},
              "scalar":"nope",
              "empty":{},
              "blank":{"command":"   ","url":""}
            }}
            """;

        var config = McpConfigLoader.Parse(json, "test.json", Env);

        Assert.Equal(["ok"], config.Servers.Select(server => server.Name));
        Assert.Equal(3, config.Diagnostics.Count);
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "scalar" && diagnostic.Message.Contains("not an object"));
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "empty" && diagnostic.Message.Contains("Neither"));
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "blank" && diagnostic.Message.Contains("Neither"));
    }

    [Fact]
    public void Invalid_json_and_wrong_shapes_become_root_level_diagnostics()
    {
        Assert.Contains("Invalid JSON", Assert.Single(McpConfigLoader.Parse("{ not json", "bad.json").Diagnostics).Message);
        Assert.Equal("The document root is not an object.", Assert.Single(McpConfigLoader.Parse("[]", "bad.json").Diagnostics).Message);
        Assert.Equal("No \"mcpServers\" object.", Assert.Single(McpConfigLoader.Parse("{}", "bad.json").Diagnostics).Message);
        Assert.Equal("\"mcpServers\" is not an object.", Assert.Single(McpConfigLoader.Parse("""{"mcpServers":[]}""", "bad.json").Diagnostics).Message);

        var empty = McpConfigLoader.Parse("", "bad.json");
        Assert.Empty(empty.Servers);
        Assert.Single(empty.Diagnostics);
    }

    [Fact]
    public void Urls_must_be_absolute_and_command_plus_url_prefers_url_with_a_warning()
    {
        const string json = """
            {"mcpServers":{
              "relative":{"url":"/mcp"},
              "both":{"command":"x","url":"http://localhost:9000/mcp"}
            }}
            """;

        var config = McpConfigLoader.Parse(json, "test.json");

        var both = Assert.Single(config.Servers);
        Assert.Equal("both", both.Name);
        Assert.Null(both.Command);
        Assert.Equal("http://localhost:9000/mcp", both.Url);
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "relative" && diagnostic.Message.Contains("not an absolute URI"));
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "both" && diagnostic.Message.Contains("using \"url\""));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("\"soon\"")]
    public void Invalid_timeout_values_are_reported_and_ignored(string value)
    {
        var json = "{\"mcpServers\":{\"t\":{\"command\":\"x\",\"timeoutSeconds\":" + value + "}}}";
        var config = McpConfigLoader.Parse(json, "test.json");

        Assert.Null(Assert.Single(config.Servers).Timeout);
        Assert.Contains(config.Diagnostics, diagnostic => diagnostic.ServerName == "t" && diagnostic.Message.Contains("timeoutSeconds"));
    }

    [Fact]
    public void Null_timeout_is_treated_as_absent_without_a_diagnostic()
    {
        var config = McpConfigLoader.Parse("""{"mcpServers":{"t":{"command":"x","timeoutSeconds":null}}}""", "test.json");

        Assert.Null(Assert.Single(config.Servers).Timeout);
        Assert.Empty(config.Diagnostics);
    }

    [Fact]
    public void Trailing_commas_and_comments_are_accepted()
    {
        const string json = """
            {
              // a comment
              "mcpServers": {
                "c": { "command": "x", "args": ["a",], },
              },
            }
            """;

        var config = McpConfigLoader.Parse(json, "test.json");

        Assert.Equal(["a"], Assert.Single(config.Servers).Args);
        Assert.Empty(config.Diagnostics);
    }

    [Fact]
    public void Duplicate_names_within_one_file_keep_only_the_last_entry()
    {
        const string json = """{"mcpServers":{"dup":{"command":"first"},"dup":{"command":"second"}}}""";

        var config = McpConfigLoader.Parse(json, "test.json");

        var server = Assert.Single(config.Servers);
        Assert.Equal("second", server.Command);
    }

    [Fact]
    public void Project_entry_replaces_the_global_entry_of_the_same_name_and_keeps_the_rest()
    {
        var globalPath = Path.Combine(root, "global.json");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
        File.WriteAllText(globalPath, """{"mcpServers":{"shared":{"command":"global-shared"},"only-global":{"command":"g"}}}""");
        File.WriteAllText(McpConfigLoader.ProjectPath(workspace), """{"mcpServers":{"shared":{"command":"project-shared"},"only-project":{"url":"https://p.test/mcp"}}}""");

        var config = McpConfigLoader.LoadFrom(globalPath, workspace, Env);

        Assert.Equal(3, config.Servers.Count);
        Assert.Equal("project-shared", config.Find("shared")!.Command);
        Assert.Equal("g", config.Find("only-global")!.Command);
        Assert.Equal("https://p.test/mcp", config.Find("only-project")!.Url);
        Assert.Null(config.Find("missing"));
        Assert.Equal(["only-global", "shared", "only-project"], config.Servers.Select(server => server.Name));
    }

    [Fact]
    public void Missing_files_are_silently_skipped_and_null_paths_load_nothing()
    {
        var config = McpConfigLoader.LoadFrom(Path.Combine(root, "absent.json"), Path.Combine(root, "no-workspace"), Env);

        Assert.Empty(config.Servers);
        Assert.Empty(config.Diagnostics);
        Assert.Empty(McpConfigLoader.LoadFrom(null, null).Servers);
        Assert.Empty(McpConfigLoader.LoadFrom("  ", "").Servers);
    }

    [Fact]
    public void Broken_project_file_reports_diagnostics_but_keeps_the_global_servers()
    {
        var globalPath = Path.Combine(root, "global.json");
        var workspace = Path.Combine(root, "broken");
        Directory.CreateDirectory(Path.Combine(workspace, ".forge"));
        File.WriteAllText(globalPath, """{"mcpServers":{"g":{"command":"g"}}}""");
        File.WriteAllText(McpConfigLoader.ProjectPath(workspace), "{ broken");

        var config = McpConfigLoader.LoadFrom(globalPath, workspace, Env);

        Assert.Equal(["g"], config.Servers.Select(server => server.Name));
        var diagnostic = Assert.Single(config.Diagnostics);
        Assert.Equal(McpConfigLoader.ProjectPath(workspace), diagnostic.Source);
    }

    [Fact]
    public void Project_path_is_under_the_forge_directory_and_rejects_blank_workspaces()
    {
        Assert.Equal(Path.Combine("/ws", ".forge", "mcp.json"), McpConfigLoader.ProjectPath(Path.Combine("/ws")));
        Assert.Throws<ArgumentException>(() => McpConfigLoader.ProjectPath(" "));
    }

    [Fact]
    public void Config_helpers_find_replace_and_report_read_only_membership()
    {
        var first = McpConfigLoader.Parse("""{"mcpServers":{"a":{"command":"one","readOnlyTools":["view"]},"b":{"command":"two"}}}""", "t").Servers;
        var config = new McpConfig(first, []);

        Assert.True(config.Find("a")!.IsReadOnlyTool("view"));
        Assert.False(config.Find("a")!.IsReadOnlyTool("write"));
        Assert.False(config.Find("b")!.IsReadOnlyTool("view"));

        var replaced = config.WithServer(new McpServerConfig { Name = "a", Command = "replaced" });
        Assert.Equal(2, replaced.Servers.Count);
        Assert.Equal("replaced", replaced.Find("a")!.Command);
        Assert.Equal("a", replaced.Servers[0].Name);

        var appended = config.WithServer(new McpServerConfig { Name = "c", Url = "https://c.test/" });
        Assert.Equal(["a", "b", "c"], appended.Servers.Select(server => server.Name));
        Assert.Empty(McpConfig.Empty.Servers);
    }

    [Fact]
    public void Whitespace_only_command_or_url_counts_as_absent()
    {
        var server = new McpServerConfig { Name = "n", Command = "   ", Url = null };

        Assert.False(server.IsStdio);
        Assert.False(server.IsHttp);
    }
}
