using System.ComponentModel;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Logging;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Pins the live string table (forge-app spec, "偏好、文案与设置分区"): a key carries a Chinese and an English text, the
/// language never resolves to <see cref="UiLanguage.System"/>, a switch notifies bound text through <c>Item[]</c> and
/// view models through <see cref="Loc.LanguageChanged"/>, a missing key is visible as the bracketed key and reported
/// once, and two tables claiming one key with different texts fail at registration.
/// </summary>
public sealed class LocalizationTests : IDisposable
{
    private readonly UiLanguage previousLanguage = Loc.Instance.Language;

    /// <summary>Restores the process-wide language so one test cannot decide the next one's texts.</summary>
    public void Dispose() => Loc.Instance.Language = previousLanguage;

    [Fact]
    public void The_resolved_language_is_never_system()
    {
        Loc.Instance.Language = UiLanguage.System;

        Assert.NotEqual(UiLanguage.System, Loc.Instance.Language);
    }

    [Fact]
    public void Switching_the_language_raises_item_changes_and_language_changed()
    {
        var names = new List<string?>();
        var languageChanged = 0;
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => names.Add(e.PropertyName);
        void OnLanguageChanged(object? sender, EventArgs e) => languageChanged++;

        Loc.Instance.Language = UiLanguage.English;
        Loc.Instance.PropertyChanged += OnPropertyChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        try
        {
            Loc.Instance.Language = UiLanguage.Chinese;

            // "Item[]" is the documented notification; "Item" is what Avalonia's compiled indexer binding refreshes on.
            Assert.Equal(["Item[]", "Item"], names);
            Assert.Equal(1, languageChanged);
            Assert.Equal("设置", Loc.Instance[CoreStrings.SettingsTitle]);

            // Setting the language it already has is not a change.
            Loc.Instance.Language = UiLanguage.Chinese;

            Assert.Equal(2, names.Count);
            Assert.Equal(1, languageChanged);
        }
        finally
        {
            Loc.Instance.PropertyChanged -= OnPropertyChanged;
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
        }
    }

    [Fact]
    public void A_missing_key_returns_the_key_in_brackets()
    {
        Assert.Equal("[missing.key.for.the.test]", Loc.Instance["missing.key.for.the.test"]);
    }

    [Fact]
    public void A_missing_key_is_reported_once()
    {
        using var log = new LogCapture();

        _ = Loc.Instance["missing.reported.once"];
        _ = Loc.Instance["missing.reported.once"];
        _ = Loc.Instance["missing.reported.once"];

        Assert.Equal(1, log.Lines.Count(static line => line.Contains("missing.reported.once", StringComparison.Ordinal)));
    }

    [Fact]
    public void Registering_the_same_key_twice_with_the_same_texts_is_a_no_op()
    {
        Loc.Register("test.idempotent", "相同", "same");

        Loc.Register("test.idempotent", "相同", "same");

        Assert.Equal("same", Text("test.idempotent", UiLanguage.English));
    }

    [Fact]
    public void Registering_the_same_key_with_different_texts_throws()
    {
        Loc.Register("test.conflict", "第一", "first");

        Assert.Throws<InvalidOperationException>(() => Loc.Register("test.conflict", "第二", "first"));
        Assert.Throws<InvalidOperationException>(() => Loc.Register("test.conflict", "第一", "second"));

        // The first registration is what stays.
        Assert.Equal("第一", Text("test.conflict", UiLanguage.Chinese));
        Assert.Equal("first", Text("test.conflict", UiLanguage.English));
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("text", "  ")]
    public void Register_rejects_a_missing_text(string chinese, string english)
    {
        Assert.ThrowsAny<ArgumentException>(() => Loc.Register("test.empty", chinese, english));
    }

    [Fact]
    public void EnsureRegistered_leaves_every_key_with_a_chinese_and_an_english_text()
    {
        Loc.EnsureRegistered();

        var keys = Loc.Keys;
        Assert.NotEmpty(keys);
        Assert.Contains(CoreStrings.SettingsSectionProvider, keys);
        Assert.Contains(CoreStrings.SettingsPathMcp, keys);

        foreach (var key in keys)
        {
            Assert.NotEqual("[" + key + "]", Text(key, UiLanguage.Chinese));
            Assert.NotEqual("[" + key + "]", Text(key, UiLanguage.English));
        }
    }

    [Fact]
    public void The_shell_section_titles_are_translated()
    {
        Assert.Equal("模型提供商", Text(CoreStrings.SettingsSectionProvider, UiLanguage.Chinese));
        Assert.Equal("Model provider", Text(CoreStrings.SettingsSectionProvider, UiLanguage.English));
        Assert.Equal("路径与服务", Text(CoreStrings.SettingsSectionPaths, UiLanguage.Chinese));
        Assert.Equal("Paths and services", Text(CoreStrings.SettingsSectionPaths, UiLanguage.English));
    }

    [Fact]
    public void Format_fills_the_text_in_the_current_language_with_invariant_numbers()
    {
        Loc.Register("test.format", "第 {0} 轮 · {1:N0}", "turn {0} · {1:N0}");

        Loc.Instance.Language = UiLanguage.Chinese;
        Assert.Equal("第 3 轮 · 49,712", Loc.Instance.Format("test.format", 3, 49712));

        Loc.Instance.Language = UiLanguage.English;
        Assert.Equal("turn 3 · 49,712", Loc.Instance.Format("test.format", 3, 49712));
    }

    private static string Text(string key, UiLanguage language)
    {
        Loc.Instance.Language = language;
        return Loc.Instance[key];
    }
}
