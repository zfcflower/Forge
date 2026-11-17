using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The picture tools' texts (the image preview's zoom buttons and the Mermaid block's open-large button).</summary>
internal static class ViewerStrings
{
    public const string Previous = "viewer.previous";
    public const string Next = "viewer.next";
    public const string ZoomIn = "viewer.zoomIn";
    public const string ZoomOut = "viewer.zoomOut";
    public const string Fit = "viewer.fit";
    public const string Actual = "viewer.actual";
    public const string OpenLarge = "viewer.openLarge";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(Previous, "上一张", "Previous");
        Loc.Register(Next, "下一张", "Next");
        Loc.Register(ZoomIn, "放大", "Zoom in");
        Loc.Register(ZoomOut, "缩小", "Zoom out");
        Loc.Register(Fit, "适应窗口", "Fit to window");
        Loc.Register(Actual, "原始大小", "Actual size");
        Loc.Register(OpenLarge, "放大查看(缩放、拖动)", "Open large (zoom, drag)");
    }
}
