using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>Texts of the review diff editor (file header bar, fold bars).</summary>
internal static class DiffStrings
{
    public const string Unmodified = "changes.diff.unmodified";
    public const string UnmodifiedTip = "changes.diff.unmodified.tip";
    public const string Viewed = "changes.diff.viewed";
    public const string Preview = "changes.diff.preview";
    public const string Source = "changes.diff.source";
    public const string RevertFile = "changes.diff.revertFile";
    public const string OpenInEditor = "changes.diff.openInEditor";
    public const string More = "changes.diff.more";
    public const string CopyPath = "changes.diff.copyPath";
    public const string ToggleCollapsed = "changes.diff.toggleCollapsed";
    public const string FileHeader = "changes.diff.fileHeader";
    public const string Binary = "changes.diff.binary";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(Unmodified, "{0} 行未改动", "{0} unmodified lines");
        Loc.Register(UnmodifiedTip, "展开这些未改动的行", "Expand these unmodified lines");
        Loc.Register(Viewed, "已查看", "Viewed");
        Loc.Register(Preview, "预览", "Preview");
        Loc.Register(Source, "Source", "Source");
        Loc.Register(RevertFile, "撤销此文件的全部改动", "Revert all changes of this file");
        Loc.Register(OpenInEditor, "在编辑器中打开", "Open in editor");
        Loc.Register(More, "更多操作", "More actions");
        Loc.Register(CopyPath, "复制路径", "Copy path");
        Loc.Register(ToggleCollapsed, "折叠或展开此文件", "Collapse or expand this file");
        Loc.Register(FileHeader, "文件头：{0}", "File header: {0}");
        Loc.Register(Binary, "二进制文件，没有可显示的文本改动", "Binary file: no text changes to show");
    }
}
