using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins the branch matrix of <see cref="SystemLanguageDetector.Resolve"/> (task decision: culture names, then the
/// Windows registry, then LC_ALL, LC_MESSAGES, LANG, then English) plus the <see cref="SystemLanguageDetector.IsChinese"/>
/// boundary and a smoke test that the live read never throws and never resolves to
/// <see cref="UiLanguage.System"/>. Pure function: no machine state is involved except in the last test.
/// </summary>
public sealed class SystemLanguageDetectorTests
{
    private static SystemLanguageDetector.Inputs Inputs(
        string? currentUiCulture = null,
        string? installedUiCulture = null,
        string? windowsUiLanguage = null,
        string? lcAll = null,
        string? lcMessages = null,
        string? lang = null) =>
        new(currentUiCulture, installedUiCulture, windowsUiLanguage, lcAll, lcMessages, lang);

    [Fact]
    public void Empty_inputs_resolve_to_English()
    {
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs()));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("zh-CN")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hans-CN")]
    [InlineData("ZH-tw")]
    [InlineData("zh_CN.UTF-8")]
    public void Chinese_locale_variables_resolve_to_Chinese(string lang)
    {
        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(Inputs(lang: lang)));
    }

    [Theory]
    [InlineData("zhx")]
    [InlineData("zh1")]
    [InlineData("en")]
    [InlineData("en_US.UTF-8")]
    [InlineData("de-DE")]
    public void Non_Chinese_locale_variables_resolve_to_English(string lang)
    {
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs(lang: lang)));
    }

    [Fact]
    public void A_Chinese_current_ui_culture_wins_over_everything_else()
    {
        var inputs = Inputs(
            currentUiCulture: "zh-CN",
            installedUiCulture: "en-US",
            windowsUiLanguage: "en-US",
            lcAll: "en_US.UTF-8",
            lcMessages: "en_US.UTF-8",
            lang: "en_US.UTF-8");

        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(inputs));
    }

    [Fact]
    public void A_Chinese_installed_ui_culture_wins_over_registry_and_variables()
    {
        var inputs = Inputs(
            currentUiCulture: "en-US",
            installedUiCulture: "zh-TW",
            windowsUiLanguage: "en-US",
            lcAll: "en_US.UTF-8",
            lang: "en_US.UTF-8");

        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(inputs));
    }

    [Fact]
    public void A_present_registry_value_beats_the_environment_even_when_it_says_English()
    {
        var inputs = Inputs(windowsUiLanguage: "en-US", lang: "zh_CN.UTF-8");

        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(inputs));
    }

    [Fact]
    public void A_present_registry_value_beats_the_environment_when_it_says_Chinese()
    {
        var inputs = Inputs(windowsUiLanguage: "zh-CN", lang: "en_US.UTF-8");

        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(inputs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_registry_value_falls_through_to_the_environment(string? registry)
    {
        Assert.Equal(
            UiLanguage.Chinese,
            SystemLanguageDetector.Resolve(Inputs(windowsUiLanguage: registry, lang: "zh_CN.UTF-8")));
        Assert.Equal(
            UiLanguage.English,
            SystemLanguageDetector.Resolve(Inputs(windowsUiLanguage: registry, lang: "en_US.UTF-8")));
    }

    [Fact]
    public void Lc_all_beats_lang()
    {
        Assert.Equal(
            UiLanguage.English,
            SystemLanguageDetector.Resolve(Inputs(lcAll: "en_US.UTF-8", lang: "zh_CN.UTF-8")));
        Assert.Equal(
            UiLanguage.Chinese,
            SystemLanguageDetector.Resolve(Inputs(lcAll: "zh_CN.UTF-8", lang: "en_US.UTF-8")));
    }

    [Fact]
    public void A_blank_lc_all_falls_through_to_lc_messages()
    {
        Assert.Equal(
            UiLanguage.Chinese,
            SystemLanguageDetector.Resolve(Inputs(lcAll: "  ", lcMessages: "zh_CN.UTF-8", lang: "en_US.UTF-8")));
        Assert.Equal(
            UiLanguage.English,
            SystemLanguageDetector.Resolve(Inputs(lcAll: "", lcMessages: "en_US.UTF-8", lang: "zh_CN.UTF-8")));
    }

    [Fact]
    public void A_blank_lc_messages_falls_through_to_lang()
    {
        Assert.Equal(
            UiLanguage.Chinese,
            SystemLanguageDetector.Resolve(Inputs(lcMessages: " ", lang: "zh_CN.UTF-8")));
    }

    [Fact]
    public void All_blank_variables_resolve_to_English()
    {
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs(lcAll: " ", lcMessages: "", lang: "  ")));
    }

    public static IEnumerable<object?[]> NeverSystemCases =>
        new List<object?[]>
        {
            new object?[] { null, null, null, null, null, null },
            new object?[] { null, null, null, null, null, "zh_CN.UTF-8" },
            new object?[] { null, null, null, null, null, "en_US.UTF-8" },
            new object?[] { "zh-CN", null, "en-US", "en_US", null, "en_US" },
            new object?[] { "en-US", "zh-CN", "en-US", null, null, null },
            new object?[] { null, null, "en-US", null, null, "zh_CN.UTF-8" },
            new object?[] { null, null, "zh-CN", null, null, "en_US.UTF-8" },
            new object?[] { null, null, " ", "en_US", "zh_CN", "zh_CN" },
            new object?[] { null, null, null, "", " ", null },
            new object?[] { "fr-FR", "de-DE", "ja-JP", "C", null, "POSIX" },
        };

    [Theory]
    [MemberData(nameof(NeverSystemCases))]
    public void Resolve_never_returns_system(
        string? currentUiCulture,
        string? installedUiCulture,
        string? windowsUiLanguage,
        string? lcAll,
        string? lcMessages,
        string? lang)
    {
        var inputs = Inputs(currentUiCulture, installedUiCulture, windowsUiLanguage, lcAll, lcMessages, lang);
        Assert.NotEqual(UiLanguage.System, SystemLanguageDetector.Resolve(inputs));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("z", false)]
    [InlineData("zhx", false)]
    [InlineData("zh1", false)]
    [InlineData("en", false)]
    [InlineData("zh", true)]
    [InlineData("ZH", true)]
    [InlineData("zh-CN", true)]
    [InlineData("zh_CN.UTF-8", true)]
    public void IsChinese_matches_zh_prefixed_cultures_only(string? culture, bool expected)
    {
        Assert.Equal(expected, SystemLanguageDetector.IsChinese(culture));
    }

    [Fact]
    public void IsChinese_treats_a_bare_zh_dash_as_Chinese()
    {
        // Degenerate input: the implementation only checks the "zh" prefix plus a separator, and no real source
        // (culture name, registry value, locale variable) ever yields exactly "zh-". Pinned so a future
        // tightening of the check shows up here deliberately.
        Assert.True(SystemLanguageDetector.IsChinese("zh-"));
    }

    [Fact]
    public void ReadCurrent_never_throws_and_never_resolves_to_system()
    {
        var exception = Record.Exception(() => SystemLanguageDetector.ReadCurrent());

        Assert.Null(exception);
        var current = SystemLanguageDetector.ReadCurrent();
        Assert.NotEqual(UiLanguage.System, SystemLanguageDetector.Resolve(current));
    }
}
