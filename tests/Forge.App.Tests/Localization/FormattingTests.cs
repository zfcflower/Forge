using System.Globalization;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins the language-dependent formatting (task decision: sentence patterns follow the language, numbers and times do
/// not): the turn-card header, elapsed times, grouping, plurals and the unreported-value mark, each with a fixed
/// Chinese and a fixed English sample. Every test names its language up front and restores the ambient one.
/// </summary>
public sealed class FormattingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 30, 45, TimeSpan.FromHours(8));

    private static string Clock => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public void Turn_header_in_Chinese_names_round_time_elapsed_and_tokens()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);

        var header = SessionStrings.TurnHeader(3, At, TimeSpan.FromSeconds(4.2), running: false, 900, 80);

        Assert.Equal($"第 3 轮 · {Clock} · 4.2 秒 · 输入 900 · 输出 80", header);
    }

    [Fact]
    public void Turn_header_in_English_names_round_time_elapsed_and_tokens()
    {
        using var _ = new LanguageScope(UiLanguage.English);

        var header = SessionStrings.TurnHeader(3, At, TimeSpan.FromSeconds(4.2), running: false, 900, 80);

        Assert.Equal($"Turn 3 · {Clock} · 4.2s · in 900 · out 80", header);
    }

    [Fact]
    public void Turn_header_of_a_running_turn_shows_running_instead_of_elapsed()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);
        var chinese = SessionStrings.TurnHeader(3, At, TimeSpan.FromSeconds(4.2), running: true, 900, 80);

        using var english = new LanguageScope(UiLanguage.English);
        var englishHeader = SessionStrings.TurnHeader(3, At, TimeSpan.FromSeconds(4.2), running: true, 900, 80);

        Assert.Contains("运行中", chinese);
        Assert.Contains("Running", englishHeader);
    }

    [Fact]
    public void Elapsed_under_a_minute_shows_one_decimal_second_in_both_languages()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var chineseText = TextFormat.Elapsed(TimeSpan.FromSeconds(4.2));

        using var english = new LanguageScope(UiLanguage.English);
        var englishText = TextFormat.Elapsed(TimeSpan.FromSeconds(4.2));

        Assert.Equal("4.2 秒", chineseText);
        Assert.Equal("4.2s", englishText);
    }

    [Fact]
    public void Elapsed_beyond_a_minute_shows_minutes_and_zero_padded_seconds_in_both_languages()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var chineseMinute = TextFormat.Elapsed(TimeSpan.FromSeconds(65));
        var chineseLong = TextFormat.Elapsed(TimeSpan.FromSeconds(185));

        using var english = new LanguageScope(UiLanguage.English);
        var englishMinute = TextFormat.Elapsed(TimeSpan.FromSeconds(65));
        var englishLong = TextFormat.Elapsed(TimeSpan.FromSeconds(185));

        Assert.Equal("1 分 05 秒", chineseMinute);
        Assert.Equal("3 分 05 秒", chineseLong);
        Assert.Equal("1m 05s", englishMinute);
        Assert.Equal("3m 05s", englishLong);
    }

    [Theory]
    [InlineData(false, 59.94, "59.9s")]
    [InlineData(true, 59.94, "59.9 秒")]
    [InlineData(false, 59.96, "1m 00s")]
    [InlineData(true, 59.96, "1 分 00 秒")]
    [InlineData(false, 60.0, "1m 00s")]
    [InlineData(true, 60.0, "1 分 00 秒")]
    [InlineData(false, 119.6, "1m 59s")]
    [InlineData(true, 119.6, "1 分 59 秒")]
    public void Elapsed_around_the_minute_boundary_never_shows_a_rounded_up_sixty(bool chinese, double seconds, string expected)
    {
        using var _ = new LanguageScope(chinese ? UiLanguage.Chinese : UiLanguage.English);

        var text = TextFormat.Elapsed(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("60.0", text);
        Assert.DoesNotContain("60", text.Replace("00", string.Empty));
    }

    [Fact]
    public void Thousands_group_with_a_comma_whatever_the_current_culture_is()
    {
        using var _ = new LanguageScope(UiLanguage.English);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "zh-CN", "fr-FR" })
            {
                var changed = false;
                try
                {
                    CultureInfo.CurrentCulture = new CultureInfo(name);
                    changed = true;
                }
                catch (Exception exception) when (exception is CultureNotFoundException or ArgumentException)
                {
                    // Invariant-globalization builds may not be able to switch culture at all; the rule still holds:
                    // the formatted result must be identical anyway.
                }

                try
                {
                    Assert.Equal("1,234,567", TextFormat.Number(1234567));
                    Assert.Equal("1,234,567", TextFormat.Number((long?)1234567));
                }
                finally
                {
                    if (changed)
                    {
                        CultureInfo.CurrentCulture = saved;
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Identifiers_are_never_grouped()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            }
            catch (Exception exception) when (exception is CultureNotFoundException or ArgumentException)
            {
            }

            Assert.Equal("49712", TextFormat.Id(49712));
            Assert.Equal("1234567", TextFormat.Id(1234567));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void File_tab_count_names_carry_the_badge_number()
    {
        using (new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Equal("全部文件（348）", InspectorStrings.FilesAllCountName(348));
            Assert.Equal("已改动文件（12）", InspectorStrings.FilesChangedCountName(12));
        }

        // The number reaches assistive technology ungrouped, exactly as the badge draws it.
        using (new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("All files (348)", InspectorStrings.FilesAllCountName(348));
            Assert.Equal("Changed files (12345)", InspectorStrings.FilesChangedCountName(12345));
        }
    }

    [Fact]
    public void English_plural_picks_one_for_exactly_one_and_other_otherwise()
    {
        using var _ = new LanguageScope(UiLanguage.English);

        var one = LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, 1, TextFormat.Number(1)).Resolve();
        var two = LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, 2, TextFormat.Number(2)).Resolve();
        var zero = LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, 0, TextFormat.Number(0)).Resolve();

        Assert.Equal("1 file", one);
        Assert.Equal("2 files", two);
        Assert.Equal("0 files", zero);
    }

    [Fact]
    public void Chinese_plural_keeps_the_same_wording_for_one_and_many()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);

        var one = LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, 1, TextFormat.Number(1)).Resolve();
        var two = LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, 2, TextFormat.Number(2)).Resolve();

        Assert.Equal("1 个文件", one);
        Assert.Equal("2 个文件", two);
    }

    [Fact]
    public void An_unreported_token_count_is_the_none_mark_and_never_zero()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal("—", SessionStrings.TokenCount(null));
        Assert.Equal("输入 — · 输出 80", SessionStrings.TokenSummary(null, 80));
        Assert.Equal("—", TextFormat.Number((long?)null));
    }

    [Fact]
    public void A_literal_never_follows_the_language()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var chineseText = LocText.Literal("C:\\work\\repo").Resolve();

        using var english = new LanguageScope(UiLanguage.English);
        var englishText = LocText.Literal("C:\\work\\repo").Resolve();

        Assert.Equal("C:\\work\\repo", chineseText);
        Assert.Equal("C:\\work\\repo", englishText);
    }

    [Fact]
    public void Default_loc_text_is_empty()
    {
        Assert.True(default(LocText).IsEmpty);
        Assert.Equal(string.Empty, default(LocText).Resolve());
        Assert.False(LocText.Literal("x").IsEmpty);
        Assert.False(new LocText(InspectorStrings.FilesAll).IsEmpty);
    }

    [Fact]
    public void Loc_text_equality_compares_key_and_arguments()
    {
        var left = new LocText(InspectorStrings.FilesMissing, LocText.Literal("a"));
        var same = new LocText(InspectorStrings.FilesMissing, LocText.Literal("a"));
        var otherArg = new LocText(InspectorStrings.FilesMissing, LocText.Literal("b"));
        var otherKey = new LocText(InspectorStrings.FilesTooLarge, LocText.Literal("a"));

        Assert.True(left.Equals(same));
        Assert.True(left == same);
        Assert.False(left != same);
        Assert.Equal(left.GetHashCode(), same.GetHashCode());
        Assert.False(left.Equals(otherArg));
        Assert.False(left.Equals(otherKey));
        Assert.False(left.Equals("not a LocText"));
    }

    [Fact]
    public void A_nested_loc_text_resolves_in_the_current_language()
    {
        LocText Build() => new(SessionStrings.ReadFailedKey, new LocText(InspectorStrings.FilesAll));

        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var chineseText = Build().Resolve();

        using var english = new LanguageScope(UiLanguage.English);
        var englishText = Build().Resolve();

        Assert.Equal("读取会话失败：全部", chineseText);
        Assert.Equal("Could not read the session: All", englishText);
    }

    [Fact]
    public void Process_id_is_never_grouped()
    {
        using var chinese = new LanguageScope(UiLanguage.Chinese);
        var chineseText = ShellStrings.ProcessId(49712);

        using var english = new LanguageScope(UiLanguage.English);
        var englishText = ShellStrings.ProcessId(49712);

        Assert.Equal("进程 49712", chineseText);
        Assert.Equal("Process 49712", englishText);
    }

    [Fact]
    public void Review_line_is_an_ungrouped_L_number_or_range()
    {
        Assert.Equal("L1", InspectorStrings.ReviewLine(1, 1));
        Assert.Equal("L49712–49713", InspectorStrings.ReviewLine(49712, 49713));
    }

    [Fact]
    public void Turn_header_with_nothing_reported_names_the_round_and_shows_none_marks()
    {
        using var _ = new LanguageScope(UiLanguage.Chinese);

        var header = SessionStrings.TurnHeader(3, At, elapsed: null, running: false, null, null);

        Assert.Contains("第 3 轮", header);
        Assert.Contains("—", header);
    }

    [Fact]
    public void Thousands_group_but_identifiers_do_not()
    {
        Assert.Equal("49,712", TextFormat.Number(49712));
        Assert.Equal("49712", TextFormat.Id(49712));
    }
}
