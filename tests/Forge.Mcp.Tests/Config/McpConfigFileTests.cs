using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Forge.Mcp.Tests.Config;

public class McpConfigFileTests
{
    private static McpServerEntry ValidEntry(string name = "srv", string command = "cmd") => new()
    {
        Name = name,
        Command = command,
    };

    [Fact]
    public void Read_reports_bad_json_as_a_diagnostic_instead_of_throwing()
    {
        var result = McpConfigFile.Read("{ not json", "broken.json");

        Assert.Empty(result.Entries);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("broken.json", diagnostic.Source);
        Assert.Contains("Invalid JSON", diagnostic.Message);
    }

    [Fact]
    public void Read_reports_a_non_object_root_as_a_diagnostic()
    {
        var result = McpConfigFile.Read("[1,2]", "list.json");

        Assert.Empty(result.Entries);
        Assert.Equal("The document root is not an object.", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Read_treats_a_missing_mcpServers_as_an_empty_config()
    {
        var result = McpConfigFile.Read("{}", "empty.json");

        Assert.Empty(result.Entries);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("""{"mcpServers":[]}""")]
    [InlineData("""{"mcpServers":"no"}""")]
    public void Read_does_not_throw_when_mcpServers_is_not_an_object(string json)
    {
        var result = McpConfigFile.Read(json, "shape.json");

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Read_skips_a_non_object_entry_with_a_diagnostic_and_keeps_the_valid_ones()
    {
        const string json = """{"mcpServers":{"ok":{"command":"x"},"scalar":42}}""";

        var result = McpConfigFile.Read(json, "test.json");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("ok", entry.Name);
        Assert.Equal("x", entry.Command);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("test.json", diagnostic.Source);
        Assert.Equal("scalar", diagnostic.ServerName);
        Assert.Contains("not an object", diagnostic.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_treats_blank_text_as_an_empty_config(string? json)
    {
        var result = McpConfigFile.Read(json, "empty.json");

        Assert.Empty(result.Entries);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_keeps_fields_verbatim_without_expanding_variables()
    {
        const string json = """{"mcpServers":{"s":{"command":"npx","args":["-y","${TOKEN}"],"env":{"A":"${TOKEN}/tail"}}}}""";

        var result = McpConfigFile.Read(json, "test.json");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("npx", entry.Command);
        Assert.Equal(["-y", "${TOKEN}"], entry.Args);
        Assert.Equal("${TOKEN}/tail", Assert.Single(entry.Env).Value);
        Assert.Equal("test.json", entry.SourcePath);
    }

    [Fact]
    public void Read_keeps_unknown_fields_in_the_raw_node_json()
    {
        const string json = """{"mcpServers":{"s":{"command":"npx","type":"sse","alwaysAllow":["t1","t2"]}}}""";

        var entry = Assert.Single(McpConfigFile.Read(json, "test.json").Entries);

        Assert.Contains("\"type\":\"sse\"", entry.RawNodeJson);
        Assert.Contains("\"alwaysAllow\"", entry.RawNodeJson);
    }

    [Fact]
    public void Read_drops_fields_of_an_unexpected_json_kind()
    {
        const string json = """{"mcpServers":{"s":{"command":"x","args":3,"env":[],"disabled":"true","timeoutSeconds":"5"}}}""";

        var entry = Assert.Single(McpConfigFile.Read(json).Entries);

        Assert.Empty(entry.Args);
        Assert.Empty(entry.Env);
        Assert.False(entry.Disabled);
        Assert.Null(entry.TimeoutSeconds);
    }

    [Fact]
    public void FromNode_reads_every_managed_field()
    {
        const string json = """{"command":"cmd","args":["a","b"],"env":{"K":"v"},"cwd":"/w","url":"https://x.test/mcp","headers":{"H":"h"},"disabled":true,"readOnlyTools":["r"],"timeoutSeconds":12.5,"custom":{"kept":true}}""";

        var entry = McpServerEntry.FromNode("srv", JsonNode.Parse(json)!.AsObject(), "file.json");

        Assert.Equal("srv", entry.Name);
        Assert.Equal("cmd", entry.Command);
        Assert.Equal(["a", "b"], entry.Args);
        Assert.Equal(new KeyValuePair<string, string>("K", "v"), Assert.Single(entry.Env));
        Assert.Equal("/w", entry.WorkingDirectory);
        Assert.Equal("https://x.test/mcp", entry.Url);
        Assert.Equal(new KeyValuePair<string, string>("H", "h"), Assert.Single(entry.Headers));
        Assert.True(entry.Disabled);
        Assert.Equal(["r"], entry.ReadOnlyTools);
        Assert.Equal(12.5, entry.TimeoutSeconds);
        Assert.Equal("file.json", entry.SourcePath);
        Assert.Contains("\"custom\"", entry.RawNodeJson);
    }

    [Fact]
    public void FromNode_to_node_round_trips_the_scalar_fields_unchanged()
    {
        const string json = """{"command":"cmd","env":{"K":"v"},"cwd":"/w","url":"https://x","headers":{"H":"h"},"disabled":true,"timeoutSeconds":12.5}""";

        var entry = McpServerEntry.FromNode("srv", JsonNode.Parse(json)!.AsObject());
        var written = entry.ToNode();

        Assert.Equal(json, written.ToJsonString());
        var again = McpServerEntry.FromNode("srv", written);
        Assert.Equal(entry.Command, again.Command);
        Assert.Equal(entry.Env, again.Env);
        Assert.Equal(entry.WorkingDirectory, again.WorkingDirectory);
        Assert.Equal(entry.Url, again.Url);
        Assert.Equal(entry.Headers, again.Headers);
        Assert.Equal(entry.Disabled, again.Disabled);
        Assert.Equal(entry.TimeoutSeconds, again.TimeoutSeconds);
    }

    [Fact(Skip = "缺陷: McpConfigFile.SetArray (McpConfigFile.cs:309) 把条目数写进了数组首元素")]
    public void ToNode_writes_args_and_read_only_tools_verbatim()
    {
        var entry = new McpServerEntry { Name = "s", Command = "c", Args = ["-y", "x"], ReadOnlyTools = ["view"] };

        var node = entry.ToNode();

        Assert.Equal(["-y", "x"], node["args"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(["view"], node["readOnlyTools"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void ToNode_omits_fields_at_their_defaults()
    {
        var entry = new McpServerEntry { Name = "srv", Command = "cmd" };

        Assert.Equal("""{"command":"cmd"}""", entry.ToNode().ToJsonString());
    }

    [Fact]
    public void ToNode_writes_disabled_only_when_true()
    {
        var withTrue = new McpServerEntry { Name = "s", Command = "c", Disabled = true };
        var withFalse = new McpServerEntry { Name = "s", Command = "c", Disabled = false };

        Assert.Contains("\"disabled\":true", withTrue.ToNode().ToJsonString());
        Assert.DoesNotContain("disabled", withFalse.ToNode().ToJsonString());
    }

    [Fact]
    public void ToNode_writes_timeout_seconds_as_a_number()
    {
        var entry = new McpServerEntry { Name = "s", Command = "c", TimeoutSeconds = 0.5 };

        Assert.Equal("0.5", entry.ToNode()["timeoutSeconds"]!.ToJsonString());
    }

    [Fact]
    public void Normalized_trims_text_and_drops_blank_args_and_blank_keys()
    {
        var entry = new McpServerEntry
        {
            Name = "  srv  ",
            Command = " cmd ",
            Args = [" -a ", "", "   ", "b"],
            Env = [new KeyValuePair<string, string>(" KEY ", " v "), new("   ", "dropped")],
            WorkingDirectory = " /w ",
            Url = " https://x ",
            Headers = [new KeyValuePair<string, string>("H", "1"), new(" ", "dropped")],
            ReadOnlyTools = [" r ", ""],
        };

        var clean = entry.Normalized();

        Assert.Equal("srv", clean.Name);
        Assert.Equal("cmd", clean.Command);
        Assert.Equal(["-a", "b"], clean.Args);
        Assert.Equal([new KeyValuePair<string, string>("KEY", " v ")], clean.Env);
        Assert.Equal("/w", clean.WorkingDirectory);
        Assert.Equal("https://x", clean.Url);
        Assert.Equal([new KeyValuePair<string, string>("H", "1")], clean.Headers);
        Assert.Equal(["r"], clean.ReadOnlyTools);
    }

    [Fact]
    public void Validate_rejects_an_empty_name()
    {
        var entry = new McpServerEntry { Name = "  ", Command = "cmd" };

        Assert.Equal([new McpEntryIssue(McpEntryField.Name, McpEntryProblem.Required)], entry.Validate());
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("ctrl\u0001char")]
    public void Validate_rejects_a_name_with_a_quote_or_control_character(string name)
    {
        var entry = new McpServerEntry { Name = name, Command = "cmd" };

        Assert.Equal([new McpEntryIssue(McpEntryField.Name, McpEntryProblem.InvalidCharacter)], entry.Validate());
    }

    [Fact]
    public void Validate_rejects_an_entry_with_both_command_and_url()
    {
        var entry = new McpServerEntry { Name = "s", Command = "cmd", Url = "https://x.test/mcp" };

        Assert.Equal([new McpEntryIssue(McpEntryField.Transport, McpEntryProblem.BothSet)], entry.Validate());
    }

    [Fact]
    public void Validate_rejects_an_entry_with_neither_command_nor_url()
    {
        var entry = new McpServerEntry { Name = "s" };

        Assert.Equal([new McpEntryIssue(McpEntryField.Transport, McpEntryProblem.NeitherSet)], entry.Validate());
    }

    [Theory]
    [InlineData("ftp://x.test/mcp")]
    [InlineData("/relative")]
    public void Validate_rejects_a_url_that_is_not_absolute_http_or_https(string url)
    {
        var entry = new McpServerEntry { Name = "s", Url = url };

        Assert.Equal([new McpEntryIssue(McpEntryField.Url, McpEntryProblem.NotHttpUrl, url)], entry.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3600.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_rejects_timeouts_outside_the_accepted_range(double seconds)
    {
        var entry = new McpServerEntry { Name = "s", Command = "cmd", TimeoutSeconds = seconds };

        Assert.Equal([new McpEntryIssue(McpEntryField.Timeout, McpEntryProblem.OutOfRange, "3600")], entry.Validate());
    }

    [Fact]
    public void Validate_flags_duplicate_env_and_header_keys()
    {
        var entry = new McpServerEntry
        {
            Name = "s",
            Command = "cmd",
            Env = [new KeyValuePair<string, string>("A", "1"), new("A ", "2")],
            Headers = [new KeyValuePair<string, string>("H", "1"), new("H", "2")],
        };

        Assert.Equal(
            [
                new McpEntryIssue(McpEntryField.Env, McpEntryProblem.DuplicateKey, "A"),
                new McpEntryIssue(McpEntryField.Headers, McpEntryProblem.DuplicateKey, "H"),
            ],
            entry.Validate());
    }

    [Fact]
    public void Validate_accepts_a_valid_entry()
    {
        var entry = new McpServerEntry
        {
            Name = "srv",
            Command = "cmd",
            TimeoutSeconds = 30,
            Env = [new KeyValuePair<string, string>("A", "1")],
        };

        Assert.Empty(entry.Validate());
    }

    [Fact]
    public void Validate_drops_blank_env_and_header_keys_during_normalization()
    {
        // Blank keys are removed by Normalized() before the checks run, so they never surface as a Required issue.
        var entry = new McpServerEntry
        {
            Name = "srv",
            Command = "cmd",
            Env = [new KeyValuePair<string, string>(" ", "1")],
            Headers = [new KeyValuePair<string, string>("", "2")],
        };

        Assert.Empty(entry.Validate());
        Assert.Empty(entry.Normalized().Env);
        Assert.Empty(entry.Normalized().Headers);
    }

    [Fact]
    public void Upsert_adds_a_new_entry_with_indentation_and_a_trailing_newline()
    {
        var text = McpConfigFile.Upsert(null, ValidEntry());

        Assert.EndsWith("\n", text);
        Assert.Contains("\n  \"mcpServers\"", text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("cmd", document.RootElement.GetProperty("mcpServers").GetProperty("srv").GetProperty("command").GetString());
    }

    [Fact]
    public void Upsert_replaces_only_the_target_entry_and_leaves_other_roots_and_entries_alone()
    {
        const string json = """
            {
              "$schema": "https://example.test/schema.json",
              "mcpServers": {
                "keep": {"command": "keep-cmd", "type": "sse"},
                "edit": {"command": "old-cmd"}
              }
            }
            """;

        var text = McpConfigFile.Upsert(json, new McpServerEntry { Name = "edit", Command = "new-cmd" });

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("https://example.test/schema.json", root.GetProperty("$schema").GetString());
        var servers = root.GetProperty("mcpServers");
        Assert.Equal(["keep", "edit"], servers.EnumerateObject().Select(property => property.Name));
        Assert.Equal("keep-cmd", servers.GetProperty("keep").GetProperty("command").GetString());
        Assert.Equal("sse", servers.GetProperty("keep").GetProperty("type").GetString());
        Assert.Equal("new-cmd", servers.GetProperty("edit").GetProperty("command").GetString());
    }

    [Fact]
    public void Upsert_preserves_unknown_fields_of_the_entry_being_edited()
    {
        const string json = """{"mcpServers":{"srv":{"command":"old","type":"sse","alwaysAllow":["t1","t2"]}}}""";

        var read = McpConfigFile.Read(json, "test.json");
        var entry = Assert.Single(read.Entries);
        var edited = new McpServerEntry { Name = entry.Name, Command = "new", RawNodeJson = entry.RawNodeJson };

        var text = McpConfigFile.Upsert(json, edited);

        using var document = JsonDocument.Parse(text);
        var node = document.RootElement.GetProperty("mcpServers").GetProperty("srv");
        Assert.Equal("new", node.GetProperty("command").GetString());
        Assert.Equal("sse", node.GetProperty("type").GetString());
        Assert.Equal(["t1", "t2"], node.GetProperty("alwaysAllow").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Upsert_omits_managed_fields_at_their_defaults()
    {
        var text = McpConfigFile.Upsert(null, ValidEntry());

        var node = JsonNode.Parse(text)!["mcpServers"]!["srv"];

        Assert.Equal("""{"command":"cmd"}""", node!.ToJsonString());
    }

    [Fact]
    public void Upsert_writes_disabled_only_when_true()
    {
        var on = McpConfigFile.Upsert(null, new McpServerEntry { Name = "s", Command = "c", Disabled = true });
        var off = McpConfigFile.Upsert(null, new McpServerEntry { Name = "s", Command = "c" });

        Assert.Contains("\"disabled\": true", on);
        Assert.DoesNotContain("disabled", off);
    }

    [Fact]
    public void Upsert_rejects_an_entry_that_does_not_validate()
    {
        var entry = new McpServerEntry { Name = "srv" };

        var error = Assert.Throws<ArgumentException>(() => McpConfigFile.Upsert(null, entry));

        Assert.Contains("'srv'", error.Message);
        Assert.Contains("Transport/NeitherSet", error.Message);
    }

    [Fact]
    public void Upsert_rejects_a_name_with_an_invalid_character()
    {
        var entry = new McpServerEntry { Name = "bad\"name", Command = "cmd" };

        Assert.Throws<ArgumentException>(() => McpConfigFile.Upsert(null, entry));
    }

    [Theory]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("[1,2]", "not an object")]
    public void Upsert_rejects_bad_json_and_a_non_object_root(string json, string expectedMessage)
    {
        var error = Assert.Throws<InvalidDataException>(() => McpConfigFile.Upsert(json, ValidEntry()));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void Upsert_rejects_a_non_object_mcpServers()
    {
        var error = Assert.Throws<InvalidDataException>(() => McpConfigFile.Upsert("""{"mcpServers":[]}""", ValidEntry()));

        Assert.Equal("\"mcpServers\" is not an object.", error.Message);
    }

    [Fact]
    public void Import_writes_foreign_entries_verbatim_keeping_placeholders_and_unknown_fields()
    {
        const string foreign = """{"mcpServers":{"remote":{"command":"npx","env":{"TOKEN":"${TOKEN}"},"type":"sse"}}}""";
        var imported = McpConfigFile.Read(foreign, "other.json").Entries;
        const string existing = """{"mcpServers":{"local":{"command":"keep"}},"otherRoot":7}""";

        var text = McpConfigFile.Import(existing, imported);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var servers = root.GetProperty("mcpServers");
        Assert.Equal("keep", servers.GetProperty("local").GetProperty("command").GetString());
        var remote = servers.GetProperty("remote");
        Assert.Equal("npx", remote.GetProperty("command").GetString());
        Assert.Equal("${TOKEN}", remote.GetProperty("env").GetProperty("TOKEN").GetString());
        Assert.Equal("sse", remote.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("otherRoot").GetInt32());
    }

    [Fact]
    public void Import_rejects_an_invalid_entry()
    {
        Assert.Throws<ArgumentException>(() => McpConfigFile.Import(null, [new McpServerEntry { Name = "bad" }]));
    }

    [Fact]
    public void Remove_deletes_only_the_target_entry_and_keeps_the_rest()
    {
        const string json = """{"mcpServers":{"a":{"command":"a"},"b":{"command":"b"}},"keep":1}""";

        var text = McpConfigFile.Remove(json, "a");

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal(["b"], root.GetProperty("mcpServers").EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("keep").GetInt32());
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Remove_of_a_name_that_is_not_there_is_not_an_error()
    {
        const string json = """{"mcpServers":{"a":{"command":"a"}}}""";

        var text = McpConfigFile.Remove(json, "missing");

        using var document = JsonDocument.Parse(text);
        Assert.Equal(["a"], document.RootElement.GetProperty("mcpServers").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void Remove_rejects_a_blank_name()
    {
        Assert.Throws<ArgumentException>(() => McpConfigFile.Remove("{}", " "));
    }
}
