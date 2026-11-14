using Xunit;

namespace Forge.Mcp.Tests.Config;

public class McpPresetsTests
{
    [Fact]
    public void Preset_ids_are_unique()
    {
        Assert.Equal(McpPresets.All.Count, McpPresets.All.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Find_returns_the_preset_with_the_exact_id_and_nothing_else()
    {
        var found = McpPresets.Find("git");

        Assert.NotNull(found);
        Assert.Equal("uvx", found.Command);
        Assert.Null(McpPresets.Find("nope"));
        Assert.Null(McpPresets.Find("Git"));
    }

    [Fact]
    public void ToEntry_fills_the_path_token_into_every_argument_that_contains_it()
    {
        var preset = new McpPreset("p", "cmd", ["--dir", "/root/{path}/sub", McpPresets.PathToken]);

        var entry = McpPresets.ToEntry(preset, "srv", "D:/data");

        Assert.Equal("srv", entry.Name);
        Assert.Equal("cmd", entry.Command);
        Assert.Equal(["--dir", "/root/D:/data/sub", "D:/data"], entry.Args);
    }

    [Fact]
    public void ToEntry_drops_a_pure_placeholder_argument_when_no_path_is_chosen()
    {
        var entry = McpPresets.ToEntry(McpPresets.Find("filesystem")!, "srv", null);

        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem"], entry.Args);
    }

    [Fact]
    public void ToEntry_leaves_placeholder_text_alone_when_no_path_is_chosen()
    {
        var preset = new McpPreset("p", "cmd", ["--dir", "{path}/sub"]);

        var entry = McpPresets.ToEntry(preset, "srv", " ");

        Assert.Equal(["--dir", "{path}/sub"], entry.Args);
    }

    [Fact]
    public void ToEntry_falls_back_to_the_preset_id_when_the_name_is_blank()
    {
        Assert.Equal("fetch", McpPresets.ToEntry(McpPresets.Find("fetch")!, "  ", null).Name);
        Assert.Equal("my srv", McpPresets.ToEntry(McpPresets.Find("fetch")!, " my srv ", null).Name);
    }

    [Fact]
    public void ToEntry_copies_the_preset_url()
    {
        var preset = new McpPreset("p", "cmd", ["{path}"], Url: "https://x.test/mcp", NeedsPath: true);

        var entry = McpPresets.ToEntry(preset, "srv", "C:/repo");

        Assert.Equal("https://x.test/mcp", entry.Url);
        Assert.Equal(["C:/repo"], entry.Args);
    }
}
