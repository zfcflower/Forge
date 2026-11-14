using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

public sealed class FilesSettingsSectionViewModelTests
{
    [Fact]
    public void Rows_and_notebook_kilobytes_round_trip_through_the_preferences()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.PreviewRows = 1500;
        section.NotebookOutputKilobytes = 50;

        Assert.Equal(1500, preferences.FilesCsvMaxRows);
        Assert.Equal(50, preferences.FilesNotebookOutputKilobytes);
        Assert.Equal(1500, section.PreviewRows);
        Assert.Equal(50, section.NotebookOutputKilobytes);
    }

    [Fact]
    public void The_image_limit_is_edited_in_the_megabytes_the_preference_stores()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.ImageLimitMiB = 20;

        Assert.Equal(20, preferences.FilesImagePreviewMaxMegabytes);
        Assert.Equal(20, section.ImageLimitMiB);
    }

    [Fact]
    public void Values_are_clamped_by_the_preferences_on_each_write()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.PreviewRows = 1;
        Assert.Equal(AppPreferences.MinFilesCsvMaxRows, preferences.FilesCsvMaxRows);

        section.NotebookOutputKilobytes = 0;
        Assert.Equal(AppPreferences.MinFilesNotebookOutputKilobytes, preferences.FilesNotebookOutputKilobytes);

        section.ImageLimitMiB = 100_000;
        Assert.Equal(AppPreferences.MaxFilesImagePreviewMaxMegabytes, preferences.FilesImagePreviewMaxMegabytes);
        Assert.Equal(AppPreferences.MaxFilesImagePreviewMaxMegabytes, section.ImageLimitMiB);

        section.ImageLimitMiB = 0;
        Assert.Equal(AppPreferences.MinFilesImagePreviewMaxMegabytes, preferences.FilesImagePreviewMaxMegabytes);
        Assert.Equal(AppPreferences.MinFilesImagePreviewMaxMegabytes, section.ImageLimitMiB);
    }

    [Fact]
    public void A_loaded_preference_reads_back_through_the_section()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        preferences.FilesImagePreviewMaxMegabytes = 40;

        Assert.Equal(40, section.ImageLimitMiB);
    }

    [Fact]
    public void The_notebook_row_is_labelled_in_the_kilobytes_it_edits()
    {
        using var section = new FilesSettingsSectionViewModel(new AppPreferences());

        Assert.Equal("files.preview.settings.notebookKilobytes", PreviewStrings.SettingsNotebookKilobytesKey);
        Assert.Equal(Loc.Instance[PreviewStrings.SettingsNotebookKilobytesKey], section.NotebookOutputLabel);
        Assert.Contains("KB", section.NotebookOutputLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_is_idempotent_and_does_not_throw()
    {
        using var section = new FilesSettingsSectionViewModel(new AppPreferences());

        section.Dispose();
        section.Dispose();
    }
}
