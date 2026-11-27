using System.Runtime.CompilerServices;

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
    public const string SettingsTargetHintKey = "compaction.settings.targetHint";

    public const string SettingsKeepRecentKey = "compaction.settings.keepRecent";

    public const string SettingsKeepRecentHintKey = "compaction.settings.keepRecentHint";

    public const string SettingsKeepRecentInvalidKey = "compaction.settings.keepRecentInvalid";

    public const string SettingsEmergencyKey = "compaction.settings.emergency";

    public const string SettingsEmergencyHintKey = "compaction.settings.emergencyHint";

    public const string SettingsEmergencyInvalidKey = "compaction.settings.emergencyInvalid";
    public const string SettingsTargetKey = "compaction.settings.target";
    public const string SettingsHintKey = "compaction.settings.hint";
    public const string SettingsTriggerInvalidKey = "compaction.settings.triggerInvalid";
    public const string SettingsTargetInvalidKey = "compaction.settings.targetInvalid";
    public const string SettingsSaveFailedKey = "compaction.settings.saveFailed";

    // ---------------------------------------------------------------- session
    public const string CommandDescriptionKey = "compaction.command.description";
    public const string StalledKey = "compaction.stalled";
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

    public static string SettingsTargetHint => Loc.Instance[SettingsTargetHintKey];

    public static string SettingsKeepRecent => Loc.Instance[SettingsKeepRecentKey];

    public static string SettingsKeepRecentHint => Loc.Instance[SettingsKeepRecentHintKey];

    public static string SettingsKeepRecentInvalid =>
        Loc.Instance.Format(SettingsKeepRecentInvalidKey, 1_000, 1_000_000);

    public static string SettingsEmergency => Loc.Instance[SettingsEmergencyKey];

    public static string SettingsEmergencyHint => Loc.Instance[SettingsEmergencyHintKey];

    public static string SettingsEmergencyInvalid => Loc.Instance[SettingsEmergencyInvalidKey];

    public static string SettingsTarget => Loc.Instance[SettingsTargetKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsTriggerInvalid => Loc.Instance.Format(SettingsTriggerInvalidKey, 50, 95);

    public static string SettingsTargetInvalid => Loc.Instance[SettingsTargetInvalidKey];

    public static string SettingsSaveFailed(string message) => Loc.Instance.Format(SettingsSaveFailedKey, message);

    public static string CommandDescription => Loc.Instance[CommandDescriptionKey];

    public static string GaugeTooltip => Loc.Instance[GaugeTooltipKey];

    /// <summary>Transcript notice: automatic compaction stopped for this agent after attempts that could not reach the line.</summary>
    public static LocText Stalled(int contextTokens, int triggerTokens) =>
        new(StalledKey, TextFormat.Number(contextTokens), TextFormat.Number(triggerTokens));

    /// <summary>Transcript notice: what no summary can drop already exceeds the line, so no attempt was made.</summary>
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
        Loc.Register(SettingsTargetKey, "压缩目标（可用窗口的 %）", "Target (% of the usable window)");
        Loc.Register(
            SettingsHintKey,
            "用量达到「触发线 × 可用输入」就把最旧的轮折成一行指针 → 折到「目标 × 可用输入」；最新 3 轮不动，折叠只是不再带进请求，原文仍在库里（read_context / read_history 取回）。可用输入 = 模型窗口 − 输出预留。",
            "Reaching trigger × usable input folds the oldest turns into one pointer line, down to target × usable input; the newest 3 turns stay verbatim and nothing is lost — a folded turn is still in the store (read_context / read_history). Usable input = window − output reserve.");
        Loc.Register(
            SettingsTriggerHintKey,
            "上下文用到可用输入的这么多时开始压缩。调低=更早压、原文更少；调高=压缩次数更少，但更容易撞上溢出重试。建议 80–90；中文或长文档会话建议 75。",
            "Compaction starts once the context reaches this share of the usable input. Lower = earlier, less raw text kept; higher = fewer compactions but a likelier overflow retry. 80–90 is a good range, 75 for CJK-heavy sessions.");
        Loc.Register(
            SettingsTargetHintKey,
            "压缩后上下文的大小（摘要 + 保留的原文尾部），不是“保留多少原文”。它决定下一次压缩还有多远：85→50 大约留出 0.35×可用输入，约 5–6 次普通读取。建议取触发线的大约六成。",
            "The size of the context after compaction (summary + the raw tail kept), not “how much raw text survives”. It decides how far off the next compaction is: 85→50 leaves about 0.35 × usable input, roughly 5–6 ordinary reads. About 60% of the trigger is a good target.");
        Loc.Register(SettingsTriggerInvalidKey, "触发线须在 {0}–{1} 之间", "The trigger must be between {0} and {1}.");
        Loc.Register(SettingsKeepRecentKey, "保留最近（token）", "Keep recent (tokens)");
        Loc.Register(
            SettingsKeepRecentHintKey,
            "压缩时原样保留的最新对话量。留空 = 自动（min(20 000, 0.2×可用输入)）；1M 窗口的模型建议 100 000。实际值还会被夹到压缩目标的一半以内，保证仍能压到目标。",
            "How much of the newest conversation stays verbatim across a compaction. Empty = derived (min(20 000, 0.2 × usable input)); 100 000 suits a 1M-window model. It is still capped at half the target so a compaction can reach the target.");
        Loc.Register(
            SettingsKeepRecentInvalidKey,
            "保留最近须在 {0}–{1} 之间，或留空",
            "Keep recent must be between {0} and {1}, or empty");
        Loc.Register(SettingsEmergencyKey, "回合内触发线（可用窗口的 %）", "Mid-turn line (% of the usable window)");
        Loc.Register(
            SettingsEmergencyHintKey,
            "一轮还没结束、工具调用仍在进行时，只有上下文超过这个线才中途压缩。默认 95：让当前回合把原文收齐，只在快撞窗口时才动。调高=中途压缩更少、更可能靠一次被拒的请求兜底。",
            "While a turn is still running and collecting tool results, only this line starts a compaction mid-turn. Default 95: the turn keeps its raw evidence and compaction waits until the window is nearly full. Higher = fewer mid-turn compactions but a likelier rejected request.");
        Loc.Register(
            SettingsEmergencyInvalidKey,
            "回合内触发线须在 90–99 之间，且高于触发线",
            "The mid-turn line must be between 90 and 99 and above the trigger");
        Loc.Register(SettingsTargetInvalidKey, "目标须至少为 1，且小于触发线", "The target must be at least 1 and below the trigger.");
        Loc.Register(SettingsSaveFailedKey, "保存压缩设置失败：{0}", "Could not save the compaction settings: {0}");

        Loc.Register(CommandDescriptionKey, "压缩上下文（保留最近的原文；可在后面写关注点）", "Compact the context (keeps the recent messages; add text to set a focus)");
        Loc.Register(
            StalledKey,
            "上下文仍高于触发线（{0} tokens，触发线 {1}），已没有可折叠的旧轮；可用 /compact 手动折叠或换一个窗口更大的模型",
            "The context is still above the trigger line ({0} tokens, trigger {1}) and there is nothing left to fold; /compact or a model with a larger window is the way forward.");
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
