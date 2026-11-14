using Forge.App.Preferences;
using Forge.App.Settings;
using Xunit;

namespace Forge.App.Tests.Settings;

/// <summary>
/// Pins <see cref="ChatSettingsSectionViewModel"/>: every row writes through to the live preferences and reads back from
/// them (with the preference's clamp for the thumbnail edge and the Mermaid timeout), the control bounds mirror the
/// preference's, and the Mermaid caption lists each diagram-type token once.
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

    [Fact]
    public void The_mermaid_whitelist_writes_through_the_preferences_and_reads_back()
    {
        var preferences = new AppPreferences();
        var section = new ChatSettingsSectionViewModel(preferences);
        Assert.Equal(AppPreferences.DefaultChatMermaidDiagramTypes, section.MermaidTypes);

        section.MermaidTypes = "flowchart, gantt";

        Assert.Equal("flowchart, gantt", preferences.ChatMermaidDiagramTypes);
        Assert.Equal("flowchart, gantt", new ChatSettingsSectionViewModel(preferences).MermaidTypes);
    }

    [Fact]
    public void The_mermaid_timeout_writes_through_is_clamped_and_notifies()
    {
        var preferences = new AppPreferences();
        var section = new ChatSettingsSectionViewModel(preferences);
        var raised = PropertyChangeLog.For(section);

        section.MermaidTimeoutMilliseconds = 4000;
        Assert.Equal(4000, preferences.ChatMermaidTimeoutMilliseconds);
        Assert.Equal(4000, section.MermaidTimeoutMilliseconds);

        // Out of range lands on the preference's clamp, so the box shows the value that was kept.
        section.MermaidTimeoutMilliseconds = 1;
        Assert.Equal(AppPreferences.MinChatMermaidTimeoutMilliseconds, section.MermaidTimeoutMilliseconds);
        section.MermaidTimeoutMilliseconds = 999_999;
        Assert.Equal(AppPreferences.MaxChatMermaidTimeoutMilliseconds, section.MermaidTimeoutMilliseconds);
        Assert.Equal(3, raised.Count(name => name == nameof(ChatSettingsSectionViewModel.MermaidTimeoutMilliseconds)));
    }

    [Fact]
    public void The_known_mermaid_types_caption_lists_each_token_once()
    {
        var known = ChatSettingsSectionViewModel.KnownMermaidTypes;

        var tokens = known.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("flowchart", tokens);
        Assert.Contains("pie", tokens);
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_mermaid_timeout_bounds_mirror_the_preference_clamp()
    {
        Assert.Equal(AppPreferences.MinChatMermaidTimeoutMilliseconds, ChatSettingsSectionViewModel.MinMermaidTimeout);
        Assert.Equal(AppPreferences.MaxChatMermaidTimeoutMilliseconds, ChatSettingsSectionViewModel.MaxMermaidTimeout);
        Assert.Equal(AppPreferences.MinChatMermaidTimeoutMilliseconds, ChatSettingsSectionViewModel.MinMermaidTimeoutDecimal);
        Assert.Equal(AppPreferences.MaxChatMermaidTimeoutMilliseconds, ChatSettingsSectionViewModel.MaxMermaidTimeoutDecimal);
    }
}
