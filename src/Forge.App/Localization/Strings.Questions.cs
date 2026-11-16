using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The <c>ask_user</c> question card (it lives in the approval queue) and its notification; keys are <c>questions.*</c>.
/// </summary>
internal static class QuestionsStrings
{
    public const string CardTitleKey = "questions.card.title";
    public const string AskedByKey = "questions.card.askedBy";
    public const string FreeTextWatermarkKey = "questions.card.freeText";
    public const string MultiHintKey = "questions.card.multiHint";
    public const string SkipKey = "questions.card.skip";
    public const string AllSkippedKey = "questions.card.allSkipped";
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
    public const string DiscussCancelHintKey = "questions.discuss.cancelHint";
    public const string OtherLabelKey = "questions.card.other";
    public const string SubmitDiscussKey = "questions.card.submitDiscuss";
    public const string TranscriptPendingKey = "questions.transcript.pending";
    public const string TranscriptDismissedKey = "questions.transcript.dismissed";
    public const string TranscriptCancelledKey = "questions.transcript.cancelled";
    public const string TranscriptDiscussingKey = "questions.transcript.discussing";
    public const string TranscriptSkippedKey = "questions.transcript.skipped";
    public const string TranscriptNoAnswerKey = "questions.transcript.noAnswer";
    public const string TranscriptAnsweredKey = "questions.transcript.answered";
    public const string NotifyTitleKey = "questions.notify.title";
    public const string NotifyBodyKey = "questions.notify.body";

    public static string CardTitle => Loc.Instance[CardTitleKey];

    public static string AskedBy(string agent) => Loc.Instance.Format(AskedByKey, agent);

    public static string TabTitle(string agent) => Loc.Instance.Format(TabTitleKey, agent);

    public static string NeedsInput(string agent) => Loc.Instance.Format(NeedsInputKey, agent);

    public static string Submit => Loc.Instance[SubmitKey];

    public static string SubmitDiscuss => Loc.Instance[SubmitDiscussKey];

    public static string AllSkippedHint => Loc.Instance[AllSkippedKey];

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
        Loc.Register(CardTitleKey, "Agent 向你提问", "The agent has a question");
        Loc.Register(AskedByKey, "提问者:{0}", "Asked by {0}");
        Loc.Register(FreeTextWatermarkKey, "输入您自己的回答…", "Type your own answer…");
        Loc.Register(TabTitleKey, "提问 · {0}", "Question · {0}");
        Loc.Register(NeedsInputKey, "需要您输入 · {0} 正在等待", "Input needed · {0} is waiting");
        Loc.Register(DiscussKey, "讨论", "Discuss");
        Loc.Register(DiscussingKey, "讨论中", "Discussing");
        Loc.Register(DiscussBannerKey, "Agent 会先与你讨论这个问题,然后再问一次。", "The agent will discuss this question with you first, then ask again.");
        Loc.Register(ReasonLabelKey, "原因(可选):", "Reason (optional):");
        Loc.Register(ReasonUnclearKey, "不够清楚", "Not clear enough");
        Loc.Register(ReasonUnrelatedKey, "与问题无关", "Not relevant");
        Loc.Register(ReasonContextKey, "需要更多上下文", "Need more context");
        Loc.Register(DiscussNoteKey, "希望 agent 解释什么?(可选)", "What should the agent explain? (optional)");
        Loc.Register(DiscussCancelHintKey, "选择某个选项或自行作答会取消此操作。", "Picking an option or typing your own answer cancels this.");
        Loc.Register(OtherLabelKey, "其他 —— 你自己的答案", "Other — your own answer");
        Loc.Register(SubmitDiscussKey, "开始讨论", "Start discussion");
        Loc.Register(TranscriptPendingKey, "等待回答", "Waiting for an answer");
        Loc.Register(TranscriptDismissedKey, "已忽略", "Dismissed");
        Loc.Register(TranscriptCancelledKey, "已取消", "Cancelled");
        Loc.Register(TranscriptDiscussingKey, "讨论中", "Discussing");
        Loc.Register(TranscriptSkippedKey, "已跳过", "Skipped");
        Loc.Register(TranscriptNoAnswerKey, "未作答", "No answer");
        Loc.Register(TranscriptAnsweredKey, "已回答", "Answered");
        Loc.Register(MultiHintKey, "可多选", "Select all that apply");
        Loc.Register(SkipKey, "跳过本题", "Skip this question");
        Loc.Register(AllSkippedKey, "所有问题都已跳过,请改用“忽略”", "Every question is skipped; use Dismiss instead");
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
