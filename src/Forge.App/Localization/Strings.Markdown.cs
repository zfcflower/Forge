using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

internal static class MarkdownStrings
{
    public const string Folded = "changes.markdown.folded";
    public const string FoldedTip = "changes.markdown.folded.tip";
    public const string Gap = "changes.markdown.gap";
    public const string CopyCode = "changes.markdown.copyCode";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(Folded, "{0} 个未改动块", "{0} unchanged blocks");
        Loc.Register(FoldedTip, "展开未改动的块", "Expand the unchanged blocks");
        Loc.Register(Gap, "{0} 行未显示", "{0} lines not shown");
        Loc.Register(CopyCode, "复制代码", "Copy code");
    }
}
