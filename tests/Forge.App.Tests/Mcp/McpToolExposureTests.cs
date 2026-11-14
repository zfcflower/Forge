using Forge.App.Agents;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The per-session MCP tool surface: <see cref="McpToolExposure"/> selects which of a ready server's tools a request
/// offers (everything when the limit does not hide any, otherwise the pinned tools, then the best focus matches, then a
/// fill in catalog order, all sorted by exposed name) and Search pins its matches so the next selection really offers them.
/// </summary>
public sealed class McpToolExposureTests
{
    /// <summary>One server, five tools in ordinal name order: t_1_alpha and t_2_beta mention "files", t_5_epsilon is the only "epsilon" name.</summary>
    private static readonly (string, string?)[] Alpha =
        [("t_1_alpha", "reads files"), ("t_2_beta", "writes files"), ("t_3_gamma", "plain tool"), ("t_4_delta", "plain tool"), ("t_5_epsilon", "plain tool")];

    [Fact]
    public async Task Select_with_a_zero_limit_returns_the_whole_catalog_without_truncating()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var selected = exposure.Select(catalog.Tools, 0, "files", out var truncated);

        Assert.Same(catalog.Tools, selected);
        Assert.False(truncated);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Select_at_or_above_the_catalog_size_returns_everything_untruncated(int limit)
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var selected = exposure.Select(catalog.Tools, limit, "files", out var truncated);

