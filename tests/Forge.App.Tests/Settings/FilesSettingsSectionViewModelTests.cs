using Forge.App.Preferences;
using Forge.App.Settings;
using Forge.App.ViewModels.Preview;
using Xunit;

namespace Forge.App.Tests.Settings;

public sealed class FilesSettingsSectionViewModelTests
{
    [Fact]
    public void Rows_and_notebook_chars_round_trip_through_the_preferences()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.PreviewRows = 1500;
        section.NotebookOutputChars = 50000;

        Assert.Equal(1500, preferences.FilePreviewRows);
        Assert.Equal(50000, preferences.NotebookOutputChars);
        Assert.Equal(1500, section.PreviewRows);
        Assert.Equal(50000, section.NotebookOutputChars);
    }

    [Fact]
    public void The_MiB_conversion_is_exact_for_20_MiB()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.ImageLimitMiB = 20;

        Assert.Equal(20 * FilePreviewSettings.BytesPerMiB, preferences.PreviewImageBytes);
        Assert.Equal(20, section.ImageLimitMiB);
    }

    [Fact]
    public void Values_are_clamped_by_the_preferences_on_each_write()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        section.PreviewRows = 1;
        Assert.Equal(AppPreferences.MinFilePreviewRows, preferences.FilePreviewRows);

        section.NotebookOutputChars = 1;
        Assert.Equal(AppPreferences.MinNotebookOutputChars, preferences.NotebookOutputChars);

        section.ImageLimitMiB = 100000;
        Assert.Equal(AppPreferences.MaxPreviewImageBytes, preferences.PreviewImageBytes);
        Assert.Equal(AppPreferences.MaxPreviewImageBytes / FilePreviewSettings.BytesPerMiB, section.ImageLimitMiB);

        section.ImageLimitMiB = 0;
        Assert.Equal(AppPreferences.MinPreviewImageBytes, preferences.PreviewImageBytes);
        Assert.Equal(1, section.ImageLimitMiB);
    }

    [Fact]
    public void A_loaded_preference_reads_back_through_the_section()
    {
        var preferences = new AppPreferences();
        using var section = new FilesSettingsSectionViewModel(preferences);

        preferences.PreviewImageBytes = 40 * FilePreviewSettings.BytesPerMiB;

        Assert.Equal(40, section.ImageLimitMiB);
    }

    [Fact]
    public void Dispose_is_idempotent_and_does_not_throw()
    {
        using var section = new FilesSettingsSectionViewModel(new AppPreferences());

        section.Dispose();
        section.Dispose();
    }
}
