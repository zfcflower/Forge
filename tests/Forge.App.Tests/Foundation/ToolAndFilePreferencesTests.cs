using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// The two preference groups W4 added (chat and files) through the persistence mapping: every value set on
/// <see cref="AppPreferences"/> must reach <c>settings.json</c> via <c>ToSection</c> and come back through
/// <c>ApplyLoaded</c>, and an out-of-range value written by hand must land on the documented bound rather than survive.
/// A property missing from either side of that mapping is silent data loss, which is what this pins.
/// </summary>
public sealed class ToolAndFilePreferencesTests
{
    private static AppPreferences Reloaded(AppPreferences source)
    {
        var reloaded = new AppPreferences();
        reloaded.ApplyLoaded(source.ToSection(), UiLanguage.English);
        return reloaded;
    }

    [Fact]
    public void The_chat_group_survives_a_round_trip()
    {
        var source = new AppPreferences { ToolImageThumbnailEdge = 320, SendToolImagesToModel = false };

        var reloaded = Reloaded(source);

        Assert.Equal(320, reloaded.ToolImageThumbnailEdge);
        Assert.False(reloaded.SendToolImagesToModel);
    }

    [Fact]
    public void The_files_group_survives_a_round_trip()
    {
        var source = new AppPreferences
        {
            FilePreviewRows = 250,
            NotebookOutputChars = 4_096,
            PreviewImageBytes = 32 * 1024 * 1024,
        };

        var reloaded = Reloaded(source);

        Assert.Equal(250, reloaded.FilePreviewRows);
        Assert.Equal(4_096, reloaded.NotebookOutputChars);
        Assert.Equal(32 * 1024 * 1024, reloaded.PreviewImageBytes);
    }

    [Fact]
    public void An_empty_section_keeps_every_default()
    {
        var reloaded = Reloaded(new AppPreferences());

        Assert.Equal(AppPreferences.DefaultToolImageThumbnailEdge, reloaded.ToolImageThumbnailEdge);
        Assert.Equal(AppPreferences.DefaultSendToolImagesToModel, reloaded.SendToolImagesToModel);
        Assert.Equal(AppPreferences.DefaultFilePreviewRows, reloaded.FilePreviewRows);
        Assert.Equal(AppPreferences.DefaultNotebookOutputChars, reloaded.NotebookOutputChars);
        Assert.Equal(AppPreferences.DefaultPreviewImageBytes, reloaded.PreviewImageBytes);
    }

    [Fact]
    public void A_hand_written_section_is_clamped_on_load()
    {
        var section = new PreferencesSection
        {
            ToolImageThumbnailEdge = 10_000,
            FilePreviewRows = 1,
            NotebookOutputChars = 10,
            PreviewImageBytes = 1,
        };

        var reloaded = new AppPreferences();
        reloaded.ApplyLoaded(section, UiLanguage.English);

        Assert.Equal(AppPreferences.MaxToolImageThumbnailEdge, reloaded.ToolImageThumbnailEdge);
        Assert.Equal(AppPreferences.MinFilePreviewRows, reloaded.FilePreviewRows);
        Assert.Equal(AppPreferences.MinNotebookOutputChars, reloaded.NotebookOutputChars);
        Assert.Equal(AppPreferences.MinPreviewImageBytes, reloaded.PreviewImageBytes);
    }

    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        // The plan's registry: 256 px, sending on, 1000 rows, 20 KB of notebook output, 20 MB image limit.
        Assert.Equal(256, AppPreferences.DefaultToolImageThumbnailEdge);
        Assert.True(AppPreferences.DefaultSendToolImagesToModel);
        Assert.Equal(1_000, AppPreferences.DefaultFilePreviewRows);
        Assert.Equal(20_480, AppPreferences.DefaultNotebookOutputChars);
        Assert.Equal(20 * 1024 * 1024, AppPreferences.DefaultPreviewImageBytes);
    }
}
