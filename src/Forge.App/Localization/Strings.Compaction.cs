using System.Runtime.CompilerServices;
using Forge.App.Agents;

namespace Forge.App.Localization;

/// <summary>
/// The compaction table: the settings block next to the default context window, the <c>/compact</c> command, the manual
/// compaction notices and the notice shown when automatic compaction gives up. Keys: <c>compaction.*</c>.
/// </summary>
internal static class CompactionStrings
{
    // ---------------------------------------------------------------- settings
    public const string SettingsTitleKey = "compaction.settings.title";
    public const string SettingsEnabledKey = "compaction.settings.enabled";
    public const string SettingsTriggerKey = "compaction.settings.trigger";
    public const string SettingsTriggerHintKey = "compaction.settings.triggerHint";

    public const string SettingsKeepRecentTurnsKey = "compaction.settings.keepRecentTurns";

    public const string SettingsKeepRecentTurnsHintKey = "compaction.settings.keepRecentTurnsHint";

    public const string SettingsKeepRecentTurnsInvalidKey = "compaction.settings.keepRecentTurnsInvalid";

    public const string SettingsEmergencyKey = "compaction.settings.emergency";

    public const string SettingsEmergencyHintKey = "compaction.settings.emergencyHint";

    public const string SettingsEmergencyInvalidKey = "compaction.settings.emergencyInvalid";
    public const string SettingsHintKey = "compaction.settings.hint";
    public const string SettingsTriggerInvalidKey = "compaction.settings.triggerInvalid";
    public const string SettingsSaveFailedKey = "compaction.settings.saveFailed";

    // ---------------------------------------------------------------- session
    public const string CommandDescriptionKey = "compaction.command.description";
    public const string BlockedKey = "compaction.blocked";
    public const string NothingKey = "compaction.manual.nothing";
    public const string BusyKey = "compaction.manual.busy";
    public const string FailedKey = "compaction.manual.failed";
    public const string GaugeTooltipKey = "compaction.gauge.tooltip";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string SettingsTitle => Loc.Instance[SettingsTitleKey];

    public static string SettingsEnabled => Loc.Instance[SettingsEnabledKey];

    public static string SettingsTrigger => Loc.Instance[SettingsTriggerKey];

    public static string SettingsTriggerHint => Loc.Instance[SettingsTriggerHintKey];

    public static string SettingsKeepRecentTurns => Loc.Instance[SettingsKeepRecentTurnsKey];

    public static string SettingsKeepRecentTurnsHint =>
        Loc.Instance.Format(
            SettingsKeepRecentTurnsHintKey,
            CompactionSettings.MinKeepRecentTurns,
            CompactionSettings.MaxKeepRecentTurns,
            CompactionSettings.DefaultKeepRecentTurns,
            CompactionSettings.MinKeepRecentTurns,
            CompactionSettings.MaxKeepRecentTurns);

    public static string SettingsKeepRecentTurnsInvalid =>
        Loc.Instance.Format(
            SettingsKeepRecentTurnsInvalidKey,
            CompactionSettings.MinKeepRecentTurns,
            CompactionSettings.MaxKeepRecentTurns);

    public static string SettingsEmergency => Loc.Instance[SettingsEmergencyKey];

    public static string SettingsEmergencyHint => Loc.Instance[SettingsEmergencyHintKey];

