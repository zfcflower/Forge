namespace Forge.App.Workbench;

/// <summary>A region is named by what it holds, never by where it sits; its position is an index into the order.</summary>
internal enum RegionRole
{
    Project,
    Main,
    Inspector,
}

/// <summary>The window edge a side region collapses to when auto-hidden, derived from its position relative to Main.</summary>
internal enum RegionEdge
{
    Left,
    Right,
}

/// <summary>What a tab shows; decides its icon, its data template and how it is persisted.</summary>
internal enum TabKind
{
    Session,
    SubAgent,
    FilePreview,
    Changes,
    Todo,
    Review,
    Specs,

    /// <summary>An interactive terminal of the active session; it lives in the bottom group and is never persisted.</summary>
    Terminal,
    Git,
}

/// <summary>Position, size and state of a floating window in screen pixels (DIPs).</summary>
internal readonly record struct FloatGeometry(double X, double Y, double Width, double Height, bool IsMaximized)
{
    public static FloatGeometry Default(double width, double height) => new(double.NaN, double.NaN, width, height, false);

    /// <summary>True until the window has been placed once; the view then centres it over the main window.</summary>
    public bool IsUnplaced => double.IsNaN(X) || double.IsNaN(Y);
}

/// <summary>Stable ids of the fixed parts of the workbench; they are the keys of the layout snapshot.</summary>
internal static class WorkbenchIds
{
    public const string ProjectPanel = "projects";
    public const string FilesPanel = "files";
    public const string MainTabs = "main";
    public const string BottomTabs = "bottom";
    public const string PreviewTabs = "preview";
    public const string InspectorTools = "inspector-tools";

    public const string TodoTab = "todo";
    public const string ReviewTab = "review";
    public const string SpecsTab = "specs";
    public const string GitTab = "git";
    public const string ChangesTab = "changes";

    /// <summary>The reusable preview tab: a plain click on a file replaces its content.</summary>
    public const string PreviewTab = "preview-file";

    public const string SessionTabPrefix = "session:";
    public const string SubAgentTabPrefix = "subagent:";

    /// <summary>A terminal of any session; the suffix is <c>TerminalViewModel.Id</c> and is not written to <c>open-tabs</c>.</summary>
    public const string TerminalTabPrefix = "terminal:";

    /// <summary>A preview tab the user opened with "open in new tab"; the suffix is the file path.</summary>
    public const string PinnedFileTabPrefix = "file:";

    public static string SessionTab(string sessionId) => SessionTabPrefix + sessionId;

    public static string SubAgentTab(string agentId) => SubAgentTabPrefix + agentId;

    public static string TerminalTab(string terminalId) => TerminalTabPrefix + terminalId;

    public static string PinnedFileTab(string path) => PinnedFileTabPrefix + path;
}

/// <summary>Fixed sizes of the workbench chrome and the limits the width solver enforces.</summary>
internal static class WorkbenchMetrics
{
    public const double SideMinWidth = 200;
    public const double MainMinWidth = 360;
    public const double DefaultProjectWidth = 260;
    public const double DefaultInspectorWidth = 300;

    /// <summary>Width of the visible line between regions; the drag hit area is wider (<see cref="SplitterHitWidth"/>).</summary>
    public const double RegionSplitterWidth = 2;

    /// <summary>Width of the visible line between panels inside a region.</summary>
    public const double PanelSplitterWidth = 1;

    /// <summary>Pointer hit area of every splitter, centred on its line and overlapping the neighbours.</summary>
    public const double SplitterHitWidth = 6;

    /// <summary>Width of the button strip an auto-hidden region leaves on its window edge.</summary>
    public const double AutoHideStripWidth = 24;

    /// <summary>How far past its minimum width a side region must be pushed, as a fraction of that minimum, before it collapses.</summary>
    public const double CollapseOvershoot = 0.5;

    public const double PanelMinHeight = 80;
    public const double PreviewMinWidth = 240;
    public const double DefaultPreviewWidth = 420;
    public const double DefaultBottomHeight = 220;
    public const double DefaultInspectorSplitRatio = 0.5;

    /// <summary>Pointer travel along the strip before a dragged tab reorders, and off the strip before it floats out.</summary>
    public const double TabDragThreshold = 6;
    public const double TabFloatOutDistance = 32;

    public const double FloatingTabWidth = 900;
    public const double FloatingTabHeight = 640;
    public const double FloatingFilesWidth = 320;
    public const double FloatingFilesHeight = 420;
}
