using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The first-run wizard's table: the three step headings and their one-line intros, the language step's frame text, the
/// provider step's action and the notification step's action, hint and no-guide sentence. The step's own status lines
/// (the provider catalogue sentence, the permission sentence, the language labels) are not duplicated here: they are
/// read from the tables that already own them (<c>Strings.Providers</c>, the notification permission texts, the language
/// section), so the wizard shows the same wording the settings page shows.
/// <para>Keys: <c>onboarding.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed
/// accessor methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// </summary>
internal static class OnboardingStrings
{
    // ---------------------------------------------------------------- the frame: step counter and navigation
    public const string StepCaptionKey = "onboarding.stepCaption";
    public const string NextKey = "onboarding.next";
    public const string DoneKey = "onboarding.done";
    public const string BackKey = "onboarding.back";
    public const string SkipKey = "onboarding.skip";

    // ---------------------------------------------------------------- the language step
    public const string LanguageTitleKey = "onboarding.language.title";
    public const string LanguageIntroKey = "onboarding.language.intro";

    // ---------------------------------------------------------------- the provider step
    public const string ProviderTitleKey = "onboarding.provider.title";
    public const string ProviderIntroKey = "onboarding.provider.intro";
    public const string ProviderOpenKey = "onboarding.provider.open";

    // ---------------------------------------------------------------- the notification permission step
    public const string NotificationsTitleKey = "onboarding.notifications.title";
    public const string NotificationsIntroKey = "onboarding.notifications.intro";
    public const string NotificationsOpenKey = "onboarding.notifications.open";
    public const string NotificationsHintKey = "onboarding.notifications.hint";
    public const string NotificationsNoGuideKey = "onboarding.notifications.noGuide";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(StepCaptionKey, "第 {0} 步，共 {1} 步", "Step {0} of {1}");
        Loc.Register(NextKey, "下一步", "Next");
        Loc.Register(DoneKey, "完成", "Done");
        Loc.Register(BackKey, "上一步", "Back");
        Loc.Register(SkipKey, "跳过", "Skip");

        Loc.Register(LanguageTitleKey, "语言", "Language");
        Loc.Register(
            LanguageIntroKey,
            "选择界面语言；立即生效，之后也可以在设置里改。",
            "Choose the interface language; it applies immediately and can be changed later in Settings.");

        Loc.Register(ProviderTitleKey, "连接提供商", "Connect a provider");
        Loc.Register(
            ProviderIntroKey,
            "对话要靠大模型提供商运行。现在连接一个，或先跳过、之后在设置里添加。",
            "Chats run on a model provider. Connect one now, or skip and add one later in Settings.");
        Loc.Register(ProviderOpenKey, "打开提供商设置", "Open provider settings");

        Loc.Register(NotificationsTitleKey, "通知权限", "Notification permission");
        Loc.Register(
            NotificationsIntroKey,
            "窗口不在前台时，桌面通知会提醒你审批与回合结果。现在确认权限，或之后再设置。",
            "While the window is in the background, desktop notifications surface approvals and finished turns. Confirm the permission now or leave it for later.");
        Loc.Register(NotificationsOpenKey, "打开系统通知设置", "Open the system notification settings");
        Loc.Register(
            NotificationsHintKey,
            "这个平台无法从应用内打开系统设置，请自己打开系统的通知设置。",
            "This platform cannot open its system settings from the app; open the notification settings yourself.");
        Loc.Register(
            NotificationsNoGuideKey,
            "这个构建没有提供通知权限信息。",
            "This build provides no notification permission information.");
    }

    // ---------------------------------------------------------------- accessors: the frame

    /// <summary>The counter over the wizard's steps, "step 2 of 3"; the numbers are grouped like every count is.</summary>
    public static string StepCaption(int step, int count) =>
        Loc.Instance.Format(StepCaptionKey, TextFormat.Number(step), TextFormat.Number(count));

    public static string Next => Loc.Instance[NextKey];

    public static string Done => Loc.Instance[DoneKey];

    public static string Back => Loc.Instance[BackKey];

    public static string Skip => Loc.Instance[SkipKey];

    // ---------------------------------------------------------------- accessors: the language step

    public static string LanguageTitle => Loc.Instance[LanguageTitleKey];

    public static string LanguageIntro => Loc.Instance[LanguageIntroKey];

    // ---------------------------------------------------------------- accessors: the provider step

    public static string ProviderTitle => Loc.Instance[ProviderTitleKey];

    public static string ProviderIntro => Loc.Instance[ProviderIntroKey];

    public static string ProviderOpen => Loc.Instance[ProviderOpenKey];

    // ---------------------------------------------------------------- accessors: the notification permission step

    public static string NotificationsTitle => Loc.Instance[NotificationsTitleKey];

    public static string NotificationsIntro => Loc.Instance[NotificationsIntroKey];

    public static string NotificationsOpen => Loc.Instance[NotificationsOpenKey];

    public static string NotificationsHint => Loc.Instance[NotificationsHintKey];

    public static string NotificationsNoGuide => Loc.Instance[NotificationsNoGuideKey];
}
