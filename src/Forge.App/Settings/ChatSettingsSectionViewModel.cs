using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>chat</c> settings section (<see cref="SettingsSectionIds.Chat"/>): how large a tool result's images render in
/// the transcript and whether those images reach the model. Edits <see cref="AppPreferences"/> directly, so the next
/// row completion and the next request read the new values and the preferences store persists them on its debounce.
/// <para>The labels and hints are <see cref="Loc"/> keys bound in XAML and the heading comes from the section's title
/// key, so a language switch re-renders all of them without this view model raising anything — it holds no
/// subscription and needs no disposal.</para>
/// </summary>
internal sealed class ChatSettingsSectionViewModel : ObservableObject
{
    /// <summary>Bounds of the thumbnail-edge control, mirrored from the preference's clamp so both stay one number.</summary>
    public const int MinThumbnailEdge = AppPreferences.MinChatToolImageThumbnailPixels;
    public const int MaxThumbnailEdge = AppPreferences.MaxChatToolImageThumbnailPixels;

    /// <summary>
    /// Decimal mirrors of the bounds for the numeric-up-down: its range properties are <c>decimal?</c> and the XAML
    /// compiler will not convert the int constants (the appearance section does the same for its font size).
    /// </summary>
    public static decimal MinThumbnailEdgeDecimal => MinThumbnailEdge;

    public static decimal MaxThumbnailEdgeDecimal => MaxThumbnailEdge;

    private readonly AppPreferences preferences;

    public ChatSettingsSectionViewModel(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        this.preferences = preferences;
    }

    /// <summary>
    /// Longest edge of a tool row's thumbnail, in device-independent pixels; the preference clamps it to
    /// [<see cref="MinThumbnailEdge"/>, <see cref="MaxThumbnailEdge"/>]. The stored image is never re-encoded.
    /// </summary>
    public int ThumbnailEdge
    {
        get => preferences.ChatToolImageThumbnailPixels;
        set
        {
            preferences.ChatToolImageThumbnailPixels = value;

            // The control also has to re-read the clamped value the setter settled on.
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a tool result's images are sent to the model. Off keeps them in the transcript and sends the text
    /// placeholder the result already carries; the transcript always shows the images either way.
    /// </summary>
    public bool SendToolImagesToModel
    {
        get => preferences.ChatToolImagesToModel;
        set
        {
            preferences.ChatToolImagesToModel = value;
            OnPropertyChanged();
        }
    }
}
