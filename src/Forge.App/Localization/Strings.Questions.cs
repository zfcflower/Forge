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
    public const string SubmitKey = "questions.card.submit";
    public const string DismissKey = "questions.card.dismiss";
    public const string NotifyTitleKey = "questions.notify.title";
    public const string NotifyBodyKey = "questions.notify.body";

    public static string CardTitle => Loc.Instance[CardTitleKey];

    public static string AskedBy(string agent) => Loc.Instance.Format(AskedByKey, agent);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(CardTitleKey, "Agent 向你提问", "The agent has a question");
        Loc.Register(AskedByKey, "提问者:{0}", "Asked by {0}");
        Loc.Register(FreeTextWatermarkKey, "其他(自由输入)", "Other (type your own answer)");
        Loc.Register(MultiHintKey, "可多选", "Select all that apply");
        Loc.Register(SubmitKey, "提交", "Submit");
        Loc.Register(DismissKey, "跳过", "Dismiss");
        Loc.Register(NotifyTitleKey, "Agent 在等你回答", "An agent is waiting for your answer");
        Loc.Register(NotifyBodyKey, "{0}:{1}", "{0}: {1}");
    }
}