        Assert.Same(catalog.Tools, selected);
        Assert.False(truncated);
    }

    [Fact]
    public async Task Select_below_the_catalog_size_truncates_and_returns_at_most_limit_tools()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var selected = exposure.Select(catalog.Tools, 2, null, out var truncated);

        Assert.True(truncated);
        Assert.Equal(2, selected.Count);
        Assert.All(selected, tool => Assert.Contains(tool, catalog.Tools));
    }

    [Theory]
    [InlineData("zzz-nothing-matches")]
    [InlineData(null)]
    public async Task Select_with_no_matching_words_fills_in_catalog_order_then_sorts_by_exposed_name(string? focus)
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        // The manager publishes name-sorted tools; feed them reversed so "catalog order" and "name order" can disagree.
        var reversed = catalog.Tools.Reverse().ToArray();
        var exposure = new McpToolExposure();

        var selected = exposure.Select(reversed, 3, focus, out var truncated);

        Assert.True(truncated);
        // The first three in catalog order are epsilon, delta, gamma; the result sorts them by exposed name.
        Assert.Equal(["srv__t_3_gamma", "srv__t_4_delta", "srv__t_5_epsilon"], selected.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Select_ranks_a_name_match_above_a_description_match()
    {
        var tools = new (string, string?)[]
        {
            ("t_1_alpha", "plain tool"), ("t_2_beta", "plain tool"), ("t_3_gamma", "mentions the epsilon workflow"),
            ("t_4_delta", "plain tool"), ("t_5_epsilon", "plain tool"),
        };
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", tools);
        var exposure = new McpToolExposure();

        var selected = exposure.Select(catalog.Tools, 1, "epsilon", out var truncated);

        Assert.Equal("t_5_epsilon", Assert.Single(selected).ToolName);
        Assert.True(truncated);
    }

    [Fact]
    public async Task Select_offers_description_matches_before_unmatched_tools_and_fills_the_rest_in_catalog_order()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var selected = exposure.Select(catalog.Tools, 3, "files", out var truncated);

        Assert.True(truncated);
        Assert.Equal(["srv__t_1_alpha", "srv__t_2_beta", "srv__t_3_gamma"], selected.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Search_pins_its_matches_so_the_next_select_offers_them()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var baseline = exposure.Select(catalog.Tools, 3, null, out _);
        Assert.Equal(["srv__t_1_alpha", "srv__t_2_beta", "srv__t_3_gamma"], baseline.Select(tool => tool.ExposedName));

        var matches = exposure.Search(catalog.Tools, "epsilon", 1);

        Assert.Equal("t_5_epsilon", Assert.Single(matches).ToolName);
        Assert.Equal(1, exposure.PinnedCount);

        var selected = exposure.Select(catalog.Tools, 3, null, out var truncated);

        Assert.True(truncated);
        Assert.Equal(["srv__t_1_alpha", "srv__t_2_beta", "srv__t_5_epsilon"], selected.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task Search_ranks_name_matches_above_description_matches_and_respects_the_limit()
    {
        var tools = new (string, string?)[]
        {
            ("t_1_alpha", "plain tool"), ("t_2_beta", "plain tool"), ("t_3_gamma", "mentions the epsilon workflow"),
            ("t_4_delta", "plain tool"), ("t_5_epsilon", "plain tool"),
        };
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", tools);
        var exposure = new McpToolExposure();

        var best = exposure.Search(catalog.Tools, "epsilon", 1);
        var both = exposure.Search(catalog.Tools, "epsilon", 2);

        Assert.Equal(["t_5_epsilon"], best.Select(tool => tool.ToolName));
        Assert.Equal(["t_5_epsilon", "t_3_gamma"], both.Select(tool => tool.ToolName));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("files", 0)]
    [InlineData("files", -2)]
    public async Task Search_without_words_or_with_a_nonpositive_limit_returns_nothing_and_pins_nothing(string? query, int limit)
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        Assert.Empty(exposure.Search(catalog.Tools, query, limit));
        Assert.Equal(0, exposure.PinnedCount);
    }

    [Fact]
    public async Task Search_that_matches_nothing_returns_nothing_and_pins_nothing()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        Assert.Empty(exposure.Search(catalog.Tools, "zzz", 5));
        Assert.Equal(0, exposure.PinnedCount);
    }

    [Fact]
    public async Task ClearPins_returns_the_selection_to_pure_scoring()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();

        var baseline = exposure.Select(catalog.Tools, 3, null, out _);
        exposure.Search(catalog.Tools, "epsilon", 1);
        var pinned = exposure.Select(catalog.Tools, 3, null, out _);
        Assert.Contains("srv__t_5_epsilon", pinned.Select(tool => tool.ExposedName));

        exposure.ClearPins();

        Assert.Equal(0, exposure.PinnedCount);
        var cleared = exposure.Select(catalog.Tools, 3, null, out _);
        Assert.Equal(baseline.Select(tool => tool.ExposedName), cleared.Select(tool => tool.ExposedName));
        Assert.NotSame(baseline, cleared);
    }

    [Fact]
    public async Task Pinning_more_than_two_hundred_names_evicts_the_oldest()
    {
        var tools = Enumerable.Range(0, 205).Select(index => ($"tool{index:D3}", (string?)null)).ToArray();
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", tools);
        var exposure = new McpToolExposure();

        for (var index = 0; index <= 200; index++)
        {
            Assert.Single(exposure.Search(catalog.Tools, $"tool{index:D3}", 1));
        }

        Assert.Equal(200, exposure.PinnedCount);

        // tool000 was pinned first, so it is the one that fell out: 200 pins survive, tool001 does and tool000 does not.
        var surviving = exposure.Select(catalog.Tools, 200, null, out var truncated).Select(tool => tool.ToolName).ToArray();
        Assert.True(truncated);
        Assert.Equal(200, surviving.Length);
        Assert.Contains("tool001", surviving);
        Assert.Contains("tool200", surviving);
        Assert.DoesNotContain("tool000", surviving);

        // A limit below the pin count keeps the newest pins: tool200 was pinned last.
        var selected = exposure.Select(catalog.Tools, 1, null, out var oneTruncated);
        Assert.True(oneTruncated);
        Assert.Equal("tool200", Assert.Single(selected).ToolName);
    }

    [Fact]
    public async Task Select_caches_the_result_when_catalog_limit_focus_and_pins_are_unchanged()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", Alpha);
        var exposure = new McpToolExposure();
        var tools = catalog.Tools;

        var first = exposure.Select(tools, 2, "files", out var firstTruncated);
        var second = exposure.Select(tools, 2, "files", out var secondTruncated);

        Assert.True(firstTruncated);
        Assert.Same(first, second);
        Assert.Equal(firstTruncated, secondTruncated);

        var otherFocus = exposure.Select(tools, 2, "zzz", out _);
        Assert.NotSame(first, otherFocus);

        var otherLimit = exposure.Select(tools, 3, "files", out _);
        Assert.NotSame(first, otherLimit);

        var otherCatalog = tools.ToArray();
        Assert.NotSame(first, exposure.Select(otherCatalog, 2, "files", out _));

        exposure.Search(tools, "epsilon", 1);
        var afterPin = exposure.Select(tools, 2, "files", out _);
        Assert.NotSame(first, afterPin);
        Assert.Contains("srv__t_5_epsilon", afterPin.Select(tool => tool.ExposedName));
    }

    [Fact]
    public async Task An_empty_catalog_selects_nothing_and_never_truncates()
    {
        await using var catalog = await ReadyMcpCatalog.OneServerAsync("srv", []);
        var exposure = new McpToolExposure();

        Assert.Empty(exposure.Select(catalog.Tools, 2, null, out var truncated));
        Assert.False(truncated);
        Assert.Empty(exposure.Search(catalog.Tools, "anything", 5));
    }
}
