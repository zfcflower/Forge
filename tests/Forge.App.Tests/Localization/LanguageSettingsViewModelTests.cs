using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins the <c>language</c> settings section (a thin view over <see cref="AppPreferences.Language"/>): checking a
/// radio writes the preference, unchecking writes nothing, an identical value notifies nothing, an outside edit
/// re-syncs the radios, a language switch re-reads every text, and <see cref="LanguageSettingsViewModel.Dispose"/>
/// drops both subscriptions. Built on a bare <c>new AppPreferences()</c>, like the composition root owns it.
/// </summary>
public sealed class LanguageSettingsViewModelTests
{
    [Fact]
    public void Checking_a_radio_writes_the_preference()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);

        section.IsChinese = true;
        Assert.Equal(UiLanguage.Chinese, preferences.Language);

        section.IsEnglish = true;
        Assert.Equal(UiLanguage.English, preferences.Language);

        section.IsSystem = true;
        Assert.Equal(UiLanguage.System, preferences.Language);
    }

    [Fact]
    public void Unchecking_a_radio_writes_nothing()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);
        section.IsChinese = true;

        section.IsChinese = false;

        Assert.Equal(UiLanguage.Chinese, preferences.Language);

        section.IsEnglish = false;

        Assert.Equal(UiLanguage.Chinese, preferences.Language);
    }

    [Fact]
    public void Setting_the_same_language_notifies_nothing()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);
        section.IsChinese = true;
        var sectionNotifications = new List<string?>();
        var preferenceNotifications = new List<string?>();
        section.PropertyChanged += (_, e) => sectionNotifications.Add(e.PropertyName);
        preferences.PropertyChanged += (_, e) => preferenceNotifications.Add(e.PropertyName);

        section.Language = UiLanguage.Chinese;
        section.IsChinese = true;

        Assert.Empty(sectionNotifications);
        Assert.Empty(preferenceNotifications);
    }

    [Fact]
    public void An_outside_edit_of_the_preference_resyncs_the_radios()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        preferences.Language = UiLanguage.English;

        Assert.True(section.IsEnglish);
        Assert.False(section.IsChinese);
        Assert.False(section.IsSystem);
        Assert.Contains(nameof(LanguageSettingsViewModel.IsEnglish), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.IsChinese), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.IsSystem), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.Language), raised);
    }

    [Fact]
    public void A_language_switch_rereads_every_section_text()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);
        using var pin = new LanguageScope(UiLanguage.Chinese);
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Loc.Instance.Language = UiLanguage.English;

        Assert.Contains(nameof(LanguageSettingsViewModel.SystemText), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.SystemResolvedText), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.ChineseText), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.EnglishText), raised);
        Assert.Contains(nameof(LanguageSettingsViewModel.Hint), raised);
    }

    [Fact]
    public void Each_language_keeps_its_own_name_in_both_interfaces()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);

        using (var chinese = new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Equal("跟随系统", section.SystemText);
            Assert.Equal("中文", section.ChineseText);
            Assert.Equal("English", section.EnglishText);
            _ = chinese;
        }

        using (var english = new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("Follow system", section.SystemText);
            Assert.Equal("Chinese", section.ChineseText);
            Assert.Equal("English", section.EnglishText);
            _ = english;
        }
    }

    [Fact]
    public void The_follow_system_line_names_the_resolved_language()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);

        // The test assembly pins "follow the system" to Chinese (TestLanguageSetup).
        using (var english = new LanguageScope(UiLanguage.English))
        {
            Assert.Equal("Follow system (currently Chinese)", section.SystemResolvedText);
            _ = english;
        }

        using (var chinese = new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Equal("跟随系统(当前:中文)", section.SystemResolvedText);
            _ = chinese;
        }
    }

    [Fact]
    public void Dispose_drops_both_subscriptions_and_is_idempotent()
    {
        var preferences = new AppPreferences();
        var section = new LanguageSettingsViewModel(preferences);
        using var pin = new LanguageScope(UiLanguage.Chinese);
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        section.Dispose();
        section.Dispose();

        Loc.Instance.Language = UiLanguage.English;
        preferences.Language = UiLanguage.English;

        Assert.Empty(raised);
        Assert.True(section.IsEnglish);
    }

    [Fact]
    public void Setting_the_language_property_applies_immediately()
    {
        var preferences = new AppPreferences();
        using var section = new LanguageSettingsViewModel(preferences);
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        section.Language = UiLanguage.English;

        Assert.Equal(UiLanguage.English, preferences.Language);
        Assert.True(section.IsEnglish);
        Assert.Contains(nameof(LanguageSettingsViewModel.Language), raised);
    }
}
