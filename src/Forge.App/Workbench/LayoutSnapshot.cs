using System.Text.Json.Serialization;

namespace Forge.App.Workbench;

/// <summary>
/// The persisted workbench layout (v5), stored under the <c>layout</c> key of <c>ui_state</c>. A v3 snapshot is migrated (only
/// the left column's split restarts at the default) and a v4 one carries its "collapsed to a strip" flag over as the v5
/// minimized state; any other version — including the Dock-era v2 — or a failed deserialize falls back to the default layout.
/// Fields added to v5 afterwards are optional and additive (a missing <see cref="BelowHeights"/> keeps the even split), so they
/// need neither a version bump nor a migration.
/// </summary>
internal sealed class LayoutSnapshot : IJsonOnSerializing
{
    public const int CurrentVersion = 5;

    /// <summary>
    /// Defaults to 0, not <see cref="CurrentVersion"/>, so JSON that omits <c>version</c> deserializes as a foreign
    /// version and falls back to the default layout; serialization stamps <see cref="CurrentVersion"/>.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <inheritdoc/>
    public void OnSerializing() => Version = CurrentVersion;

    /// <summary>Region roles in visual order ("Project", "Main", "Inspector"); an invalid permutation keeps the default order.</summary>
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    [JsonPropertyName("regions")]
    public List<RegionSnapshot> Regions { get; set; } = [];

    [JsonPropertyName("previewWidth")]
    public double PreviewWidth { get; set; } = WorkbenchMetrics.DefaultPreviewWidth;

    [JsonPropertyName("inspectorSplit")]
    public double InspectorSplitRatio { get; set; } = WorkbenchMetrics.DefaultInspectorSplitRatio;

    /// <summary>Share of the projects panel above the general chats in the left column; missing keeps the default.</summary>
    [JsonPropertyName("projectSplit")]
    public double ProjectSplitRatio { get; set; } = WorkbenchMetrics.DefaultProjectSplitRatio;

    /// <summary>
    /// Height (px) each panel below the files panel last had in the Inspector column, keyed by panel id, so the lines between
    /// them come back where the user left them. Optional and additive: a snapshot without it (or without an entry for a panel)
    /// splits that column evenly, which is what every version before it did, so no migration is needed.
    /// </summary>
    [JsonPropertyName("belowHeights")]
    public Dictionary<string, double> BelowHeights { get; set; } = [];

    [JsonPropertyName("bottomHeight")]
    public double BottomHeight { get; set; } = WorkbenchMetrics.DefaultBottomHeight;

    /// <summary>True while the bottom group sits at title-bar height; the composer's sub-agent toggle and the group's own
    /// restore button drive this one state.</summary>
    [JsonPropertyName("bottomMinimized")]
    public bool BottomMinimized { get; set; }

    /// <summary>Retired v4 field: only the v4 migration reads it, and a v5 snapshot never writes it.</summary>
    [JsonPropertyName("bottomCollapsed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BottomCollapsed { get; set; }

    /// <summary>Order of the fixed inspector tool tabs and the active one.</summary>
    [JsonPropertyName("tools")]
    public List<string> ToolsOrder { get; set; } = [];

    [JsonPropertyName("toolsActive")]
    public string? ToolsActive { get; set; }

    [JsonPropertyName("floating")]
    public List<FloatingSnapshot> Floating { get; set; } = [];
}

internal sealed class RegionSnapshot
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("autoHidden")]
    public bool AutoHidden { get; set; }
}

/// <summary>A floating window: the tab id it holds, or <see cref="WorkbenchIds.FilesPanel"/>, and its geometry.</summary>
internal sealed class FloatingSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Null while the window was never placed; it is then centred over the main window on restore.</summary>
    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("maximized")]
    public bool IsMaximized { get; set; }
}

/// <summary>The open tabs (v2), stored under <c>open-tabs</c>: session tabs, and the preview group's files and changes tab.</summary>
internal sealed class OpenTabsSnapshot : IJsonOnSerializing
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Defaults to 0, not <see cref="CurrentVersion"/>, so JSON that omits <c>version</c> deserializes as a foreign
    /// version and falls back to the default open tabs; serialization stamps <see cref="CurrentVersion"/>.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <inheritdoc/>
    public void OnSerializing() => Version = CurrentVersion;

    [JsonPropertyName("sessions")]
    public List<string> Sessions { get; set; } = [];

    [JsonPropertyName("activeSession")]
    public string? ActiveSession { get; set; }

    /// <summary>File shown in the reusable preview tab.</summary>
    [JsonPropertyName("previewFile")]
    public string? PreviewFile { get; set; }

    /// <summary>Files opened with "open in new tab", in tab order.</summary>
    [JsonPropertyName("pinnedFiles")]
    public List<string> PinnedFiles { get; set; } = [];

    [JsonPropertyName("changesOpen")]
    public bool ChangesOpen { get; set; }

    /// <summary>Id of the active preview-group tab.</summary>
    [JsonPropertyName("activePreview")]
    public string? ActivePreview { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LayoutSnapshot))]
[JsonSerializable(typeof(OpenTabsSnapshot))]
internal sealed partial class WorkbenchJsonContext : JsonSerializerContext
{
}
