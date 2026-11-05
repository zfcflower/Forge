using System.Text.Json.Serialization;

namespace Forge.App.Workbench;

/// <summary>
/// The persisted workbench layout (v3), stored under the <c>layout</c> key of <c>ui_state</c>. Any other version —
/// including the Dock-era v2 — or a failed deserialize falls back to the default layout; there is no migration.
/// </summary>
internal sealed class LayoutSnapshot : IJsonOnSerializing
{
    public const int CurrentVersion = 3;

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

    [JsonPropertyName("bottomHeight")]
    public double BottomHeight { get; set; } = WorkbenchMetrics.DefaultBottomHeight;

    [JsonPropertyName("bottomCollapsed")]
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
