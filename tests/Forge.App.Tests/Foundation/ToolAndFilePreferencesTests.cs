using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// The two preference groups W0 registered for W4 (chat and files) through the persistence mapping: every value set on
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
        var source = new AppPreferences { ChatToolImageThumbnailPixels = 320, ChatToolImagesToModel = false };

        var reloaded = Reloaded(source);

        Assert.Equal(320, reloaded.ChatToolImageThumbnailPixels);
        Assert.False(reloaded.ChatToolImagesToModel);
    }

    [Fact]
    public void The_files_group_survives_a_round_trip()
    {
        var source = new AppPreferences
        {
            FilesCsvMaxRows = 250,
            FilesNotebookOutputKilobytes = 4,
            FilesImagePreviewMaxMegabytes = 32,
        };

        var reloaded = Reloaded(source);

        Assert.Equal(250, reloaded.FilesCsvMaxRows);
        Assert.Equal(4, reloaded.FilesNotebookOutputKilobytes);
        Assert.Equal(32, reloaded.FilesImagePreviewMaxMegabytes);
    }

    [Fact]
    public void An_empty_section_keeps_every_default()
    {
        var reloaded = Reloaded(new AppPreferences());

        Assert.Equal(AppPreferences.DefaultChatToolImageThumbnailPixels, reloaded.ChatToolImageThumbnailPixels);
        Assert.Equal(AppPreferences.DefaultChatToolImagesToModel, reloaded.ChatToolImagesToModel);
        Assert.Equal(AppPreferences.DefaultFilesCsvMaxRows, reloaded.FilesCsvMaxRows);
        Assert.Equal(AppPreferences.DefaultFilesNotebookOutputKilobytes, reloaded.FilesNotebookOutputKilobytes);
        Assert.Equal(AppPreferences.DefaultFilesImagePreviewMaxMegabytes, reloaded.FilesImagePreviewMaxMegabytes);
    }

    [Fact]
    public void A_hand_written_section_is_clamped_on_load()
    {
        var section = new PreferencesSection
        {
            ChatToolImageThumbnailPixels = 100_000,
            FilesCsvMaxRows = 1,
            FilesNotebookOutputKilobytes = 0,
            FilesImagePreviewMaxMegabytes = 100_000,
        };

        var reloaded = new AppPreferences();
        reloaded.ApplyLoaded(section, UiLanguage.English);

        Assert.Equal(AppPreferences.MaxChatToolImageThumbnailPixels, reloaded.ChatToolImageThumbnailPixels);
        Assert.Equal(AppPreferences.MinFilesCsvMaxRows, reloaded.FilesCsvMaxRows);
        Assert.Equal(AppPreferences.MinFilesNotebookOutputKilobytes, reloaded.FilesNotebookOutputKilobytes);
        Assert.Equal(AppPreferences.MaxFilesImagePreviewMaxMegabytes, reloaded.FilesImagePreviewMaxMegabytes);
    }

    [Fact]
    public void The_defaults_are_the_registry_ones()
    {
        // The plan's registry: 256 px, sending on, 1000 rows, 20 KB of notebook output, 20 MB image limit.
        Assert.Equal(256, AppPreferences.DefaultChatToolImageThumbnailPixels);
        Assert.True(AppPreferences.DefaultChatToolImagesToModel);
        Assert.Equal(1_000, AppPreferences.DefaultFilesCsvMaxRows);
        Assert.Equal(20, AppPreferences.DefaultFilesNotebookOutputKilobytes);
        Assert.Equal(20, AppPreferences.DefaultFilesImagePreviewMaxMegabytes);
    }
}
