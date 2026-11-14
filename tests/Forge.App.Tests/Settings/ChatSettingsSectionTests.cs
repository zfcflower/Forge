using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// Pins <see cref="ChatSettingsSectionViewModel"/>: both rows write through to the live preferences and read back
/// from them (with the preference's clamp for the thumbnail edge), and the control bounds mirror the preference's.
/// </summary>
public sealed class ChatSettingsSectionTests
{
    [Fact]
    public void Thumbnail_edge_and_the_send_switch_round_trip_through_the_preferences()
    {
        var preferences = new AppPreferences();
        var section = new ChatSettingsSectionViewModel(preferences);

        Assert.Equal(AppPreferences.DefaultChatToolImageThumbnailPixels, section.ThumbnailEdge);
        Assert.True(section.SendToolImagesToModel);

        section.ThumbnailEdge = 384;
        Assert.Equal(384, preferences.ChatToolImageThumbnailPixels);
        Assert.Equal(384, section.ThumbnailEdge);

        // Out of range lands on the preference's clamp (1024 is the registry's upper bound).
        section.ThumbnailEdge = 10_000;
        Assert.Equal(AppPreferences.MaxChatToolImageThumbnailPixels, preferences.ChatToolImageThumbnailPixels);
        Assert.Equal(AppPreferences.MaxChatToolImageThumbnailPixels, section.ThumbnailEdge);

        section.SendToolImagesToModel = false;
        Assert.False(preferences.ChatToolImagesToModel);
        Assert.False(section.SendToolImagesToModel);

        // A fresh section built on the same preferences reads the edited values back.
        var reread = new ChatSettingsSectionViewModel(preferences);
        Assert.Equal(AppPreferences.MaxChatToolImageThumbnailPixels, reread.ThumbnailEdge);
        Assert.False(reread.SendToolImagesToModel);
    }

    [Fact]
    public void A_clamped_edit_notifies_so_the_box_shows_the_value_that_was_kept()
    {
        var section = new ChatSettingsSectionViewModel(new AppPreferences());
        var raised = PropertyChangeLog.For(section);

        section.ThumbnailEdge = 1;

        Assert.Contains(nameof(ChatSettingsSectionViewModel.ThumbnailEdge), raised);
        Assert.Equal(AppPreferences.MinChatToolImageThumbnailPixels, section.ThumbnailEdge);
    }

    [Fact]
    public void The_control_bounds_mirror_the_preferences_thumbnail_clamp()
    {
        Assert.Equal(AppPreferences.MinChatToolImageThumbnailPixels, ChatSettingsSectionViewModel.MinThumbnailEdge);
        Assert.Equal(AppPreferences.MaxChatToolImageThumbnailPixels, ChatSettingsSectionViewModel.MaxThumbnailEdge);
        Assert.Equal(ChatSettingsSectionViewModel.MinThumbnailEdge, ChatSettingsSectionViewModel.MinThumbnailEdgeDecimal);
        Assert.Equal(ChatSettingsSectionViewModel.MaxThumbnailEdge, ChatSettingsSectionViewModel.MaxThumbnailEdgeDecimal);
    }
}
