using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>The picture viewer's texts (transcript thumbnails opened large: zoom, pan, previous/next, reveal the file).</summary>
internal static class ViewerStrings
{
    public const string Title = "viewer.title";
    public const string Previous = "viewer.previous";
    public const string Next = "viewer.next";
    public const string ZoomIn = "viewer.zoomIn";
    public const string ZoomOut = "viewer.zoomOut";
    public const string Fit = "viewer.fit";
    public const string Actual = "viewer.actual";
    public const string Reveal = "viewer.reveal";
    public const string OpenSystem = "viewer.openSystem";
    public const string OpenLarge = "viewer.openLarge";
    public const string Failed = "viewer.failed";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(Title, "图片查看", "Picture viewer");
        Loc.Register(Previous, "上一张", "Previous");
        Loc.Register(Next, "下一张", "Next");
        Loc.Register(ZoomIn, "放大", "Zoom in");
        Loc.Register(ZoomOut, "缩小", "Zoom out");
        Loc.Register(Fit, "适应窗口", "Fit to window");
        Loc.Register(Actual, "原始大小", "Actual size");
        Loc.Register(Reveal, "打开文件位置", "Show in folder");
        Loc.Register(OpenSystem, "用系统程序打开", "Open with system app");
        Loc.Register(OpenLarge, "放大查看(缩放、拖动)", "Open large (zoom, drag)");
        Loc.Register(Failed, "无法显示该图片", "This picture cannot be shown");
    }
}
