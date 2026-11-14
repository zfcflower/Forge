using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins <see cref="TimelineStrings.Describe"/> (forge-app spec, "聊天时间轴(W13)"): an idle node reads as its bare name,
/// a busy one names the state word, the result is resolved in the current language on every call, and every name and
/// state key of the table is registered in Chinese and English.
/// </summary>
public sealed class TimelineStringsTests
{
    private static readonly TimelineNodeKind[] AllKinds = Enum.GetValues<TimelineNodeKind>();
    private static readonly TimelineNodeState[] NonIdleStates =
        [TimelineNodeState.Running, TimelineNodeState.Failed, TimelineNodeState.Warning];

    [Fact]
    public void An_idle_node_describes_as_its_bare_name()
    {
        foreach (var kind in AllKinds)
        {
            using (new LanguageScope(UiLanguage.Chinese))
            {
                Assert.Equal(Loc.Instance[TimelineNodeMap.NameKey(kind)], TimelineStrings.Describe(kind, TimelineNodeState.Idle));
            }

            using (new LanguageScope(UiLanguage.English))
            {
                Assert.Equal(Loc.Instance[TimelineNodeMap.NameKey(kind)], TimelineStrings.Describe(kind, TimelineNodeState.Idle));
            }
        }
    }

    [Fact]
    public void A_non_idle_node_names_both_itself_and_its_state()
    {
        foreach (var language in new[] { UiLanguage.Chinese, UiLanguage.English })
        {
            using var scope = new LanguageScope(language);
            foreach (var kind in AllKinds)
            {
                foreach (var state in NonIdleStates)
                {
                    var name = Loc.Instance[TimelineNodeMap.NameKey(kind)];
                    var word = Loc.Instance[TimelineNodeMap.StateKey(state)];
                    var described = TimelineStrings.Describe(kind, state);

                    Assert.Contains(name, described);
                    Assert.Contains(word, described);
                    Assert.NotEqual(name, described);
                }
            }
        }
    }

    [Fact]
    public void Describe_resolves_in_the_current_language_on_every_call()
    {
        using var scope = new LanguageScope(UiLanguage.English);
        var english = TimelineStrings.Describe(TimelineNodeKind.ReadFile, TimelineNodeState.Running);
        Assert.Contains("Read file", english);
        Assert.Contains("running", english);

        Loc.Instance.Language = UiLanguage.Chinese;
        var chinese = TimelineStrings.Describe(TimelineNodeKind.ReadFile, TimelineNodeState.Running);

        Assert.NotEqual(english, chinese);
        Assert.Contains(Loc.Instance[TimelineStrings.NameReadFile], chinese);
        Assert.Contains(Loc.Instance[TimelineStrings.StateRunning], chinese);
    }

    [Fact]
    public void Every_name_key_of_the_table_is_registered_in_both_languages()
    {
        foreach (var kind in AllKinds)
        {
            AssertRegistered(TimelineNodeMap.NameKey(kind));
        }
    }

    [Fact]
    public void Every_state_key_of_the_table_is_registered_in_both_languages()
    {
        AssertRegistered(TimelineStrings.StateRunning);
        AssertRegistered(TimelineStrings.StateFailed);
        AssertRegistered(TimelineStrings.StateWarning);
    }

    /// <summary>A registered key resolves in both languages, is never the missing-key bracket form, and differs per language.</summary>
    private static void AssertRegistered(string key)
    {
        string chinese;
        string english;
        using (new LanguageScope(UiLanguage.Chinese))
        {
            chinese = Loc.Instance[key];
        }

        using (new LanguageScope(UiLanguage.English))
        {
            english = Loc.Instance[key];
        }

        Assert.NotEmpty(chinese);
        Assert.NotEmpty(english);
        Assert.NotEqual("[" + key + "]", chinese);
        Assert.NotEqual("[" + key + "]", english);
        Assert.NotEqual(chinese, english);
    }
}