    public static string SettingsEmergencyInvalid => Loc.Instance[SettingsEmergencyInvalidKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsTriggerInvalid => Loc.Instance.Format(SettingsTriggerInvalidKey, 50, 95);

    public static string SettingsSaveFailed(string message) => Loc.Instance.Format(SettingsSaveFailedKey, message);

    public static string CommandDescription => Loc.Instance[CommandDescriptionKey];

    public static string GaugeTooltip => Loc.Instance[GaugeTooltipKey];

    /// <summary>Transcript notice: nothing is left to fold and the context is still above the line.</summary>
    public static LocText Blocked(int contextTokens, int triggerTokens) =>
        new(BlockedKey, TextFormat.Number(contextTokens), TextFormat.Number(triggerTokens));

    public static LocText Nothing() => new(NothingKey);

    public static LocText Busy() => new(BusyKey);

    public static LocText Failed(string message) => new(FailedKey, message);

    internal static void Register()
    {
        Loc.Register(SettingsTitleKey, "上下文压缩", "Context compaction");
        Loc.Register(SettingsEnabledKey, "自动压缩", "Compact automatically");
        Loc.Register(SettingsTriggerKey, "触发线（可用窗口的 %）", "Trigger (% of the usable window)");
        Loc.Register(
            SettingsHintKey,
            "用量达到「触发线 × 可用输入」时，把除最新 N 轮以外的所有已结算轮折成一行指针。折叠不花模型调用，所以一次折到底：省下的上下文能撑更久，折叠次数更少。折叠只是不再带进请求，原文仍在库里（read_context / read_history 取回）。可用输入 = 模型窗口 − 输出预留。",
            "Reaching trigger × usable input folds every finished turn except the newest N into one pointer line. A fold costs no model call, so it goes as deep as it can: the freed context lasts longer and folds happen less often. Nothing is lost — a folded turn is still in the store (read_context / read_history). Usable input = window − output reserve.");
        Loc.Register(
            SettingsTriggerHintKey,
            "上下文用到可用输入的这么多时开始压缩。调低=更早压、原文更少；调高=压缩次数更少，但更容易撞上溢出重试。建议 80–90；中文或长文档会话建议 75。",
            "Compaction starts once the context reaches this share of the usable input. Lower = earlier, less raw text kept; higher = fewer compactions but a likelier overflow retry. 80–90 is a good range, 75 for CJK-heavy sessions.");
        Loc.Register(SettingsTriggerInvalidKey, "触发线须在 {0}–{1} 之间", "The trigger must be between {0} and {1}.");
        Loc.Register(SettingsKeepRecentTurnsKey, "保留最新轮数", "Keep newest turns");
        Loc.Register(
            SettingsKeepRecentTurnsHintKey,
            "折叠时永远原样保留的最新轮数（{0}–{1}）。轮是整体折或不折，所以这里按轮计数而不是按 token；默认 {2}。调大 = 最近的上下文更完整，但折叠能腾出的空间更少。",
            "How many of the newest finished turns a fold always leaves alone ({0}–{1}). A turn is folded whole or not at all, so this counts turns rather than tokens; the default is {2}. Higher keeps more of the recent past intact and leaves less room for a fold to free.");
        Loc.Register(
            SettingsKeepRecentTurnsInvalidKey,
            "保留最新轮数须在 {0}–{1} 之间",
            "Keep newest turns must be between {0} and {1}");
        Loc.Register(SettingsEmergencyKey, "回合内触发线（可用窗口的 %）", "Mid-turn line (% of the usable window)");
        Loc.Register(
            SettingsEmergencyHintKey,
            "一轮还没结束、工具调用仍在进行时，只有上下文超过这个线才中途压缩。默认 95：让当前回合把原文收齐，只在快撞窗口时才动。调高=中途压缩更少、更可能靠一次被拒的请求兜底。",
            "While a turn is still running and collecting tool results, only this line starts a compaction mid-turn. Default 95: the turn keeps its raw evidence and compaction waits until the window is nearly full. Higher = fewer mid-turn compactions but a likelier rejected request.");
        Loc.Register(
            SettingsEmergencyInvalidKey,
            "回合内触发线须在 90–99 之间，且高于触发线",
            "The mid-turn line must be between 90 and 99 and above the trigger");
        Loc.Register(SettingsSaveFailedKey, "保存压缩设置失败：{0}", "Could not save the compaction settings: {0}");

        Loc.Register(CommandDescriptionKey, "折叠上下文（只留最新几轮；可在后面写关注点）", "Fold the context (keeps the newest turns; add text to set a focus)");
        Loc.Register(
            BlockedKey,
            "无可折叠的部分（{0} tokens，触发线 {1}）：系统前缀、工具定义与最新几轮本身已经占满窗口，折叠旧轮也降不到线下；换一个窗口更大的模型或减少工具才能继续",
            "Nothing left to fold ({0} tokens, trigger {1}): the stable prefix, the tool definitions and the newest turns already exceed the line, so folding older turns cannot help; a model with a larger window, or fewer tools, is the way forward.");
        Loc.Register(NothingKey, "没有可折叠的内容", "Nothing to fold.");
        Loc.Register(BusyKey, "当前轮次运行中，无法压缩", "Cannot compact while a turn is running.");
        Loc.Register(FailedKey, "压缩失败：{0}", "Compaction failed: {0}");
        Loc.Register(GaugeTooltipKey, "点击压缩上下文", "Click to compact the context");
    }
}
