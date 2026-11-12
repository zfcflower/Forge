using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.App.Providers;

namespace Forge.App.Localization;

/// <summary>
/// The model catalog's string table: the settings row that shows when it was last fetched, its address field, and the one
/// sentence a run of the daily refresh reports. Keys are namespaced with <c>modelCatalog.</c>.
/// </summary>
internal static class ModelCatalogStrings
{
    public const string LabelKey = "modelCatalog.label";
    public const string UpdatedKey = "modelCatalog.updated";
    public const string NeverKey = "modelCatalog.never";
    public const string CountsKey = "modelCatalog.counts";
    public const string UpdateKey = "modelCatalog.update";
    public const string UpdatingKey = "modelCatalog.updating";
    public const string SourceLabelKey = "modelCatalog.sourceLabel";
    public const string SourceHintKey = "modelCatalog.sourceHint";
    public const string SourceInvalidKey = "modelCatalog.sourceInvalid";
    public const string SkippedTodayKey = "modelCatalog.skippedToday";
    public const string UnchangedKey = "modelCatalog.unchanged";
    public const string FailedKey = "modelCatalog.failed";
    public const string UpdatedSummaryKey = "modelCatalog.updatedSummary";
    public const string ProviderListsKey = "modelCatalog.providerLists";
    public const string ProviderProblemKey = "modelCatalog.providerProblem";

    /// <summary>The row's label (the section is "providers", so this reads as one of its settings).</summary>
    public static string Label => Loc.Instance[LabelKey];

    public static string UpdateText => Loc.Instance[UpdateKey];

    public static string UpdatingText => Loc.Instance[UpdatingKey];

    public static string SourceLabel => Loc.Instance[SourceLabelKey];

    public static string SourceHint => Loc.Instance[SourceHintKey];

    public static string SourceInvalid => Loc.Instance[SourceInvalidKey];

    public static string NeverText => Loc.Instance[NeverKey];

    /// <summary>"Updated 2026-11-12 05:00" — the local time of the fetch.</summary>
    public static string UpdatedAt(DateTimeOffset moment) =>
        Loc.Instance.Format(UpdatedKey, moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    /// <summary>"235 providers · 8 993 models".</summary>
    public static string Counts(int providers, int models) => Loc.Instance.Format(
        CountsKey,
        providers.ToString(CultureInfo.InvariantCulture),
        models.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The one sentence a refresh reports to the run history or the settings row: what happened, what the catalog holds
    /// now, and — when some provider's own list call failed — how many did.
    /// </summary>
    public static string Summary(ModelCatalogRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var head = result.Outcome switch
        {
            ModelCatalogRefreshOutcome.Updated => Loc.Instance.Format(
                UpdatedSummaryKey,
                result.ProviderCount.ToString(CultureInfo.InvariantCulture),
                result.ModelCount.ToString(CultureInfo.InvariantCulture)),
            ModelCatalogRefreshOutcome.NotModified => Loc.Instance[UnchangedKey],
            ModelCatalogRefreshOutcome.Skipped => Loc.Instance[SkippedTodayKey],
            _ => Loc.Instance.Format(FailedKey, result.Failure ?? string.Empty),
        };

        if (result.ProviderLists > 0)
        {
            head += Loc.Instance.Format(ProviderListsKey, result.ProviderLists.ToString(CultureInfo.InvariantCulture));
        }

        if (result.ProviderProblems is { Count: > 0 } problems)
        {
            head += Loc.Instance.Format(
                ProviderProblemKey,
                problems.Count.ToString(CultureInfo.InvariantCulture),
                problems[0]);
        }

        return head;
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();

    private static void Register()
    {
        Loc.Register(LabelKey, "模型目录", "Model catalog");
        Loc.Register(UpdatedKey, "更新于 {0}", "Updated {0}");
        Loc.Register(NeverKey, "尚未更新", "Not updated yet");
        Loc.Register(CountsKey, "{0} 个提供商 · {1} 个模型", "{0} providers · {1} models");
        Loc.Register(UpdateKey, "立即更新", "Update now");
        Loc.Register(UpdatingKey, "正在更新…", "Updating…");
        Loc.Register(SourceLabelKey, "目录地址", "Catalog address");
        Loc.Register(
            SourceHintKey,
            "任何返回与 models.dev 相同 JSON 结构的地址（镜像或内网代理）。",
            "Any address serving the same JSON as models.dev (a mirror or an internal proxy).");
        Loc.Register(SourceInvalidKey, "目录地址必须是 http 或 https 的完整地址", "The catalog address must be an absolute http or https URL");
        Loc.Register(SkippedTodayKey, "今天已经更新过，跳过", "Already updated today — skipped");
        Loc.Register(UnchangedKey, "模型目录已是最新", "The model catalog is already up to date");
        Loc.Register(FailedKey, "模型目录更新失败：{0}", "The model catalog update failed: {0}");
        Loc.Register(UpdatedSummaryKey, "模型目录已更新：{0} 个提供商 · {1} 个模型", "Model catalog updated: {0} providers · {1} models");
        Loc.Register(ProviderListsKey, "；提供商模型列表 {0} 个", "; {0} provider model lists refreshed");
        Loc.Register(ProviderProblemKey, "；{0} 个提供商的列表失败：{1}", "; {0} provider lists failed: {1}");
    }
}
