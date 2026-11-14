using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.Tests.Workbench;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The <c>palette</c> settings section: its two editors are plain adapters over the live <see cref="AppPreferences"/>, so
/// a change applies to the palette's next keystroke without a reload. Pins the write-through, the clamp the preference
/// itself applies, and that the section title is a registered localization key (the settings page reads it as one).
/// </summary>
[Collection("forge-app-ui")]
public sealed class PaletteSettingsSectionTests
{
    [Fact]
    public void The_result_limit_editor_writes_through_to_the_live_preference()
    {
        var preferences = new AppPreferences();
        var section = new PaletteSettingsSectionViewModel(preferences);

        section.ResultLimit = 123;

        Assert.Equal(123, preferences.PalettePerSourceLimit);
        Assert.Equal(123, section.ResultLimit);
    }

    [Fact]
    public void The_result_limit_editor_reports_the_bounds_the_preference_enforces()
    {
        var section = new PaletteSettingsSectionViewModel(new AppPreferences());

        Assert.Equal(AppPreferences.MinPalettePerSourceLimit, section.MinimumLimit);
        Assert.Equal(AppPreferences.MaxPalettePerSourceLimit, section.MaximumLimit);
    }

    [Fact]
    public void A_result_limit_outside_the_bounds_is_clamped_by_the_preference()
    {
        var preferences = new AppPreferences();
        var section = new PaletteSettingsSectionViewModel(preferences);

        section.ResultLimit = AppPreferences.MaxPalettePerSourceLimit + 1;

        Assert.Equal(AppPreferences.MaxPalettePerSourceLimit, section.ResultLimit);
        Assert.Equal(AppPreferences.MaxPalettePerSourceLimit, preferences.PalettePerSourceLimit);
    }

    [Fact]
    public void The_exclude_directories_editor_writes_through_and_trims()
    {
        var preferences = new AppPreferences();
        var section = new PaletteSettingsSectionViewModel(preferences);

        section.ExcludeDirectories = "  node_modules, bin  ";

        Assert.Equal("node_modules, bin", preferences.PaletteExcludeDirectories);
    }

    [Fact]
    public void The_exclude_editor_keeps_the_text_as_typed_so_a_trailing_space_survives_the_next_keystroke()
    {
        var preferences = new AppPreferences { PaletteExcludeDirectories = "bin" };
        var section = new PaletteSettingsSectionViewModel(preferences);
        Assert.Equal("bin", section.ExcludeDirectories);

        // The text box writes the property per keystroke: "bin, " must read back as typed, or the space vanishes and
        // the user can never get to "bin, obj".
        section.ExcludeDirectories = "bin, ";
        Assert.Equal("bin, ", section.ExcludeDirectories);
        Assert.Equal("bin,", preferences.PaletteExcludeDirectories);

        section.ExcludeDirectories = "bin, obj";
        Assert.Equal("bin, obj", preferences.PaletteExcludeDirectories);
    }

    [Fact]
    public void The_module_registers_the_palette_section_with_its_view_template()
    {
        var settings = new SettingsSections();

        PaletteComposition.RegisterSection(new AppPreferences(), settings);

        var section = Assert.Single(settings.Items);
        Assert.Equal(SettingsSectionIds.Palette, section.Id);
        Assert.Equal(SettingsSectionIds.PaletteOrder, section.Order);
        Assert.IsType<PaletteSettingsSectionViewModel>(section.ViewModel);
    }

    [Fact]
    public async Task The_section_renders_its_two_editors_through_the_section_template()
    {
        await SharedUi.RunAsync(
            () =>
            {
                var settings = new SettingsSections();
                PaletteComposition.RegisterSection(new AppPreferences(), settings);
                var content = new ContentControl { DataTemplates = { new SectionViewTemplate() } };

                content.Content = Assert.Single(settings.Items).ViewModel;
                var window = SharedUi.Show(content, 700, 300);
                try
                {
                    SharedUi.Settle(window);

                    Assert.NotNull(WorkbenchUi.Find<NumericUpDown>(window, "palette-settings-limit"));
                    Assert.NotNull(WorkbenchUi.Find<TextBox>(window, "palette-settings-exclude"));
                }
                finally
                {
                    window.Close();
                }

                return Task.CompletedTask;
            });
    }

    [Fact]
    public void The_section_title_is_a_registered_key_in_both_languages()
    {
        using var chinese = new Localization.LanguageScope(UiLanguage.Chinese);
        var chineseTitle = Loc.Instance[PaletteSettingsSectionViewModel.SectionTitle];

        using var english = new Localization.LanguageScope(UiLanguage.English);
        var englishTitle = Loc.Instance[PaletteSettingsSectionViewModel.SectionTitle];

        Assert.NotEqual("[" + PaletteSettingsSectionViewModel.SectionTitle + "]", chineseTitle);
        Assert.NotEqual("[" + PaletteSettingsSectionViewModel.SectionTitle + "]", englishTitle);
        Assert.NotEqual(chineseTitle, englishTitle);
    }
}
