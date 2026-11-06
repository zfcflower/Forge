using System.Reflection;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>Pins that every <see cref="AppearanceStrings"/> key has a Chinese and an English text and that they differ.</summary>
public sealed class AppearanceStringsTests : IDisposable
{
    private readonly UiLanguage previous = Loc.Instance.Language;

    public void Dispose() => Loc.Instance.Language = previous;

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(AppearanceStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string key)
            {
                data.Add(key);
            }
        }

        return data;
    }

    [Fact]
    public void The_table_exposes_the_expected_number_of_keys()
    {
        Assert.Equal(9, typeof(AppearanceStrings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Count(static field => field.IsLiteral));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_key_is_registered_in_both_languages(string key)
    {
        Assert.StartsWith("appearance.", key, StringComparison.Ordinal);
        Assert.Contains(key, Loc.Keys);

        Loc.Instance.Language = UiLanguage.Chinese;
        var chinese = Loc.Instance[key];
        Loc.Instance.Language = UiLanguage.English;
        var english = Loc.Instance[key];

        Assert.False(string.IsNullOrWhiteSpace(chinese));
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.NotEqual("[" + key + "]", chinese);
        Assert.NotEqual("[" + key + "]", english);
        Assert.NotEqual(chinese, english);
    }

    [Fact]
    public void Registering_the_table_again_is_a_no_op()
    {
        var before = Loc.Keys.Count;

        AppearanceStrings.Register();

        Assert.Equal(before, Loc.Keys.Count);
    }
}
