using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The <c>ask_user</c> question card (it lives in the approval queue) and its notification; keys are <c>questions.*</c>.
/// </summary>
internal static class QuestionsStrings
{
    public const string FreeTextWatermarkKey = "questions.card.freeText";
    public const string MultiHintKey = "questions.card.multiHint";
    public const string WaitingKey = "questions.card.waiting";
    public const string BoundsExactKey = "questions.card.bounds.exact";
    public const string BoundsRangeKey = "questions.card.bounds.range";
    public const string BoundsAtLeastKey = "questions.card.bounds.atLeast";
    public const string BoundsAtMostKey = "questions.card.bounds.atMost";
    public const string SubmitKey = "questions.card.submit";
    public const string DismissKey = "questions.card.dismiss";
    public const string TabTitleKey = "questions.tab.title";
    public const string NeedsInputKey = "questions.panel.needsInput";
    public const string DiscussKey = "questions.discuss.button";
    public const string DiscussingKey = "questions.discuss.active";
    public const string DiscussBannerKey = "questions.discuss.banner";
    public const string ReasonLabelKey = "questions.discuss.reasonLabel";
    public const string ReasonUnclearKey = "questions.discuss.reason.unclear";
    public const string ReasonUnrelatedKey = "questions.discuss.reason.unrelated";
    public const string ReasonContextKey = "questions.discuss.reason.context";
    public const string DiscussNoteKey = "questions.discuss.note";
    public const string DiscussHintKey = "questions.discuss.hint";
    public const string DiscussRequiredKey = "questions.discuss.required";
    public const string PreviousKey = "questions.panel.previous";
    public const string NextKey = "questions.panel.next";
    public const string OtherLabelKey = "questions.card.other";
    public const string SubmitDiscussKey = "questions.card.submitDiscuss";
    public const string TranscriptPendingKey = "questions.transcript.pending";
    public const string TranscriptDismissedKey = "questions.transcript.dismissed";
    public const string TranscriptCancelledKey = "questions.transcript.cancelled";
    public const string TranscriptDiscussingKey = "questions.transcript.discussing";
    public const string TranscriptAnsweredKey = "questions.transcript.answered";
    public const string NotifyTitleKey = "questions.notify.title";
    public const string NotifyBodyKey = "questions.notify.body";

    public static string TabTitle(string agent) => Loc.Instance.Format(TabTitleKey, agent);

    public static string NeedsInput(string session, string agent) => Loc.Instance.Format(NeedsInputKey, session, agent);

    public static string Submit => Loc.Instance[SubmitKey];

    public static string SubmitDiscuss => Loc.Instance[SubmitDiscussKey];

    public static string Waiting(int count) => Loc.Instance.Format(WaitingKey, count);

    /// <summary>The selection-bound hint of a multi-select question; <paramref name="capacity"/> is options plus free text.</summary>
    public static string BoundsHint(int min, int max, int capacity)
    {
        if (min == max)
        {
            return Loc.Instance.Format(BoundsExactKey, min);
        }

        if (max >= capacity)
        {
            return min <= 1 ? Loc.Instance[MultiHintKey] : Loc.Instance.Format(BoundsAtLeastKey, min);
        }

        return min <= 1 ? Loc.Instance.Format(BoundsAtMostKey, max) : Loc.Instance.Format(BoundsRangeKey, min, max);
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(FreeTextWatermarkKey, "输入您自己的回答…", "Type your own answer…");
        Loc.Register(TabTitleKey, "提问 · {0}", "Question · {0}");
        Loc.Register(NeedsInputKey, "{0} · {1} 正在等您的回答", "{0} · {1} is waiting for your answer");
        Loc.Register(DiscussKey, "先讨论下", "Discuss first");
        Loc.Register(DiscussingKey, "取消讨论", "Cancel discussion");
        Loc.Register(DiscussBannerKey, "Agent 会先与你讨论这个问题,然后再问一次。", "The agent will discuss this question with you first, then ask again.");
        Loc.Register(DiscussHintKey, "提出你的疑问,讨论清楚后模型会再次对该问题和剩下的问题进行提问", "Raise your doubts; once they are settled the model asks this question and the remaining ones again");
        Loc.Register(ReasonLabelKey, "原因(选一项,或在下面填写说明):", "Reason (pick one, or explain below):");
        Loc.Register(ReasonUnclearKey, "不够清楚", "Not clear enough");
        Loc.Register(ReasonUnrelatedKey, "与问题无关", "Not relevant");
        Loc.Register(ReasonContextKey, "需要更多上下文", "Need more context");
        Loc.Register(DiscussNoteKey, "希望 agent 解释什么?", "What should the agent explain?");
        Loc.Register(DiscussRequiredKey, "选一个原因或填写说明后即可开始讨论。", "Pick a reason or write a note to start the discussion.");
        Loc.Register(OtherLabelKey, "其他", "Other");
        Loc.Register(SubmitDiscussKey, "开始讨论", "Start discussion");
        Loc.Register(PreviousKey, "上一个", "Previous");
        Loc.Register(NextKey, "下一个", "Next");
        Loc.Register(TranscriptPendingKey, "等待回答", "Waiting for an answer");
        Loc.Register(TranscriptDismissedKey, "已忽略", "Dismissed");
        Loc.Register(TranscriptCancelledKey, "已取消", "Cancelled");
        Loc.Register(TranscriptDiscussingKey, "待讨论", "To discuss");
        Loc.Register(TranscriptAnsweredKey, "已回答", "Answered");
        Loc.Register(MultiHintKey, "可多选", "Select all that apply");
        Loc.Register(WaitingKey, "还有 {0} 个问题等待", "{0} more question(s) waiting");
        Loc.Register(BoundsExactKey, "选择 {0} 项", "Select exactly {0}");
        Loc.Register(BoundsRangeKey, "选择 {0}–{1} 项", "Select {0}–{1}");
        Loc.Register(BoundsAtLeastKey, "至少选 {0} 项", "Select at least {0}");
        Loc.Register(BoundsAtMostKey, "至多选 {0} 项", "Select at most {0}");
        Loc.Register(SubmitKey, "提交", "Submit");
        Loc.Register(DismissKey, "忽略", "Dismiss");
        Loc.Register(NotifyTitleKey, "Agent 在等你回答", "An agent is waiting for your answer");
        Loc.Register(NotifyBodyKey, "{0}:{1}", "{0}: {1}");
    }
}
