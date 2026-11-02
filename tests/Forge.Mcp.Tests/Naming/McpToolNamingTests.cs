using System.Text.RegularExpressions;
using Xunit;

namespace Forge.Mcp.Tests.Naming;

public class McpToolNamingTests
{
    private static readonly Regex ProviderSafe = new("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);

    [Fact]
    public void Sanitize_keeps_ascii_letters_digits_underscore_and_hyphen()
    {
        Assert.Equal("abc-XYZ_09", McpToolNaming.Sanitize("abc-XYZ_09"));
    }

    [Theory]
    [InlineData("dot.name", "dot_name")]
    [InlineData("with space", "with_space")]
    [InlineData("slash/path:colon", "slash_path_colon")]
    [InlineData("naïve", "na_ve")]
    [InlineData("中文", "__")]
    [InlineData("emoji😀", "emoji__")]
    public void Sanitize_replaces_every_other_char_with_underscore(string input, string expected)
    {
        Assert.Equal(expected, McpToolNaming.Sanitize(input));
    }

    [Fact]
    public void Sanitize_maps_empty_input_to_a_single_underscore()
    {
        Assert.Equal("_", McpToolNaming.Sanitize(string.Empty));
    }

    [Fact]
    public void Sanitize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => McpToolNaming.Sanitize(null!));
    }

    [Fact]
    public void Build_joins_sanitized_server_and_tool_with_the_double_underscore_separator()
    {
        Assert.Equal("github__create_issue", McpToolNaming.Build("github", "create_issue"));
        Assert.Equal("my_srv__list_files", McpToolNaming.Build("my.srv", "list files"));
        Assert.Equal("srv___lead", McpToolNaming.Build("srv", "_lead"));
    }

    [Fact]
    public void Build_at_exactly_the_limit_has_no_hash_suffix()
    {
        var server = new string('s', 20);
        var tool = new string('t', McpToolNaming.MaxLength - server.Length - McpToolNaming.Separator.Length);

        var name = McpToolNaming.Build(server, tool);

        Assert.Equal(McpToolNaming.MaxLength, name.Length);
        Assert.Equal(server + "__" + tool, name);
    }

    [Fact]
    public void Build_one_char_over_the_limit_truncates_and_appends_an_eight_hex_hash()
    {
        var server = new string('s', 20);
        var tool = new string('t', McpToolNaming.MaxLength - server.Length - McpToolNaming.Separator.Length + 1);

        var name = McpToolNaming.Build(server, tool);

        Assert.Equal(McpToolNaming.MaxLength, name.Length);
        Assert.Matches("^s{20}__t+_[0-9a-f]{8}$", name);
        Assert.Matches(ProviderSafe, name);
    }

    [Fact]
    public void Long_names_sharing_a_prefix_get_distinct_hash_suffixes()
    {
        var prefix = new string('p', 80);

        var first = McpToolNaming.Build("srv", prefix + "_alpha");
        var second = McpToolNaming.Build("srv", prefix + "_beta");

        Assert.NotEqual(first, second);
        Assert.Equal(McpToolNaming.MaxLength, first.Length);
        Assert.Equal(McpToolNaming.MaxLength, second.Length);
    }

    [Fact]
    public void Hash_suffix_is_stable_across_calls_and_processes_for_the_same_input()
    {
        var long1 = new string('q', 90);
        var expected = McpToolNaming.Build("server", long1);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected, McpToolNaming.Build("server", long1));
        }

        var key = "server__" + long1;
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))[..4]);
        Assert.Equal(McpToolNaming.MaxLength, expected.Length);
        Assert.Equal("_" + digest, expected[^9..]);
    }

    [Fact]
    public void Hash_is_taken_over_the_unsanitized_key_so_different_raw_names_do_not_collide_after_sanitizing()
    {
        var padding = new string('z', 70);

        var dotted = McpToolNaming.Build("srv", padding + ".x");
        var underscored = McpToolNaming.Build("srv", padding + "_x");

        Assert.NotEqual(dotted, underscored);
    }

    [Fact]
    public void Build_always_produces_a_provider_safe_name_within_the_limit()
    {
        var inputs = new[]
        {
            ("a", "b"),
            ("server with spaces", "tool.with/many:chars"),
            (new string('é', 50), new string('ü', 50)),
            (new string('x', 200), new string('y', 200)),
            ("", ""),
        };

        foreach (var (server, tool) in inputs)
        {
            var name = McpToolNaming.Build(server, tool);
            Assert.True(name.Length <= McpToolNaming.MaxLength, name);
            Assert.Matches(ProviderSafe, name);
        }
    }

    [Fact]
    public void Disambiguate_appends_a_hash_derived_from_the_key_and_respects_the_limit()
    {
        var first = McpToolNaming.Disambiguate("srv__tool", "srv__tool");
        var again = McpToolNaming.Disambiguate("srv__tool", "srv__tool");
        var other = McpToolNaming.Disambiguate("srv__tool", "srv__tool#1");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.StartsWith("srv__tool_", first);
        Assert.Equal("srv__tool".Length + 9, first.Length);

        var long1 = McpToolNaming.Disambiguate(new string('n', 64), "k");
        Assert.Equal(McpToolNaming.MaxLength, long1.Length);
    }
}
