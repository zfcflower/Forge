using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The per-provider rate limit table (wave W15): the <c>rate-limit</c> settings section, which sets the budget every model
/// request is acquired from before it leaves, plus the spacing of automated resumes. Keys: <c>rateLimit.*</c>.
/// <para>Only names and wording live here. The ranges and defaults the hints name are written out (as in the other settings
/// tables) because they are compiled constants, not values a user or a provider supplies: a text that read them from the
/// preference would be a cached string that a language switch has to rebuild.</para>
/// </summary>
internal static class RateLimitStrings
{
    // ---------------------------------------------------------------- the `rate-limit` settings section
    public const string SettingsTitleKey = "rateLimit.settings.title";
    public const string SettingsHintKey = "rateLimit.settings.hint";
    public const string SettingsEnabledKey = "rateLimit.settings.enabled";
    public const string SettingsEnabledHintKey = "rateLimit.settings.enabledHint";
    public const string SettingsRequestsKey = "rateLimit.settings.requests";
    public const string SettingsRequestsHintKey = "rateLimit.settings.requestsHint";
    public const string SettingsTokensKey = "rateLimit.settings.tokens";
    public const string SettingsTokensHintKey = "rateLimit.settings.tokensHint";
    public const string SettingsAutomationGapKey = "rateLimit.settings.automationGap";
    public const string SettingsAutomationGapHintKey = "rateLimit.settings.automationGapHint";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string SettingsTitle => Loc.Instance[SettingsTitleKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    public static string SettingsEnabled => Loc.Instance[SettingsEnabledKey];

    public static string SettingsEnabledHint => Loc.Instance[SettingsEnabledHintKey];

    public static string SettingsRequests => Loc.Instance[SettingsRequestsKey];

    public static string SettingsRequestsHint => Loc.Instance[SettingsRequestsHintKey];

    public static string SettingsTokens => Loc.Instance[SettingsTokensKey];

    public static string SettingsTokensHint => Loc.Instance[SettingsTokensHintKey];

    public static string SettingsAutomationGap => Loc.Instance[SettingsAutomationGapKey];

    public static string SettingsAutomationGapHint => Loc.Instance[SettingsAutomationGapHintKey];

    internal static void Register()
    {
        Loc.Register(SettingsTitleKey, "按提供商限速", "Rate limits");
        Loc.Register(
            SettingsHintKey,
            "这些是本地预算：请求先在本地排队，而不是等提供商拒绝。每个提供商各有一份独立预算，一个慢的提供商不会占掉另一个的额度；默认值很宽松，只用来防止失控的循环在一秒里烧掉整分钟的额度。响应头里报出真实限额后，本地预算会自动收紧到那个数——所以只有想比提供商更严格时才需要改这里。改动对下一次请求就生效，不需要重启。",
            "These are local budgets: a request waits here instead of being refused by the provider. Each provider gets its own, so a slow one never eats an unrelated provider's allowance; the defaults are loose on purpose and only stop a runaway loop from burning a whole minute's allowance in one second. Once a response reports the real limit, the local budget tightens to it by itself, so these numbers only need changing when you want to be stricter than the provider. A change applies to the next request; no restart.");
        Loc.Register(SettingsEnabledKey, "启用本地限速", "Enforce the local budget");
        Loc.Register(
            SettingsEnabledHintKey,
            "关掉后请求不再经过本地预算，只受提供商自己的拒绝约束，自动恢复的间隔也一并失效。",
            "Off means no request is checked against the local budget and only the provider's own refusals apply; the automation gap stops spacing resumes too.");
        Loc.Register(SettingsRequestsKey, "每提供商请求/分钟", "Requests per minute per provider");
        Loc.Register(
            SettingsRequestsHintKey,
            "1 到 100000，默认 600。一分钟的普通对话只有几十次请求，所以默认值给得很宽；子 Agent 的并发扇出是最容易撞上它的情况。",
            "1 to 100000, 600 by default. A minute of ordinary conversation is dozens of requests, so the default is deliberately generous; a fan-out of sub-agents is what reaches it first.");
        Loc.Register(SettingsTokensKey, "每提供商令牌/分钟", "Tokens per minute per provider");
        Loc.Register(
            SettingsTokensHintKey,
            "1000 到 1000000000，默认 4000000。按令牌算，不按字符算；发出前先按估算额度扣，答案回来后再按真实用量对账。上下文被缓存时一分钟很容易过百万，所以默认值高于任何公开档位。",
            "1000 to 1000000000, 4000000 by default. Counted in tokens, not characters: the estimate is charged before the request and reconciled with the real usage when the answer arrives. A cached long context passes a million in a minute, so the default sits above every published tier.");
        Loc.Register(SettingsAutomationGapKey, "自动恢复间隔（秒）", "Automation gap (seconds)");
        Loc.Register(
            SettingsAutomationGapHintKey,
            "同一个提供商两次自动恢复之间至少隔这么久，0 到 600 秒，默认 10 秒；0 表示不间隔。",
            "How long two automated resumes of the same provider are kept apart: 0 to 600 seconds, 10 by default; 0 means no spacing.");
    }
}
