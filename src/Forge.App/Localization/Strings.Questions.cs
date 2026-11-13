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
    public const string NotifyTitleKey = "questions.notify.title";
    public const string NotifyBodyKey = "questions.notify.body";

    public static string CardTitle => Loc.Instance[CardTitleKey];

    public static string AskedBy(string agent) => Loc.Instance.Format(AskedByKey, agent);

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
        Loc.Register(FreeTextWatermarkKey, "其他(自由输入)", "Other (type your own answer)");
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
