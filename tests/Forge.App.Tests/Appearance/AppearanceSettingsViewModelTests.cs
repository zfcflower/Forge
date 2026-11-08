using System.ComponentModel;
using Forge.App.Appearance;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// Pins the editor contract of TASK-fonts without a UI: a family is applied only on an explicit commit, a size at once,
/// each group resets alone, external preference changes echo back, the cached texts follow the language, and a disposed
/// view model is detached from both the preferences and <see cref="Loc"/>.
/// </summary>
public sealed class AppearanceSettingsViewModelTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>The only properties a language switch may refresh: the texts cached from <see cref="Loc"/>.</summary>
    private static readonly string[] CachedTexts =
    [
        nameof(FontGroupViewModel.Title),
        nameof(FontGroupViewModel.FamilyLabel),
        nameof(FontGroupViewModel.SizeLabel),
        nameof(FontGroupViewModel.ResetText),
        nameof(FontGroupViewModel.PreviewText),
    ];

    private readonly UiLanguage previousLanguage = Loc.Instance.Language;
    private readonly AppPreferences preferences = new();
    private readonly AppearanceSettingsViewModel viewModel;

    public AppearanceSettingsViewModelTests()
    {
        Loc.Instance.Language = UiLanguage.English;
        viewModel = new AppearanceSettingsViewModel(preferences, new SystemFontCatalog(static () => ["Beta", "Alpha"]));
    }

    public void Dispose()
    {
        viewModel.Dispose();
        Loc.Instance.Language = previousLanguage;
    }

    [Fact]
    public void The_editors_start_from_the_stored_preferences()
    {
        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, viewModel.Code.FamilyText);
        Assert.Equal(13m, viewModel.Ui.Size);
        Assert.Equal(13m, viewModel.Code.Size);
        Assert.Equal(13d, viewModel.Ui.PreviewSize);
    }

    [Fact]
    public async Task The_installed_families_reach_both_groups()
    {
        await viewModel.FamiliesLoaded.WaitAsync(Budget);

        Assert.Equal(["Alpha", "Beta"], viewModel.Ui.Families);
        Assert.Equal(["Alpha", "Beta"], viewModel.Code.Families);
    }

    [Fact]
    public async Task A_failing_catalog_leaves_an_editable_section_with_no_families()
    {
        using var scope = new Scope();
        var failing = new AppearanceSettingsViewModel(
            preferences,
            new SystemFontCatalog(static () => throw new InvalidOperationException("no fonts")));
        scope.Add(failing);

        await failing.FamiliesLoaded.WaitAsync(Budget);

        Assert.Empty(failing.Ui.Families);
        Assert.Empty(failing.Code.Families);
        failing.Ui.FamilyText = "Georgia";
        failing.Ui.CommitFamily();
        Assert.Equal("Georgia", preferences.UiFontFamily);
    }

    [Fact]
    public void Typing_a_family_does_not_write_the_preference_until_it_is_committed()
    {
        var changes = new List<string?>();
        preferences.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.Ui.FamilyText = "G";
        viewModel.Ui.FamilyText = "Ge";
        viewModel.Ui.FamilyText = "Georgia";

        Assert.Empty(changes);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);

        viewModel.Ui.CommitFamily();

        Assert.Equal(["UiFontFamily"], changes);
        Assert.Equal("Georgia", preferences.UiFontFamily);
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, preferences.CodeFontFamily);
    }

    [Fact]
    public void Committing_the_code_group_writes_only_the_code_family()
    {
        viewModel.Code.FamilyText = "JetBrains Mono, monospace";
        viewModel.Code.CommitFamily();

        Assert.Equal("JetBrains Mono, monospace", preferences.CodeFontFamily);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
    }

    [Fact]
    public void Committing_an_empty_family_falls_back_to_the_default_and_the_editor_shows_it()
    {
        preferences.UiFontFamily = "Georgia";

        viewModel.Ui.FamilyText = "   ";
        viewModel.Ui.CommitFamily();

        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
    }

    [Fact]
    public void Committing_a_padded_family_shows_the_stored_value_in_the_editor()
    {
        viewModel.Ui.FamilyText = "  Georgia  ";
        viewModel.Ui.CommitFamily();

        Assert.Equal(preferences.UiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal("Georgia", viewModel.Ui.FamilyText);
    }

    [Fact]
    public void Committing_the_default_again_keeps_the_editor_on_the_stored_value()
    {
        viewModel.Code.FamilyText = AppPreferences.DefaultCodeFontFamily;
        viewModel.Code.CommitFamily();

        Assert.Equal(AppPreferences.DefaultCodeFontFamily, viewModel.Code.FamilyText);
    }

    [Fact]
    public void A_null_family_text_is_treated_as_empty()
    {
        viewModel.Ui.FamilyText = null!;

        Assert.Equal(string.Empty, viewModel.Ui.FamilyText);
        viewModel.Ui.CommitFamily();
        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
    }

    [Fact]
    public void A_committed_family_updates_the_preview_family()
    {
        var raised = new List<string?>();
        viewModel.Ui.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.Ui.FamilyText = "Georgia, serif";
        viewModel.Ui.CommitFamily();

        Assert.Contains(nameof(FontGroupViewModel.PreviewFamily), raised);
        Assert.Equal(["Georgia", "serif"], viewModel.Ui.PreviewFamily.FamilyNames.Select(static n => n.ToString()));
    }

    [Fact]
    public void A_family_without_a_usable_name_does_not_throw_in_the_preview()
    {
        viewModel.Ui.FamilyText = ",";
        var exception = Record.Exception(viewModel.Ui.CommitFamily);

        Assert.Null(exception);
        Assert.Equal(",", preferences.UiFontFamily);
        Assert.NotNull(viewModel.Ui.PreviewFamily);
    }

    [Fact]
    public void A_size_is_applied_immediately_without_a_commit()
    {
        viewModel.Ui.Size = 20m;
        viewModel.Code.Size = 16m;

        Assert.Equal(20d, preferences.UiFontSize);
        Assert.Equal(16d, preferences.CodeFontSize);
        Assert.Equal(20d, viewModel.Ui.PreviewSize);
        Assert.Equal(16d, viewModel.Code.PreviewSize);
    }

    [Theory]
    [InlineData(1, 9)]
    [InlineData(9, 9)]
    [InlineData(28, 28)]
    [InlineData(400, 28)]
    public void An_out_of_range_size_is_clamped_and_echoed_back(int requested, int expected)
    {
        viewModel.Ui.Size = requested;

        Assert.Equal(expected, preferences.UiFontSize);
        Assert.Equal((decimal)expected, viewModel.Ui.Size);
        Assert.Equal((double)expected, viewModel.Ui.PreviewSize);
    }

    [Theory]
    [InlineData("12.5", 13)]
    [InlineData("14.5", 15)]
    [InlineData("12.4", 12)]
    public void A_fractional_size_rounds_away_from_zero(string requested, int expected)
    {
        viewModel.Ui.Size = decimal.Parse(requested, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, preferences.UiFontSize);
        Assert.Equal((decimal)expected, viewModel.Ui.Size);
    }

    [Fact]
    public void A_cleared_size_is_ignored_and_keeps_the_stored_value()
    {
        viewModel.Ui.Size = 17m;
        var changes = new List<string?>();
        preferences.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.Ui.Size = null;

        Assert.Empty(changes);
        Assert.Equal(17d, preferences.UiFontSize);
        Assert.Equal(17m, viewModel.Ui.Size);
    }

    [Fact]
    public void The_size_range_constants_mirror_the_preferences()
    {
        Assert.Equal(9, FontGroupViewModel.MinSize);
        Assert.Equal(28, FontGroupViewModel.MaxSize);
        Assert.Equal(9m, FontGroupViewModel.MinSizeDecimal);
        Assert.Equal(28m, FontGroupViewModel.MaxSizeDecimal);
        Assert.Equal(1m, FontGroupViewModel.SizeStepDecimal);
    }

    [Fact]
    public void Resetting_the_interface_group_leaves_the_code_group_alone()
    {
        ChangeEverything();

        viewModel.Ui.ResetCommand.Execute(null);

        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
        Assert.Equal(AppPreferences.DefaultUiFontSize, preferences.UiFontSize);
        Assert.Equal("Menlo", preferences.CodeFontFamily);
        Assert.Equal(21d, preferences.CodeFontSize);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal(13m, viewModel.Ui.Size);
        Assert.Equal("Menlo", viewModel.Code.FamilyText);
        Assert.Equal(21m, viewModel.Code.Size);
    }

    [Fact]
    public void Resetting_the_code_group_leaves_the_interface_group_alone()
    {
        ChangeEverything();

        viewModel.Code.ResetCommand.Execute(null);

        Assert.Equal(AppPreferences.DefaultCodeFontFamily, preferences.CodeFontFamily);
        Assert.Equal(AppPreferences.DefaultCodeFontSize, preferences.CodeFontSize);
        Assert.Equal("Georgia", preferences.UiFontFamily);
        Assert.Equal(19d, preferences.UiFontSize);
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, viewModel.Code.FamilyText);
        Assert.Equal(13m, viewModel.Code.Size);
    }

    [Fact]
    public void Resetting_a_group_that_is_already_default_discards_uncommitted_typing()
    {
        viewModel.Ui.FamilyText = "half typed";

        viewModel.Ui.ResetCommand.Execute(null);

        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, preferences.UiFontFamily);
    }

    [Fact]
    public void An_external_preference_change_is_echoed_into_the_editors()
    {
        var raised = new List<string?>();
        viewModel.Ui.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        viewModel.Code.PropertyChanged += (_, e) => raised.Add("code:" + e.PropertyName);

        preferences.UiFontFamily = "Georgia";
        preferences.UiFontSize = 18;
        preferences.CodeFontFamily = "Menlo";
        preferences.CodeFontSize = 15;

        Assert.Equal("Georgia", viewModel.Ui.FamilyText);
        Assert.Equal(18m, viewModel.Ui.Size);
        Assert.Equal("Menlo", viewModel.Code.FamilyText);
        Assert.Equal(15m, viewModel.Code.Size);
        Assert.Equal(15d, viewModel.Code.PreviewSize);
        Assert.Contains(nameof(FontGroupViewModel.FamilyText), raised);
        Assert.Contains(nameof(FontGroupViewModel.Size), raised);
        Assert.Contains("code:" + nameof(FontGroupViewModel.FamilyText), raised);
    }

    [Fact]
    public void An_external_change_overwrites_uncommitted_typing_of_the_same_group_only()
    {
        viewModel.Ui.FamilyText = "typing in ui";
        viewModel.Code.FamilyText = "typing in code";

        preferences.UiFontFamily = "Georgia";

        Assert.Equal("Georgia", viewModel.Ui.FamilyText);
        Assert.Equal("typing in code", viewModel.Code.FamilyText);
    }

    [Fact]
    public async Task An_unrelated_preference_change_leaves_the_inputs_alone_and_a_language_switch_only_refreshes_text()
    {
        // The catalog publishes the families from the background and raises Families on both groups; wait for it first, so
        // only the changes below reach the collectors.
        await viewModel.FamiliesLoaded.WaitAsync(Budget);

        var ui = new List<string?>();
        var code = new List<string?>();
        viewModel.Ui.PropertyChanged += (_, e) => ui.Add(e.PropertyName);
        viewModel.Code.PropertyChanged += (_, e) => code.Add(e.PropertyName);

        // A preference the section does not edit: no notification at all, not even a text refresh.
        preferences.TerminalShell = "pwsh";

        Assert.Empty(ui);
        Assert.Empty(code);

        // Storing the language is not the switch itself (the composition root pushes the stored value into Loc); the
        // switch refreshes the cached texts and must leave the values being edited alone.
        Loc.Instance.Language = UiLanguage.English;
        preferences.Language = UiLanguage.Chinese;
        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.Contains(nameof(FontGroupViewModel.Title), ui);
        Assert.Contains(nameof(FontGroupViewModel.Title), code);
        Assert.All(ui.Concat(code), raised => Assert.Contains(raised, CachedTexts));
        Assert.DoesNotContain(nameof(FontGroupViewModel.FamilyText), ui);
        Assert.DoesNotContain(nameof(FontGroupViewModel.FamilyText), code);
        Assert.DoesNotContain(nameof(FontGroupViewModel.Size), ui);
        Assert.DoesNotContain(nameof(FontGroupViewModel.Size), code);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal(13m, viewModel.Ui.Size);
        Assert.Equal(AppPreferences.DefaultCodeFontFamily, viewModel.Code.FamilyText);
        Assert.Equal(13m, viewModel.Code.Size);
    }

    [Fact]
    public void Switching_the_language_refreshes_every_cached_text()
    {
        // The texts asserted first are the English ones; state that instead of inheriting it from another test.
        Loc.Instance.Language = UiLanguage.English;

        Assert.Equal("Interface font", viewModel.Ui.Title);
        Assert.Equal("Code font", viewModel.Code.Title);
        var englishPreview = viewModel.Ui.PreviewText;
        var raised = new List<string?>();
        viewModel.Ui.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.Equal("界面字体", viewModel.Ui.Title);
        Assert.Equal("代码字体", viewModel.Code.Title);
        Assert.Equal("字体", viewModel.Ui.FamilyLabel);
        Assert.Equal("字号", viewModel.Ui.SizeLabel);
        Assert.Equal("恢复默认", viewModel.Ui.ResetText);
        Assert.NotEqual(englishPreview, viewModel.Ui.PreviewText);
        foreach (var name in new[]
                 {
                     nameof(FontGroupViewModel.Title),
                     nameof(FontGroupViewModel.FamilyLabel),
                     nameof(FontGroupViewModel.SizeLabel),
                     nameof(FontGroupViewModel.ResetText),
                     nameof(FontGroupViewModel.PreviewText),
                 })
        {
            Assert.Contains(name, raised);
        }

        Loc.Instance.Language = UiLanguage.English;
        Assert.Equal("Interface font", viewModel.Ui.Title);
    }

    [Fact]
    public void A_language_switch_does_not_disturb_pending_edits()
    {
        // English to Chinese is a real switch; Chinese to Chinese raises nothing and would make this vacuous.
        Loc.Instance.Language = UiLanguage.English;
        viewModel.Ui.FamilyText = "pending";
        viewModel.Ui.Size = 17m;

        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.Equal("pending", viewModel.Ui.FamilyText);
        Assert.Equal(17m, viewModel.Ui.Size);
    }

    [Fact]
    public async Task After_dispose_neither_preference_nor_language_changes_reach_the_groups()
    {
        // The families land after the constructor; wait for them, so nothing but the changes below can raise.
        await viewModel.FamiliesLoaded.WaitAsync(Budget);

        viewModel.Dispose();
        viewModel.Dispose();
        var raised = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        viewModel.Ui.PropertyChanged += handler;
        viewModel.Code.PropertyChanged += handler;

        // English to Chinese is a real switch; Chinese to Chinese raises nothing and would make this vacuous.
        Loc.Instance.Language = UiLanguage.English;
        preferences.UiFontFamily = "Georgia";
        preferences.CodeFontSize = 20;
        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.Empty(raised);
        Assert.Equal(AppPreferences.DefaultUiFontFamily, viewModel.Ui.FamilyText);
        Assert.Equal(13m, viewModel.Code.Size);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var catalog = new SystemFontCatalog(static () => []);

        Assert.Throws<ArgumentNullException>(() => new AppearanceSettingsViewModel(null!, catalog));
        Assert.Throws<ArgumentNullException>(() => new AppearanceSettingsViewModel(preferences, null!));
    }

    private void ChangeEverything()
    {
        preferences.UiFontFamily = "Georgia";
        preferences.UiFontSize = 19;
        preferences.CodeFontFamily = "Menlo";
        preferences.CodeFontSize = 21;
    }

    private sealed class Scope : IDisposable
    {
        private readonly List<IDisposable> items = [];

        public void Add(IDisposable item) => items.Add(item);

        public void Dispose() => items.ForEach(static item => item.Dispose());
    }
}
