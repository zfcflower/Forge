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
    public const string SettingsTargetKey = "compaction.settings.target";
    public const string SettingsHintKey = "compaction.settings.hint";
    public const string SettingsTriggerInvalidKey = "compaction.settings.triggerInvalid";
    public const string SettingsTargetInvalidKey = "compaction.settings.targetInvalid";
    public const string SettingsSaveFailedKey = "compaction.settings.saveFailed";

    // ---------------------------------------------------------------- session
    public const string CommandDescriptionKey = "compaction.command.description";
    public const string StalledKey = "compaction.stalled";
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

    public static string SettingsTarget => Loc.Instance[SettingsTargetKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsTriggerInvalid => Loc.Instance.Format(SettingsTriggerInvalidKey, 50, 95);

    public static string SettingsTargetInvalid => Loc.Instance[SettingsTargetInvalidKey];

    public static string SettingsSaveFailed(string message) => Loc.Instance.Format(SettingsSaveFailedKey, message);

    public static string CommandDescription => Loc.Instance[CommandDescriptionKey];

    public static string GaugeTooltip => Loc.Instance[GaugeTooltipKey];

    /// <summary>Transcript notice: automatic compaction stopped for this agent.</summary>
    public static LocText Stalled(int contextTokens, int triggerTokens) =>
        new(StalledKey, TextFormat.Number(contextTokens), TextFormat.Number(triggerTokens));

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
            "阈值按每个模型的有效窗口计算,受上面的默认上下文窗口约束",
            "Thresholds are computed against each model's effective window, which the default context window above caps.");
        Loc.Register(SettingsTriggerInvalidKey, "触发线须在 {0}–{1} 之间", "The trigger must be between {0} and {1}.");
        Loc.Register(SettingsTargetInvalidKey, "目标须至少为 1，且小于触发线", "The target must be at least 1 and below the trigger.");
        Loc.Register(SettingsSaveFailedKey, "保存压缩设置失败：{0}", "Could not save the compaction settings: {0}");

        Loc.Register(CommandDescriptionKey, "压缩上下文（保留最近的原文；可在后面写关注点）", "Compact the context (keeps the recent messages; add text to set a focus)");
        Loc.Register(
            StalledKey,
            "上下文无法再压缩（{0} tokens，触发线 {1}），已停止自动压缩；可用 /compact 手动压缩或换模型",
            "The context cannot be compacted further ({0} tokens, trigger {1}); automatic compaction is off until /compact or another model.");
        Loc.Register(NothingKey, "没有可压缩的内容", "Nothing to compact.");
        Loc.Register(BusyKey, "当前轮次运行中，无法压缩", "Cannot compact while a turn is running.");
        Loc.Register(FailedKey, "压缩失败：{0}", "Compaction failed: {0}");
        Loc.Register(GaugeTooltipKey, "点击压缩上下文", "Click to compact the context");
    }
}
