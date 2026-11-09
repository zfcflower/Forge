using System.Runtime.CompilerServices;
using Forge.Workspace;

namespace Forge.App.Localization;

/// <summary>Texts of the reworked changes review (top bar, file header, block bar, footer). Owned by the main integration.</summary>
internal static class ChangesReviewStrings
{
    public const string BlockKeep = "changes.block.keep";
    public const string BlockKeepTip = "changes.block.keep.tip";
    public const string BlockKept = "changes.block.kept";
    public const string BlockRevert = "changes.block.revert";
    public const string KeptPattern = "changes.review.kept";
    public const string ViewedPattern = "changes.review.viewed";
    public const string ReviewedPattern = "changes.review.reviewed";
    public const string Split = "changes.review.split";
    public const string Inline = "changes.review.inline";
    public const string Stacked = "changes.review.stacked";
    public const string Single = "changes.review.single";
    public const string ViewModeName = "changes.review.viewMode.name";
    public const string LayoutName = "changes.review.layout.name";
    public const string Wrap = "changes.review.wrap";
    public const string NextUnviewed = "changes.review.nextUnviewed";
    public const string MarkAllViewed = "changes.review.markAllViewed";
    public const string ProgressName = "changes.review.progress.name";
    public const string RevertFileTitle = "changes.review.revertFile.title";
    public const string RevertFileMessagePattern = "changes.review.revertFile.message";
    public const string RevertFileConfirm = "changes.review.revertFile.confirm";
    public const string RevertFileChanged = "changes.review.revert.fileChanged";
    public const string RevertUnsupported = "changes.review.revert.unsupported";
    public const string RevertMissing = "changes.review.revert.missing";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(BlockKeep, "保留", "Keep");
        Loc.Register(BlockKeepTip, "保留这处改动（标记为已接受）", "Keep this change (mark as accepted)");
        Loc.Register(BlockKept, "已保留", "Kept");
        Loc.Register(BlockRevert, "撤销这处改动", "Revert this change");
        Loc.Register(KeptPattern, "已保留 {0}/{1}", "Kept {0}/{1}");
        Loc.Register(ViewedPattern, "已看 {0}/{1}", "Viewed {0}/{1}");
        Loc.Register(ReviewedPattern, "已评审 {0}/{1}", "Reviewed {0}/{1}");
        Loc.Register(Split, "拆分", "Split");
        Loc.Register(Inline, "内联", "Inline");
        Loc.Register(Stacked, "堆叠", "Stacked");
        Loc.Register(Single, "单文件", "Single file");
        Loc.Register(ViewModeName, "差异显示方式", "Diff view mode");
        Loc.Register(LayoutName, "文件排列方式", "File layout");
        Loc.Register(Wrap, "自动换行", "Wrap lines");
        Loc.Register(NextUnviewed, "下一个未评审的", "Next unreviewed");
        Loc.Register(MarkAllViewed, "全部标记为已查看", "Mark all as viewed");
        Loc.Register(ProgressName, "评审进度", "Review progress");
        Loc.Register(RevertFileTitle, "撤销整个文件的改动？", "Revert all changes in this file?");
        Loc.Register(RevertFileMessagePattern, "{0} 的改动会还原到对比基线，此操作会直接修改工作区文件。", "The changes in {0} will be restored to the comparison base. This edits the working tree file.");
        Loc.Register(RevertFileConfirm, "撤销", "Revert");
        Loc.Register(RevertFileChanged, "无法撤销：文件内容已与此改动不一致，请刷新后再试。", "Cannot revert: the file no longer matches this change. Refresh and try again.");
        Loc.Register(RevertUnsupported, "无法撤销：重命名、二进制或被截断的文件不支持撤销。", "Cannot revert: renamed, binary or truncated files are not supported.");
        Loc.Register(RevertMissing, "无法撤销：文件已不存在。", "Cannot revert: the file no longer exists.");
    }

    public static string KeptSummary(int kept, int total) =>
        Loc.Instance.Format(KeptPattern, TextFormat.Number(kept), TextFormat.Number(total));

    public static string ViewedSummary(int viewed, int total) =>
        Loc.Instance.Format(ViewedPattern, TextFormat.Number(viewed), TextFormat.Number(total));

    public static string ReviewedSummary(int viewed, int total) =>
        Loc.Instance.Format(ReviewedPattern, TextFormat.Number(viewed), TextFormat.Number(total));

    public static string RevertFileMessage(string path) => Loc.Instance.Format(RevertFileMessagePattern, path);

    public static string RevertFailedKey(RevertOutcome outcome) => outcome switch
    {
        RevertOutcome.Unsupported => RevertUnsupported,
        RevertOutcome.Missing => RevertMissing,
        _ => RevertFileChanged,
    };
}
