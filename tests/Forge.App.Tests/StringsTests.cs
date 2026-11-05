using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Pins the identifier/number formatting rules in <see cref="Strings"/>: process ids and line numbers
/// never carry grouping separators (so <c>49712</c> stays <c>"49712"</c>), while an unreported usage is
/// always the <see cref="Strings.None"/> dash.
/// </summary>
public sealed class StringsTests
{
    [Theory]
    [InlineData(7, "进程 7")]
    [InlineData(49712, "进程 49712")]
    [InlineData(1234567, "进程 1234567")]
    public void Process_id_has_no_grouping_separators(int id, string expected)
    {
        var text = Strings.ProcessId(id);

        Assert.Equal(expected, text);
        Assert.DoesNotContain(",", text);
    }

    [Theory]
    [InlineData(1, 1, "L1")]
    [InlineData(49712, 49712, "L49712")]
    [InlineData(1, 5, "L1–5")]
    [InlineData(49712, 49713, "L49712–49713")]
    public void Review_line_has_no_grouping_separators(int start, int end, string expected)
    {
        var text = Strings.ReviewLine(start, end);

        Assert.Equal(expected, text);
        Assert.DoesNotContain(",", text);
    }

    [Fact]
    public void Null_usage_is_a_dash()
    {
        Assert.Equal(Strings.None, Strings.TokenCount(null));
        Assert.Equal("输入 — · 输出 —", Strings.TokenSummary(null, null));
        Assert.Equal("输入 10 · 输出 —", Strings.TokenSummary(10, null));
        Assert.Equal("输入 — · 输出 80", Strings.TokenSummary(null, 80));
    }

    [Fact]
    public void Turn_header_shows_a_dash_when_usage_was_not_reported()
    {
        var at = new DateTimeOffset(2025, 6, 1, 12, 30, 45, TimeSpan.Zero);

        var header = Strings.TurnHeader(3, at, null, running: false, null, null);

        Assert.Contains("第 3 轮", header);
        Assert.Contains(Strings.None, header);
    }

    [Fact]
    public void Thousands_grouping_stays_in_Number_only_ids_do_not_use_it()
    {
        // Documents why ProcessId/ReviewLine format with InvariantCulture instead of Number():
        // Number() groups thousands, which identifiers must never do.
        Assert.Equal("49,712", Strings.Number(49712));
        Assert.DoesNotContain(",", Strings.ProcessId(49712));
    }
}
