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
    /// The output cap the request will actually be sent with, when the host can resolve it — the catalogue's limit plus the
    /// lift an adapter applies for itself (Anthropic sends <c>max_tokens = thinkingBudget + headroom</c> whenever the budget
    /// reaches the cap). Consulted first: the model's own pin is only one of its inputs. Null (the usual case for protocols
    /// that send no cap at all) leaves <see cref="MaxOutputTokens"/> in charge.
    /// </summary>
    public Func<ModelSettings, int?>? WireMaxOutputTokens { get; init; }

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
        int? wire;
        try
        {
            window = ContextWindow?.Invoke(model);
            maxOutput = model.MaxOutputTokens ?? MaxOutputTokens?.Invoke(model);
            wire = WireMaxOutputTokens?.Invoke(model);
        }
        catch (Exception)
        {
            return null;
        }

        return Budget(window, maxOutput, KeepRecentTokens, wire);
    }

    /// <summary>
    /// R = the figure the request is sent with — <paramref name="wireMaxOutput"/> when known, else min(maxOutput, 0.2·W) and
    /// 0.2·W when that is unknown too — raised to at least 4096 but kept below half the window for tiny windows; U = W − R.
    /// Null when <paramref name="window"/> is unknown or not positive, and when the reserve leaves no input room at all (a
    /// request whose own output cap does not fit its window is not something a compaction can fix).
    /// </summary>
    /// <param name="keepRecentTokens">A configured raw tail, clamped to [1 000, U/2]; null derives it from U.</param>
    /// <param name="wireMaxOutput">The cap the request carries; taken as is, because capping it is what lets a trigger line
    /// sit above the real prompt limit.</param>
    public static CompactionBudget? Budget(int? window, int? maxOutput, int? keepRecentTokens = null, int? wireMaxOutput = null)
    {
        if (window is not { } w || w <= 1)
        {
            return null;
        }

        var share = (int)(w * MaxReserveShare);
        var reserve = wireMaxOutput is { } wire && wire > 0
            ? wire
            : maxOutput is { } output && output > 0 ? Math.Min(output, share) : share;
        reserve = Math.Max(reserve, Math.Min(MinReserveTokens, w / 2));
        if (reserve >= w)
        {
            return null;
        }

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
/// The reduction rung reclaimed context: old tool results were replaced by receipts. Emitted once per pass that
/// actually replaced something, with the release split by the tool that produced it.
/// </summary>
public sealed record AgentContextReduced(
    string AgentId,
    int TokensBefore,
    int TokensAfter,
    int DroppedResults,
    int ReadTokens,
    int BashTokens,
    int ImageTokens,
    int OtherTokens) : AgentEvent(AgentId);

/// <summary>Why automatic summarization stopped on an instance.</summary>
public enum CompactionStallReason
{
    /// <summary>Two consecutive summaries left the context above the line they compacted against.</summary>
    Ineffective = 0,

    /// <summary>
    /// What no summary can drop — the inherited prefix, the stable prefix, the tool definitions and the newest messages the
    /// summarizer must keep — already exceeds the line.
    /// </summary>
    Irreducible = 1,
}

/// <summary>
/// Automatic summarization gave up on an instance: either <see cref="CompactionStallReason.Ineffective"/> (two attempts left the
/// context above the trigger line) or <see cref="CompactionStallReason.Irreducible"/> (a floor no summary can get below already
/// exceeds it, detected before any summarizer request). It stays off until a manual compaction, another model, or a context
/// reduction that brings the instance back under the line.
/// </summary>
public sealed record AgentCompactionStalled(string AgentId, int ContextTokens, int TriggerTokens) : AgentEvent(AgentId)
{
    /// <summary>Which of the two dead ends was hit; the shell says a different thing for each.</summary>
    public CompactionStallReason Reason { get; init; }
}

/// <summary>
/// The context size C of an instance as automatic compaction sees it (the last real prompt size plus an estimate of what was
/// appended since), reported after every model request and after every compaction. <see cref="UsableTokens"/> is the
/// usable input budget U of the instance's model and <see cref="TriggerTokens"/> the line the measurement was judged
/// against — the trigger line before a turn's first request, the emergency line inside a running turn; both are null
/// when the model's window is unknown.
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
