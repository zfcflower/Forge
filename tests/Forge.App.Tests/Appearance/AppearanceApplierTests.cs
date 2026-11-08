using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.Appearance;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// Pins the applier contract of TASK-fonts over a stand-alone <see cref="ResourceDictionary"/> (no <c>Application</c>
/// needed): one owned child dictionary that is replaced whole, four triggering properties only, no-op applies, an invalid
/// family that keeps the previous one, and a disposed applier that stays quiet.
/// </summary>
public sealed class AppearanceApplierTests : IDisposable
{
    private readonly AppPreferences preferences = new();
    private readonly ResourceDictionary target = new();
    private readonly AppearanceApplier applier;

    public AppearanceApplierTests() => applier = new AppearanceApplier(preferences, target);

    public void Dispose() => applier.Dispose();

    [Fact]
    public void The_first_apply_writes_every_key_the_views_read()
    {
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultUiFontFamily)), Chain(Value<FontFamily>("FontUi")));
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultCodeFontFamily)), Chain(Value<FontFamily>("FontMono")));
        Assert.Equal(11, Value<double>("FontSizeXs"));
        Assert.Equal(12, Value<double>("FontSizeSm"));
        Assert.Equal(13, Value<double>("FontSizeBase"));
        Assert.Equal(14, Value<double>("FontSizeMd"));
        Assert.Equal(18, Value<double>("FontSizeLg"));
        Assert.Equal(22, Value<double>("FontSizeXl"));
        Assert.Equal(13, Value<double>("FontSizeCode"));
        // Fluent reads these two for its own controls and popups.
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultUiFontFamily)), Chain(Value<FontFamily>("ContentControlThemeFontFamily")));
        Assert.Equal(13, Value<double>("ControlContentThemeFontSize"));
    }

    [Fact]
    public void The_default_interface_family_starts_on_the_system_font_and_leaves_inter_out()
    {
        // The interface starts on the system chain, not the embedded Inter: Inter reads visibly larger at the same
        // pixel size because of its bigger x-height, so putting it back into the default would silently inflate every
        // label. Inter only comes in through an explicit "fonts:Inter#Inter" in the settings.
        var families = FontFamily.Parse(AppPreferences.DefaultUiFontFamily).FamilyNames;

        Assert.Equal("Segoe UI", families[0]);
        Assert.DoesNotContain(families, static family => family.Contains("Inter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_ui_size_change_rescales_the_ramp_and_the_fluent_keys_but_not_the_code_size()
    {
        var before = applier.Tokens;

        preferences.UiFontSize = 20;

        Assert.NotSame(before, applier.Tokens);
        Assert.Equal(20, Value<double>("FontSizeBase"));
        // 22 * 20/13 = 33.85, rounded away from zero.
        Assert.Equal(34, Value<double>("FontSizeXl"));
        Assert.Equal(20, Value<double>("ControlContentThemeFontSize"));
        Assert.Equal(13, Value<double>("FontSizeCode"));
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultCodeFontFamily)), Chain(Value<FontFamily>("FontMono")));
    }

    [Fact]
    public void A_code_size_change_touches_only_the_code_size()
    {
        var before = applier.Tokens;

        preferences.CodeFontSize = 16;

        Assert.NotSame(before, applier.Tokens);
        Assert.Equal(16, Value<double>("FontSizeCode"));
        Assert.Equal(13, Value<double>("FontSizeBase"));
        Assert.Equal(13, Value<double>("ControlContentThemeFontSize"));
    }

    [Fact]
    public void An_interface_family_change_rewrites_the_ui_family_but_not_the_code_family()
    {
        var before = applier.Tokens;

        preferences.UiFontFamily = "Georgia, serif";

        Assert.NotSame(before, applier.Tokens);
        Assert.Equal(Chain(FontFamily.Parse("Georgia, serif")), Chain(Value<FontFamily>("FontUi")));
        Assert.Equal(Chain(FontFamily.Parse("Georgia, serif")), Chain(Value<FontFamily>("ContentControlThemeFontFamily")));
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultCodeFontFamily)), Chain(Value<FontFamily>("FontMono")));
    }

    [Fact]
    public void Other_preferences_do_not_swap_the_tokens()
    {
        var before = applier.Tokens;

        preferences.Language = UiLanguage.Chinese;
        preferences.TerminalShell = "pwsh";
        preferences.TerminalArguments = "-NoLogo";

        Assert.Same(before, applier.Tokens);
        Assert.Single(target.MergedDictionaries);
    }

    [Fact]
    public void The_same_values_again_swap_nothing()
    {
        var before = applier.Tokens;

        preferences.UiFontSize = AppPreferences.DefaultUiFontSize;
        preferences.CodeFontFamily = AppPreferences.DefaultCodeFontFamily;
        applier.Apply();

        Assert.Same(before, applier.Tokens);
        Assert.Single(target.MergedDictionaries);
    }

    [Fact]
    public void The_target_never_accumulates_child_dictionaries()
    {
        preferences.UiFontSize = 20;
        preferences.CodeFontSize = 22;
        preferences.UiFontFamily = "Georgia";
        preferences.CodeFontFamily = "Menlo";

        Assert.Single(target.MergedDictionaries);
        Assert.Same(applier.Tokens, target.MergedDictionaries[0]);
    }

    [Fact]
    public void A_family_without_a_usable_name_keeps_the_previous_family()
    {
        var before = applier.Tokens;
        var previous = Chain(Value<FontFamily>("FontUi"));

        // The setter keeps "," verbatim; it parses into a chain with no family name at all.
        preferences.UiFontFamily = ",";

        Assert.Equal(",", preferences.UiFontFamily);
        Assert.Same(before, applier.Tokens);
        Assert.Equal(previous, Chain(Value<FontFamily>("FontUi")));
    }

    [Fact]
    public void Dispose_twice_does_not_throw_and_stops_the_applier()
    {
        var before = applier.Tokens;

        applier.Dispose();
        applier.Dispose();
        preferences.UiFontSize = 25;

        Assert.Same(before, applier.Tokens);
        Assert.Single(target.MergedDictionaries);
    }

    [Fact]
    public void A_family_that_is_bad_on_the_first_apply_falls_back_to_the_default_chain()
    {
        var fresh = new AppPreferences { UiFontFamily = ",", CodeFontFamily = "," };
        var dictionary = new ResourceDictionary();
        using var other = new AppearanceApplier(fresh, dictionary);

        Assert.True(dictionary.TryGetResource("FontUi", null, out var ui));
        Assert.True(dictionary.TryGetResource("FontMono", null, out var mono));
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultUiFontFamily)), Chain(Assert.IsType<FontFamily>(ui)));
        Assert.Equal(Chain(FontFamily.Parse(AppPreferences.DefaultCodeFontFamily)), Chain(Assert.IsType<FontFamily>(mono)));
    }

    [Fact]
    public void A_repeated_bad_family_text_is_reported_once()
    {
        var listener = new CountingListener();
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            preferences.UiFontFamily = ",";
            var afterFirst = listener.Count;
            applier.Apply();
            applier.Apply();
            preferences.UiFontSize = 14;
            preferences.UiFontSize = 15;

            Assert.True(afterFirst >= 1);
            Assert.Equal(afterFirst, listener.Count);
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
    }

    private sealed class CountingListener : System.Diagnostics.TraceListener
    {
        public int Count { get; private set; }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message?.Contains("[appearance]", StringComparison.Ordinal) == true)
            {
                Count++;
            }
        }
    }

    private T Value<T>(string key)
    {
        Assert.True(target.TryGetResource(key, null, out var value), key + " was not written");
        return Assert.IsType<T>(value);
    }

    private static string Chain(FontFamily family) => string.Join("|", family.FamilyNames);
}
