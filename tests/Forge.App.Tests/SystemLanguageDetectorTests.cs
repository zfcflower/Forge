using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// "Follow the system" asks the platform first (<c>IPlatformSettings.PreferredApplicationLanguage</c>), because that is the
/// only source that still says something under <c>InvariantGlobalization</c>; the registry and the POSIX variables remain
/// as the fallbacks for platforms that report nothing. The cases below pin that precedence, including the one it exists
/// for: a Git Bash session exports <c>LANG=en_US.UTF-8</c> on a Chinese Windows.
/// </summary>
public sealed class SystemLanguageDetectorTests
{
    private static SystemLanguageDetector.Inputs Inputs(
        string? platform = null,
        string? windows = null,
        string? lcAll = null,
        string? lcMessages = null,
        string? lang = null) =>
        new(platform, CurrentUiCulture: "", InstalledUiCulture: "", windows, lcAll, lcMessages, lang);

    [Fact]
    public void The_platform_answer_decides_before_anything_else()
    {
        // Windows says German, the shell exports a Chinese locale: the OS wins, and "not Chinese" means English.
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs(platform: "de-DE", lang: "zh_CN.UTF-8")));

        // A Chinese platform answer wins over an English locale export just as well.
        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(Inputs(platform: "zh-Hans-CN", lang: "en_US.UTF-8")));
    }

    [Fact]
    public void An_empty_platform_answer_falls_through_to_the_registry_and_then_the_locale()
    {
        // X11 and macOS report nothing under invariant globalization, so the older chain still decides.
        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(Inputs(windows: "zh-CN", lang: "en_US.UTF-8")));
        Assert.Equal(UiLanguage.Chinese, SystemLanguageDetector.Resolve(Inputs(lang: "zh_CN.UTF-8")));
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs(lang: "en_US.UTF-8")));
    }

    [Fact]
    public void Nothing_to_go_on_means_the_built_in_default()
    {
        Assert.Equal(UiLanguage.English, SystemLanguageDetector.Resolve(Inputs()));
    }
}
