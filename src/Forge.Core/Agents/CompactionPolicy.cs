using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// When automatic compaction runs, expressed against each model's own window. Core only does the arithmetic and the
/// scheduling: the host supplies <see cref="ContextWindow"/> (and optionally <see cref="MaxOutputTokens"/>) from its
/// catalog and settings. A model whose window is unknown is never compacted automatically; only the context-overflow
/// fallback and manual compaction apply to it.
/// </summary>
public sealed record CompactionPolicy
{
    /// <summary>Smallest output reserve kept out of the usable input budget, when the window allows it.</summary>
    public const int MinReserveTokens = 4096;

    /// <summary>Largest share of the window reserved for output.</summary>
    public const double MaxReserveShare = 0.2;

    /// <summary>Share of the usable budget this instance's own content must grow by before a fruitless compaction is retried.</summary>
    public const double RegrowthShare = 0.1;

    /// <summary>Consecutive fruitless automatic attempts after which automatic compaction stops for the instance.</summary>
    public const int MaxFruitlessAttempts = 2;

    /// <summary>Most raw history a compaction keeps verbatim at least (also the figure used when the window is unknown).</summary>
    public const int MaxKeepRecentTokens = 20_000;

    /// <summary>Share of the usable budget kept verbatim at least, when that is below <see cref="MaxKeepRecentTokens"/>.</summary>
    public const double KeepRecentShare = 0.2;

    /// <summary>Smallest raw tail a configured keep-recent figure may ask for.</summary>
    public const int MinKeepRecentTokens = 1_000;

    public bool Enabled { get; init; } = true;

    /// <summary>Share of the usable budget at which compaction starts (0 &lt; target &lt; trigger ≤ 1).</summary>
    public double TriggerRatio { get; init; } = 0.85;

    /// <summary>Share of the usable budget a compaction aims to leave in context.</summary>
    public double TargetRatio { get; init; } = 0.5;

    /// <summary>Share of the usable budget at which a turn that is already running compacts (0 &lt; trigger &lt; urgent ≤ 1).</summary>
    public double EmergencyRatio { get; init; } = 0.95;

    /// <summary>Effective context window of a model in tokens; null (or a null result) means unknown.</summary>
    public Func<ModelSettings, int?>? ContextWindow { get; init; }

    /// <summary>Largest output of a model in tokens when <see cref="ModelSettings.MaxOutputTokens"/> is not set; null means unknown.</summary>
    public Func<ModelSettings, int?>? MaxOutputTokens { get; init; }

    /// <summary>
    /// How much of the newest conversation stays verbatim across a compaction, in tokens. Null (the default) derives it per
    /// model as min(20 000, 0.2·U); a value is clamped to [1 000, U/2] and still cut to half the target by the caller, so a
    /// large-window model can keep far more raw text than the derived figure allows without breaking the target.
    /// </summary>
    public int? KeepRecentTokens { get; init; }

    /// <summary>Throws when the ratios are out of range.</summary>
    public void Validate()
    {
        if (!(TargetRatio > 0 && TargetRatio < TriggerRatio && TriggerRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TriggerRatio),
                $"Compaction ratios must satisfy 0 < target ({TargetRatio}) < trigger ({TriggerRatio}) <= 1.");
        }

        if (!(EmergencyRatio > TriggerRatio && EmergencyRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmergencyRatio),
                $"The emergency line must sit above the trigger ({TriggerRatio}) and at most at 1, not {EmergencyRatio}.");
        }
    }

    /// <summary>The budget for one model, or null when its window is unknown.</summary>
    public CompactionBudget? BudgetFor(ModelSettings model)
    {
        ArgumentNullException.ThrowIfNull(model);
        int? window;
        int? maxOutput;
        try
        {
            window = ContextWindow?.Invoke(model);
            maxOutput = model.MaxOutputTokens ?? MaxOutputTokens?.Invoke(model);
        }
        catch (Exception)
        {
            return null;
        }

        return Budget(window, maxOutput, KeepRecentTokens);
    }

    /// <summary>
    /// R = min(maxOutput, 0.2·W) (0.2·W when maxOutput is unknown), raised to at least 4096 but kept below half the
    /// window for tiny windows; U = W − R. Null when <paramref name="window"/> is unknown or not positive.
    /// </summary>
    /// <param name="keepRecentTokens">A configured raw tail, clamped to [1 000, U/2]; null derives it from U.</param>
    public static CompactionBudget? Budget(int? window, int? maxOutput, int? keepRecentTokens = null)
    {
        if (window is not { } w || w <= 1)
        {
            return null;
        }

        var share = (int)(w * MaxReserveShare);
        var reserve = maxOutput is { } output && output > 0 ? Math.Min(output, share) : share;
        reserve = Math.Max(reserve, Math.Min(MinReserveTokens, w / 2));
        var usable = w - reserve;
        var derived = Math.Max(1, Math.Min(MaxKeepRecentTokens, (int)(usable * KeepRecentShare)));
        var keepRecent = keepRecentTokens is > 0
            ? Math.Clamp(keepRecentTokens.Value, Math.Min(MinKeepRecentTokens, usable), Math.Max(1, usable / 2))
            : derived;
        return new CompactionBudget(w, reserve, usable, keepRecent);
    }
}

/// <summary>A model's window <see cref="Window"/>, its output reserve, the usable input budget U = W − R and the raw tail kept.</summary>
public readonly record struct CompactionBudget(int Window, int Reserve, int Usable, int KeepRecent)
{
    public int TriggerTokens(double ratio) => (int)(Usable * ratio);

    /// <summary>The line a running turn compacts at: well above the trigger, so a turn usually finishes before it.</summary>
    public int EmergencyTokens(double ratio) => (int)(Usable * ratio);

    public int TargetTokens(double ratio) => Math.Max(1, (int)(Usable * ratio));

    public int RegrowthTokens => Math.Max(1, (int)(Usable * CompactionPolicy.RegrowthShare));

    /// <summary>The newest tokens kept verbatim: the configured figure or min(20 000, ⌊0.2·U⌋), at least 1.</summary>
    public int KeepRecentTokens => Math.Max(1, KeepRecent);
}

/// <summary>
/// Automatic compaction gave up on an instance: two consecutive attempts left the context above the trigger line (for
/// example because an inherited prefix alone exceeds it). It stays off until a manual compaction or another model.
/// </summary>
public sealed record AgentCompactionStalled(string AgentId, int ContextTokens, int TriggerTokens) : AgentEvent(AgentId);

/// <summary>
/// The context size C of an instance as automatic compaction sees it (the last real prompt size plus an estimate of what was
/// appended since), reported after every model request and after every compaction. <see cref="UsableTokens"/> is the
/// usable input budget U of the instance's model and <see cref="TriggerTokens"/> the trigger line under the policy in
/// force; both are null when the model's window is unknown.
/// </summary>
public sealed record AgentContextMeasured(string AgentId, int ContextTokens, int? UsableTokens, int? TriggerTokens)
    : AgentEvent(AgentId)
{
    /// <summary>
    /// The prompt-cache lifetime the request being reported asked for (<c>"5m"</c>/<c>"1h"</c>), so the shell can say which one is
    /// in force instead of leaving the user to infer it from the bill.
    /// </summary>
    public string? CacheTtl { get; init; }
}
