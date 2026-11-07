using System.Runtime.CompilerServices;
using Forge.App.Localization;

namespace Forge.App.Localization;

internal static class ReviewStrings
{
    public const string AddComment = "review.editor.add";
    public const string Placeholder = "review.editor.placeholder";
    public const string Save = "review.editor.save";
    public const string Send = "review.editor.send";
    public const string Cancel = "review.editor.cancel";
    public const string Delete = "review.editor.delete";
    public const string SendFile = "review.editor.sendFile";
    public const string NoOwner = "review.editor.noOwner";
    public const string Faulted = "review.editor.faulted";
    public const string Lost = "review.editor.lost";
    public const string LostInFile = "review.editor.lostInFile";
    public const string BodyEmpty = "review.editor.bodyEmpty";
    public const string Busy = "review.editor.busy";
    public const string Saved = "review.editor.saved";
    public const string Sent = "review.editor.sent";
    public const string SaveFailed = "review.editor.saveFailed";
    public const string ChangedDuringSave = "review.editor.changedDuringSave";
    public const string SessionGone = "review.editor.sessionGone";
    public const string SentNotMarked = "review.editor.sentNotMarked";
    public const string StoreFailed = "review.editor.storeFailed";
    public const string NoDrafts = "review.editor.noDrafts";
    public const string Restored = "review.editor.restored";
    public const string RestoreFailed = "review.editor.restoreFailed";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(AddComment, "添加评论", "Add comment");
        Loc.Register(Placeholder, "写下对这段代码的评论…", "Comment on this code…");
        Loc.Register(Save, "保存草稿", "Save draft");
        Loc.Register(Send, "立即发送", "Send now");
        Loc.Register(Cancel, "取消", "Cancel");
        Loc.Register(Delete, "删除", "Delete");
        Loc.Register(SendFile, "发送本文件的草稿（{0}）", "Send drafts of this file ({0})");
        Loc.Register(NoOwner, "此文件不属于任何会话：评论与发送给 AI 已停用，文件仍可编辑保存。", "This file does not belong to a session: review comments and sending them to the AI are disabled; the file stays editable.");
        Loc.Register(Faulted, "编辑器与文档不同步，评论已停用。", "The editor is out of sync with the document; review comments are disabled.");
        Loc.Register(Lost, "评论对应的代码已变化，无法定位，不会被发送。请删除后重新评论。", "The commented code changed and can no longer be located; it will not be sent. Delete it and comment again.");
        Loc.Register(LostInFile, "本文件有评论已失效，不会发送；请先删除它们。", "Some comments of this file are lost and will not be sent; delete them first.");
        Loc.Register(BodyEmpty, "评论内容不能为空。", "The comment is empty.");
        Loc.Register(Busy, "上一次操作仍在进行。", "The previous operation is still running.");
        Loc.Register(Saved, "草稿已保存。", "Draft saved.");
        Loc.Register(Sent, "已发送给所属会话。", "Sent to the owning session.");
        Loc.Register(SaveFailed, "保存文件失败，评论未发送：{0}", "Saving the file failed; nothing was sent: {0}");
        Loc.Register(ChangedDuringSave, "发送期间文件或草稿已变化，评论未发送；草稿已保留，请重试。", "The file or drafts changed while sending; nothing was sent. The drafts are kept, try again.");
        Loc.Register(SessionGone, "所属会话已不可用，评论未发送；草稿已保留。", "The owning session is no longer available; nothing was sent. The drafts are kept.");
        Loc.Register(SentNotMarked, "已发送，但草稿状态未能更新，本次运行内不会重复发送，下次发送或重新打开时重试更新：{0}", "Sent, but the drafts could not be marked as sent. They will not be sent again in this run; the update is retried on the next send or reopen: {0}");
        Loc.Register(StoreFailed, "评论存储失败：{0}", "The review store failed: {0}");
        Loc.Register(NoDrafts, "本文件没有可发送的草稿。", "This file has no drafts to send.");
        Loc.Register(Restored, "排队的评论消息已被撤回，草稿已恢复。", "The queued review message was withdrawn; the drafts are restored.");
        Loc.Register(RestoreFailed, "排队消息已撤回，但草稿未能恢复，下次发送或重新打开时重试：{0}", "The queued message was withdrawn, but the drafts could not be restored; this is retried on the next send or reopen: {0}");
    }
}
