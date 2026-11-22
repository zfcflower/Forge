using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The session usage summary's own string table (wave W16a, L4): the label of the strip chip that opens it and one row
/// per number inside it. Keys are namespaced <c>usage.*</c> and are registered from a module initializer.
/// <para>The numbers themselves are formatted by <c>SessionInstrumentsViewModel</c> (token scaling, cost, elapsed time),
/// so a row is built as "label + value" and this table only owns the words. The strip's own usage tooltip stays in
/// <see cref="InstrumentsStrings"/>; this table is the expanded summary, not a second reading of the same numbers.</para>
/// </summary>
internal static class UsageStrings
{
    /// <summary>The chip's label and its hover line.</summary>
    public const string SummaryTitle = "usage.summary.title";
    public const string SummaryTooltip = "usage.summary.tooltip";

    public const string Input = "usage.summary.input";
    public const string Output = "usage.summary.output";
    public const string CacheRead = "usage.summary.cacheRead";
    public const string CacheWrite = "usage.summary.cacheWrite";
    public const string Cost = "usage.summary.cost";
    public const string Turns = "usage.summary.turns";

    /// <summary>Wall clock of the turns that recorded a checkpoint.</summary>
    public const string Duration = "usage.summary.duration";

    /// <summary>The share of input tokens served from the prompt cache, one decimal.</summary>
    public const string CacheHit = "usage.summary.cacheHit";

    public const string AverageInput = "usage.summary.avgInput";
    public const string AverageOutput = "usage.summary.avgOutput";
    public const string AverageCost = "usage.summary.avgCost";

    /// <summary>The model's context window — the one constant among the session's numbers, carried in the popover so it
    /// answers everything the strip's hover line does.</summary>
    public const string Window = "usage.summary.window";

    /// <summary>The cache-write total with its hour-lived share split out: <c>{0}（5m {1} · 1h {2}）</c>.</summary>
    public const string CacheWriteSplit = "usage.summary.cacheWrite.split";

    /// <summary>The context reclaimed by receipts (old tool results replaced by one line each), and how often that
    /// was enough on its own. The per-tool split names the tools; only the image and other rows need words.</summary>
    public const string Reduction = "usage.summary.reduction";
    public const string ReductionImage = "usage.summary.reduction.image";
    public const string ReductionOther = "usage.summary.reduction.other";
    public const string ReductionCount = "usage.summary.reduction.count";
    public const string ReductionRatio = "usage.summary.reduction.ratio";

    /// <summary>Where the numbers come from, and which part of them a non-git session cannot report.</summary>
    public const string Hint = "usage.summary.hint";

    /// <summary>The summary of a session that has not spent anything yet.</summary>
    public const string Empty = "usage.summary.empty";

    /// <summary>A reading this session has no source for (no price, no window, no checkpoint).</summary>
    public const string Unknown = "usage.summary.unknown";

    /// <summary>Turns read from the session's own checkpoints, and the ones this window watched: <c>{0}</c>, <c>{1}</c>.</summary>
    public const string TurnsValue = "usage.summary.turns.value";

    public static string Title => Loc.Instance[SummaryTitle];

    public static string Tooltip => Loc.Instance[SummaryTooltip];

    public static string InputLabel => Loc.Instance[Input];

    public static string OutputLabel => Loc.Instance[Output];

    public static string CacheReadLabel => Loc.Instance[CacheRead];

    public static string CacheWriteLabel => Loc.Instance[CacheWrite];

    public static string CostLabel => Loc.Instance[Cost];

    public static string TurnsLabel => Loc.Instance[Turns];

    public static string DurationLabel => Loc.Instance[Duration];

    public static string CacheHitLabel => Loc.Instance[CacheHit];

    public static string AverageInputLabel => Loc.Instance[AverageInput];

    public static string AverageOutputLabel => Loc.Instance[AverageOutput];

    public static string AverageCostLabel => Loc.Instance[AverageCost];

    public static string HintText => Loc.Instance[Hint];

    public static string EmptyText => Loc.Instance[Empty];

    public static string UnknownText => Loc.Instance[Unknown];

    public static string WindowLabel => Loc.Instance[Window];

    public static string CacheWriteSplitText(string total, string shortLived, string hourLived) =>
        Loc.Instance.Format(CacheWriteSplit, total, shortLived, hourLived);

    public static string ReductionLabel => Loc.Instance[Reduction];

    public static string ReductionImageText(string value) => Loc.Instance.Format(ReductionImage, value);

    public static string ReductionOtherText(string value) => Loc.Instance.Format(ReductionOther, value);

    public static string ReductionCountText(string total, string parts, int count) =>
        Loc.Instance.Format(ReductionCount, total, parts, count);

    public static string ReductionRatioLabel => Loc.Instance[ReductionRatio];

    public static string TurnsValueText(string recorded, string live) =>
        Loc.Instance.Format(TurnsValue, recorded, live);

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SummaryTitle, "用量", "Usage");
        Loc.Register(SummaryTooltip, "本会话累计的 token、成本与回合", "This session's tokens, cost and turns");
        Loc.Register(Input, "总输入", "Input");
        Loc.Register(Output, "总输出", "Output");
        Loc.Register(CacheRead, "缓存读", "Cache read");
        Loc.Register(CacheWrite, "缓存写", "Cache write");
        Loc.Register(Cost, "成本", "Cost");
        Loc.Register(Turns, "回合", "Turns");
        Loc.Register(Duration, "总耗时", "Total time");
        Loc.Register(CacheHit, "缓存命中率", "Cache hit rate");
        Loc.Register(AverageInput, "每回合输入", "Input per turn");
        Loc.Register(AverageOutput, "每回合输出", "Output per turn");
        Loc.Register(AverageCost, "每回合成本", "Cost per turn");
        Loc.Register(
            Hint,
            "累计来自本会话已保存的消息;回合数与耗时来自回合检查点,只有 git 会话才记录。",
            "The totals come from this session's stored messages; turns and time come from turn checkpoints, which only " +
            "a git session records.");
        Loc.Register(Empty, "这个会话还没有用量", "This session has no usage yet");
        Loc.Register(Unknown, "—", "—");
        Loc.Register(Window, "窗口", "Window");
        Loc.Register(CacheWriteSplit, "{0}（5m {1} · 1h {2}）", "{0} (5m {1} · 1h {2})");
        Loc.Register(TurnsValue, "{0} 已记录 · {1} 本次", "{0} recorded · {1} this window");
        Loc.Register(Reduction, "裁剪释放", "Reclaimed by receipts");
        Loc.Register(ReductionImage, "图片 {0}", "images {0}");
        Loc.Register(ReductionOther, "其它 {0}", "other {0}");
        Loc.Register(ReductionCount, "{0}（{1}）· {2} 条", "{0} ({1}) · {2} dropped");
        Loc.Register(ReductionRatio, "裁剪即够 / 仍需摘要", "Receipts enough / summarizer needed");
    }
}
