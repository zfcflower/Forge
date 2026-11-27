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

    public bool Enabled { get; init; } = true;

    /// <summary>Share of the usable budget at which a fold starts (0 &lt; trigger ≤ 1).</summary>
    public double TriggerRatio { get; init; } = 0.85;

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
    /// How many of the newest finished turns a fold always leaves alone. Turns, not tokens: a turn is folded whole or not at
    /// all (its entry is what the view carries), so the figure that can be honoured is a count. The default is what the view
    /// used to hardcode.
    /// </summary>
    public int KeepRecentTurns { get; init; } = ManagedContext.MinRecentEntries;

    /// <summary>Throws when the ratios are out of range.</summary>
    public void Validate()
    {
        if (!(TriggerRatio > 0 && TriggerRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TriggerRatio),
                $"The trigger line must satisfy 0 < trigger ({TriggerRatio}) <= 1.");
        }

        if (!(EmergencyRatio > TriggerRatio && EmergencyRatio <= 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmergencyRatio),
                $"The emergency line must sit above the trigger ({TriggerRatio}) and at most at 1, not {EmergencyRatio}.");
        }

        if (KeepRecentTurns < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(KeepRecentTurns),
                $"A fold must leave at least one finished turn alone, not {KeepRecentTurns}.");
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

        return Budget(window, maxOutput, wire);
    }

    /// <summary>
    /// R = the figure the request is sent with — <paramref name="wireMaxOutput"/> when known, else min(maxOutput, 0.2·W) and
    /// 0.2·W when that is unknown too — raised to at least 4096 but kept below half the window for tiny windows; U = W − R.
    /// Null when <paramref name="window"/> is unknown or not positive, and when the reserve leaves no input room at all (a
    /// request whose own output cap does not fit its window is not something a compaction can fix).
    /// </summary>
    /// <param name="wireMaxOutput">The cap the request carries; taken as is, because capping it is what lets a trigger line
    /// sit above the real prompt limit.</param>
    public static CompactionBudget? Budget(int? window, int? maxOutput, int? wireMaxOutput = null)
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

        return new CompactionBudget(w, reserve, w - reserve);
    }
}

/// <summary>A model's window <see cref="Window"/>, its output reserve and the usable input budget U = W − R.</summary>
public readonly record struct CompactionBudget(int Window, int Reserve, int Usable)
{
    public int TriggerTokens(double ratio) => (int)(Usable * ratio);

    /// <summary>The line a running turn compacts at: well above the trigger, so a turn usually finishes before it.</summary>
    public int EmergencyTokens(double ratio) => (int)(Usable * ratio);
}

/// <summary>
/// Automatic folding ran and could not bring the context under the line: nothing foldable is left. What remains — the stable
/// prefix, the tool definitions, the fold block and the newest turns the fold must keep verbatim — already exceeds it, so no
/// later request can change anything until something outside the history moves (a manual compaction, another model, a
/// smaller tool set).
/// </summary>
public sealed record AgentCompactionStalled(string AgentId, int ContextTokens, int TriggerTokens) : AgentEvent(AgentId);

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

    /// <summary>
    /// The instance's cumulative usage at this measurement — the same counter <see cref="AgentTurnCompleted"/> carries, observed
    /// after every request instead of once per turn, so the shell's readings move as the turn spends rather than at its end. A
    /// measurement that spends nothing (a reduction, a compaction) reports the counter as it stands, so a reader that
    /// differences sees nothing new.
    /// </summary>
    public TokenUsage Usage { get; init; } = TokenUsage.Empty;
}
