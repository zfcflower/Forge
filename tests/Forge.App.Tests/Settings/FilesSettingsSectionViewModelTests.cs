using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

public sealed class FilesSettingsSectionViewModelTests
{
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
    public void The_notebook_row_is_labelled_in_the_kilobytes_it_edits()
    {
        using var section = new FilesSettingsSectionViewModel(new AppPreferences());

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
