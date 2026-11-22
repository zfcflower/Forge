using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Core.Providers;

namespace Forge.App.ViewModels;

/// <summary>
/// One session's readout: the composer strip above the input. Left of the strip it answers "what is this session doing"
/// (running state, working directory, branch, ahead/behind, CI, how full the context window is); right of it, the resource
/// button (it opens the resource window) and the sub-agent toggle, plus the project the session belongs to.
/// <para>Everything here is derived, never authoritative. Usage is the root agent's, because that is what the context
/// window measures and what the selected model prices; a sub-agent's own spend stays on its tab. A source that knows
/// nothing leaves the readout blank instead of inventing a percentage or a price.</para>
/// <para>Threading: UI thread only; an MCP state change that arrives on another thread is posted to it.</para>
/// </summary>
/// <summary>One slanted segment of the context gauge: filled up to the rounded fraction, hollow after it.</summary>
internal sealed record ContextSegment(bool IsFilled)
{
    /// <summary>How many segments the gauge has; the view lays out exactly this many.</summary>
    public const int Count = 5;

    public bool IsEmpty => !IsFilled;
}

/// <summary>
/// One line of the session usage summary: a label and the number behind it. Both are already formatted by the strip (token
/// scaling, price, elapsed time), so the row holds no formula and a language switch rebuilds the list.
/// </summary>
internal sealed record UsageStatRow(string Label, string Value);

/// <summary>How close the root context is to the automatic-compaction trigger line; the gauge is coloured by it.</summary>
internal enum ContextLevel
{
    Normal,

    /// <summary>Within 10 percentage points of U below the trigger line.</summary>
    Warning,

    /// <summary>At or past the trigger line.</summary>
    Over,
}

/// <summary>
/// One session's readout: the composer strip above the input.
/// <para>Left of the strip it answers "what is this session doing" (branch, ahead/behind, CI, the resource button and the
/// workspace button); the far right carries the token reading — <c>RC</c> cache-read hit rate, the five-segment gauge and
/// <c>2%/1.0M</c>.</para>
/// </summary>
internal sealed class SessionInstrumentsViewModel : ObservableObject, IDisposable
{
    private readonly SessionInfo session;
    private readonly ISessionInstrumentSources sources;

    /// <summary>Whose agent events this instance reads; "root" for the session's own strip.</summary>
    private readonly string agentId;
    private readonly Func<string> currentModel;
    private readonly Func<string?> currentProvider;

    /// <summary>The window's resource window; null leaves the strip's button disabled rather than a dead entry.</summary>
    private readonly Action? openInspector;
    private int runningResources;
    private bool mcpFailed;
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private TokenUsage usage = TokenUsage.Empty;
    private ProviderRateLimit? rateLimit;
    private string? rateLimitProvider;
    private string? cacheTtl;
    private int? contextTokens;
    private int? usableTokens;
    private int? triggerTokens;

    /// <summary>
    /// The line the model reads about its own budget, rebuilt whenever the measurement changes and read from the agent
    /// loop's thread, so it is an immutable string swapped as a whole rather than three fields read one by one.
    /// </summary>
    private volatile string? budgetLine;
    private ContextLevel contextLevel;
    private bool disposed;

    /// <summary>The turns the session already recorded, read once per strip from <c>turn_metrics</c> (else its checkpoints).</summary>
    private SessionTurnHistory turnHistory = SessionTurnHistory.Empty;

    /// <summary>
    /// How many turns this window watched end — every one of them, including a failed or unreported turn, because the
    /// averages are spread over all turns the session ran; added to <see cref="turnHistory"/>.
    /// </summary>
    private int liveTurns;

    /// <summary>Guards the one background turn-history read against a strip that was disposed while it ran.</summary>
    private int turnHistoryToken;

    private bool turnHistoryLoaded;

    /// <summary>The first turn number this window ran; rows at or above it belong to the live count, not the history.</summary>
    private int? windowFirstTurn;

    /// <summary>True when the loaded history cannot contain this window's rows (read before the first turn, or read through the cutoff).</summary>
    private bool turnHistoryClean;

    /// <summary>Guards the one corrective re-read: a history read that raced this window's first turn end is redone with the cutoff.</summary>
    private bool turnHistoryReread;

    /// <summary>
    /// The wall clock of the turns this window watched end. The stored rows of the same turns are excluded from the
    /// history read by the cutoff, so the two sides never double-count - and the summary's duration stops being a number
    /// only a reopened tab could see.
    /// </summary>
    private TimeSpan liveDuration;

    public SessionInstrumentsViewModel(
        SessionInfo session,
        ISessionInstrumentSources sources,
        Func<string> currentModel,
        Func<string?>? currentProvider = null,
        Action? openInspector = null,
        string? agentId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        // The events this instance owns: the root's own id is "root"; a sub-agent tab passes its child's id and reads
        // its own context, quota and spend from the same events, through the same code.
        this.agentId = agentId ?? "root";
        this.sources = sources ?? EmptySessionInstrumentSources.Instance;
        this.currentModel = currentModel ?? (static () => string.Empty);
        this.currentProvider = currentProvider ?? (static () => null);
        this.openInspector = openInspector;

        OpenInspectorCommand = new RelayCommand(() => this.openInspector?.Invoke(), () => CanOpenInspector);

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        this.sources.McpStateChanged += OnMcpStateChanged;
        mcpFailed = this.sources.McpFailed();
        RaiseQuota();
        SeedCacheTtl();
        RefreshTurnHistory();
    }

    // ---------------------------------------------------------------- usage

    /// <summary>
    /// The usage chip's own text: the two token columns and the cache-hit rate, composed from the same formatters the
    /// hover line uses. Always present, zeros included, so the strip's right end keeps its shape before the first turn.
    /// </summary>
    public string UsageChipText =>
        InstrumentsStrings.UsageTokens(Scale(usage.InputTokens), Scale(usage.OutputTokens)) + " · " + CacheReadText;

    public bool HasUsage => usage.InputTokens > 0 || usage.OutputTokens > 0;

    /// <summary>True once the root agent reported its context size (<see cref="NoteContext"/>) since the strip was created.</summary>
    public bool HasContextMeasure => contextTokens is not null;

    /// <summary>True when the context size <em>and</em> the model's usable budget U are both known: the gauge then shows a percentage.</summary>
    public bool HasContextWindow => contextTokens is not null && usableTokens is > 0;

    /// <summary>C / U in percent (0-100, rounded); 0 while either is unknown.</summary>
    public double ContextPercent => ContextRatio is { } ratio ? Math.Round(ratio * 100, 0) : 0;

    /// <summary>Normal, warning (within 10 percentage points of the trigger line) or over (at or past it).</summary>
    public ContextLevel ContextLevel => contextLevel;

    public bool IsContextWarning => contextLevel == ContextLevel.Warning;

    public bool IsContextOver => contextLevel == ContextLevel.Over;

    /// <summary>
    /// The fixed-width gauge next to the readout: five identical slanted segments, the filled ones counting up to the rounded
    /// fraction C / U. Fixed because it is the strip's stable anchor — the numbers beside it change, the gauge does not resize.
    /// </summary>
    public IReadOnlyList<ContextSegment> ContextSegments { get; private set; } = BuildSegments(0);

    /// <summary>"RC 98.5": the cache-read hit rate of the session, one decimal, 0.0 before any usage.</summary>
    public string CacheReadText => InstrumentsStrings.CacheRead(
        (SessionUsageMath.CacheHitRatio(usage) * 100).ToString("0.0", CultureInfo.InvariantCulture));

    // ---------------------------------------------------------------- account windows

    /// <summary>
    /// True when the provider reported the account's usage windows with the last response <em>and</em> the composer still
    /// points at that provider. A window reading belongs to the account the request went to, so switching the picker to a
    /// different provider hides it rather than showing another account's numbers under this session.
    /// </summary>
    public bool HasQuota => rateLimit is { } reading
        && (reading.HasWindows || reading.Throttle is not null)
        && string.Equals(rateLimitProvider, currentProvider(), StringComparison.Ordinal);

    /// <summary>True when the server says the next request would be refused; the reading is then coloured as a failure.</summary>
    public bool IsQuotaRejected => HasQuota && rateLimit!.IsRejected;

    /// <summary>
    /// True when the server itself flags a window as near its limit (<c>allowed_warning</c>) without refusing. The threshold is
    /// the server's, not one this app invents.
    /// </summary>
    public bool IsQuotaWarning => HasQuota && !rateLimit!.IsRejected
        && (rateLimit.FiveHour == RateLimitStatus.AllowedWarning || rateLimit.Weekly == RateLimitStatus.AllowedWarning);

    /// <summary>
    /// "5h 62% · 2h41m 后重置   周 41% · 3d 后重置": the server's own utilization and countdown, one part per window and only
    /// for the windows the response mentioned. Empty when nothing was reported — the segment does not exist then.
    /// </summary>
    public string QuotaLabel => HasQuota ? QuotaLabelFor(rateLimit!, DateTimeOffset.UtcNow) : string.Empty;

    /// <summary>The hover line: one decimal per window, its absolute reset, which window is binding, the overage channel.</summary>
    public string QuotaTooltip => HasQuota ? QuotaTooltipFor(rateLimit!, DateTimeOffset.UtcNow) : string.Empty;

    /// <summary>
    /// Which prompt-cache lifetime the last request asked for (<c>"5m"</c>/<c>"1h"</c>, Core's <c>AgentContextMeasured</c>). Shown as
    /// its own chip because it is a cost decision the user can set, and inferring it from the bill is not a readout.
    /// </summary>
    public bool HasCacheTtl => !string.IsNullOrEmpty(cacheTtl);

    /// <summary>True once a request reported the ttl that actually went out; until then the chip shows the provider's
    /// configuration, which a reopened session knows without waiting for a first request.</summary>
    private bool cacheTtlMeasured;

    /// <summary>"缓存 1h" — the lifetime in force, empty before the first measured request.</summary>
    public string CacheTtlText => HasCacheTtl ? InstrumentsStrings.CacheTtlLabel(cacheTtl!) : string.Empty;

    /// <summary>The hover line: which lifetime, and the one sentence that says what changing it saves.</summary>
    public string CacheTtlTooltip => HasCacheTtl ? InstrumentsStrings.CacheTtlTooltip(cacheTtl!) : string.Empty;

    /// <summary>
    /// The provider's configured lifetime, shown until a request reports the one that really went out. The composer's
    /// provider can change (or arrive after the strip was built), so this re-reads on every counter refresh - the measured
    /// value, once noted, owns the chip and the seed never overwrites it.
    /// </summary>
    private void SeedCacheTtl()
    {
        if (cacheTtlMeasured)
        {
            return;
        }

        // Writes the field without marking it measured: a provider switch before the first request re-reads this, and a
        // provider without a configured lifetime keeps the chip hidden rather than pinning "none" on it.
        SetCacheTtl(sources.PromptCacheTtl(currentProvider()));
    }

    public void NoteCacheTtl(string? ttl)
    {
        // A request went out and reported the ttl it used: from here the chip is measured, and the configuration seed
        // never overwrites it (see SeedCacheTtl).
        cacheTtlMeasured = true;
        SetCacheTtl(ttl);
    }

    private void SetCacheTtl(string? ttl)
    {
        if (string.Equals(cacheTtl, ttl, StringComparison.Ordinal))
        {
            return;
        }

        cacheTtl = string.IsNullOrEmpty(ttl) ? null : ttl;
        OnPropertyChanged(nameof(HasCacheTtl));
        OnPropertyChanged(nameof(CacheTtlText));
        OnPropertyChanged(nameof(CacheTtlTooltip));
    }

    /// <summary>
    /// A response reported the account's windows (Core's <c>AgentRateLimitUpdated</c>). <paramref name="providerId"/> is the
    /// provider the request went to; the reading is dropped when the picker moves away from it.
    /// </summary>
    public void NoteRateLimit(string? providerId, ProviderRateLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        rateLimit = limit;
        rateLimitProvider = providerId;
        RaiseQuota();
    }

    private void RaiseQuota()
    {
        OnPropertyChanged(nameof(HasQuota));
        OnPropertyChanged(nameof(IsQuotaRejected));
        OnPropertyChanged(nameof(IsQuotaWarning));
        OnPropertyChanged(nameof(QuotaLabel));
        OnPropertyChanged(nameof(QuotaTooltip));
    }

    internal static string QuotaLabelFor(ProviderRateLimit limit, DateTimeOffset now)
    {
        // One segment, two meters with different meanings: the subscription's spending windows, or an API key's per-minute rate
        // limit. The label names which one it is, because "62%" and "43/50" do not say it on their own.
        if (!limit.HasWindows)
        {
            return ThrottleLabel(limit.Throttle, now);
        }

        var fiveHour = QuotaPart(InstrumentsStrings.QuotaFiveHour, limit.FiveHourUtilization, limit.FiveHourResetAt, now);
        var weekly = QuotaPart(InstrumentsStrings.QuotaWeekly, limit.WeeklyUtilization, limit.WeeklyResetAt, now);
        var parts = (fiveHour, weekly) switch
        {
            ({ } five, { } week) => InstrumentsStrings.QuotaUsage(five, week),
            ({ } five, null) => five,
            (null, { } week) => week,
            _ => string.Empty,
        };
        return parts.Length == 0 ? string.Empty : InstrumentsStrings.QuotaWindowsPrefix + " " + parts;
    }

    /// <summary>"限流 每分钟请求 43/50 · 12s 后重置" — the API key's ceiling, never confused with a spending window.</summary>
    private static string ThrottleLabel(ProviderThrottle? throttle, DateTimeOffset now)
    {
        if (throttle is null)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (throttle.HasRequests)
        {
            parts.Add(InstrumentsStrings.QuotaThrottleRequests(
                throttle.RequestRemaining?.ToString(CultureInfo.InvariantCulture) ?? "—",
                throttle.RequestLimit?.ToString(CultureInfo.InvariantCulture) ?? "—"));
        }

        if (throttle.HasTokens)
        {
            parts.Add(InstrumentsStrings.QuotaThrottleTokens(
                throttle.TokenRemaining is { } remaining ? Scale(remaining) : "—",
                throttle.TokenLimit is { } limit ? Scale(limit) : "—"));
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var text = InstrumentsStrings.QuotaThrottlePrefix + " " + string.Join(" · ", parts);
        var reset = throttle.RequestResetAt ?? throttle.TokenResetAt;
        return reset is { } at && at > now
            ? text + " · " + InstrumentsStrings.QuotaReset(InstrumentsStrings.QuotaCountdown(at - now))
            : text;
    }

    private static string ThrottleTooltip(ProviderThrottle throttle, DateTimeOffset now)
    {
        var requests = throttle.HasRequests
            ? (throttle.RequestRemaining?.ToString(CultureInfo.InvariantCulture) ?? "—") + "/"
              + (throttle.RequestLimit?.ToString(CultureInfo.InvariantCulture) ?? "—")
            : "—";
        var tokens = throttle.HasTokens
            ? (throttle.TokenRemaining is { } remaining ? Scale(remaining) : "—") + "/"
              + (throttle.TokenLimit is { } limit ? Scale(limit) : "—")
            : "—";
        var reset = throttle.RequestResetAt ?? throttle.TokenResetAt;
        var resetText = reset is { } at
            ? TextFormat.ShortStamp(at) + " (" + InstrumentsStrings.QuotaCountdown(at - now) + ")"
            : InstrumentsStrings.QuotaNoReset;
        return InstrumentsStrings.QuotaThrottleTooltip(requests, tokens, resetText);
    }

    private static string? QuotaPart(string label, double? utilization, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (utilization is null && resetAt is null)
        {
            return null;
        }

        var window = utilization is { } value
            ? InstrumentsStrings.QuotaWindow(label, QuotaPercent(value, "0"))
            : InstrumentsStrings.QuotaWindowUnknown(label);
        return resetAt is { } at && at > now
            ? window + " · " + InstrumentsStrings.QuotaReset(InstrumentsStrings.QuotaCountdown(at - now))
            : window;
    }

    internal static string QuotaTooltipFor(ProviderRateLimit limit, DateTimeOffset now)
    {
        if (!limit.HasWindows && limit.Throttle is { } throttleOnly)
        {
            return ThrottleTooltip(throttleOnly, now);
        }

        var binding = limit.BindingWindow is { } window
            ? InstrumentsStrings.QuotaBinding(InstrumentsStrings.QuotaWindowName(window))
            : InstrumentsStrings.QuotaBindingUnknown;
        return InstrumentsStrings.QuotaTooltip(
            QuotaDetail(limit.FiveHourUtilization, limit.FiveHourResetAt, RateLimitWindow.FiveHour, now),
            QuotaDetail(limit.WeeklyUtilization, limit.WeeklyResetAt, RateLimitWindow.Weekly, now),
            binding,
            InstrumentsStrings.QuotaOverage(InstrumentsStrings.QuotaOverageState(limit.Overage)));
    }

    private static string QuotaDetail(double? utilization, DateTimeOffset? resetAt, RateLimitWindow window, DateTimeOffset now)
    {
        var label = InstrumentsStrings.QuotaWindowName(window);
        var percent = utilization is { } value ? QuotaPercent(value, "0.0") : "—";
        var reset = resetAt is { } at
            ? TextFormat.ShortStamp(at) + " (" + InstrumentsStrings.QuotaCountdown(at - now) + ")"
            : InstrumentsStrings.QuotaNoReset;
        return InstrumentsStrings.QuotaTooltipWindow(label, percent, reset);
    }

    /// <summary>
    /// A 0-1 share as a percent string. Clamped to 100 because the server may report a window slightly past full while overage
    /// is still open, and "103%" reads like a bug rather than a state.
    /// </summary>
    private static string QuotaPercent(double value, string format) =>
        (Math.Clamp(value, 0, 1) * 100).ToString(format, CultureInfo.InvariantCulture);

    private double? ContextRatio => contextTokens is { } used && usableTokens is > 0
        ? Math.Clamp((double)used / usableTokens.Value, 0, 1)
        : null;

    private static ContextSegment[] BuildSegments(double ratio)
    {
        var filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * ContextSegment.Count, MidpointRounding.AwayFromZero);
        var segments = new ContextSegment[ContextSegment.Count];
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = new ContextSegment(index < filled);
        }

        return segments;
    }

    /// <summary>
    /// "62k/170k · 36%" next to the gauge: the current context C over the model's usable input budget U (the window minus
    /// the output reserve, the same budget the trigger line is a share of). With an unknown window it is the token count
    /// alone; before the first request reported a size it reads "0%", so the right end of the strip keeps its shape.
    /// </summary>
    public string ContextLabel
    {
        get
        {
            if (contextTokens is not { } used)
            {
                // Not zero: an unmeasured context is unknown, and a reopened session reads it that way until its next request.
                return TextFormat.None;
            }

            return usableTokens is > 0
                ? InstrumentsStrings.UsageContext(Scale(used), Scale(usableTokens.Value), PercentText())
                : InstrumentsStrings.ContextTokens(Scale(used));
        }
    }

    /// <summary>The gauge's own hover line: the numbers behind it and the trigger line. What a click does belongs to
    /// the host (see InstrumentsStrings' compact/auto hints), not to the reading.</summary>
    public string ContextTooltip
    {
        get
        {
            if (contextTokens is not { } used)
            {
                return InstrumentsStrings.ContextTooltipPending;
            }

            if (usableTokens is not > 0)
            {
                return InstrumentsStrings.ContextTooltipUnknown(Scale(used));
            }

            var trigger = triggerTokens ?? usableTokens.Value;
            var triggerPercent = Math.Round((double)trigger / usableTokens.Value * 100, 0).ToString("0", CultureInfo.InvariantCulture);
            return InstrumentsStrings.ContextTooltip(Scale(used), Scale(usableTokens.Value), PercentText(), Scale(trigger), triggerPercent);
        }
    }

    private string PercentText() => ContextPercent.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The hover line: every number the strip's readout is made of, always all of them and always in the same order, so the
    /// tooltip never collapses to "no usage yet" and never changes shape between a fresh session and a running one. A part the
    /// model cannot report (no window, no price) reads zero.
    /// </summary>
    public string UsageTooltip
    {
        get
        {
            var metadata = Metadata;
            var cost = SessionUsageMath.Cost(usage, metadata.Pricing) ?? 0;
            return string.Join(
                " · ",
                InstrumentsStrings.UsageTokens(Scale(usage.InputTokens), Scale(usage.OutputTokens)),
                InstrumentsStrings.UsageCacheRead(Scale(usage.CacheReadTokens)),
                usage.CacheWrite1hTokens > 0
                    ? InstrumentsStrings.UsageCacheWriteSplit(
                        Scale(usage.CacheWriteTokens),
                        Scale(usage.CacheWriteTokens - usage.CacheWrite1hTokens),
                        Scale(usage.CacheWrite1hTokens))
                    : InstrumentsStrings.UsageCacheWrite(Scale(usage.CacheWriteTokens)),
                InstrumentsStrings.UsageCache(
                    (SessionUsageMath.CacheHitRatio(usage) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"),
                Price(cost),
                // The window is the one model constant rather than a session number, so it sits last.
                InstrumentsStrings.UsageWindow(Scale(metadata.ContextWindow ?? 0)));
        }
    }

    private ModelMetadata Metadata => sources.ModelMetadata(currentModel(), currentProvider());

    /// <summary>
    /// One turn's delta, from the same <see cref="AgentTurnCompleted"/> arithmetic the turn card uses. Only the root agent
    /// counts: the cost is priced with the model the composer's picker shows, and a sub-agent's spend stays on its tab. The
    /// gauge does not move here: a turn's summed input is not a prompt size, <see cref="NoteContext"/> reports that.
    /// </summary>
    public void AddTurn(string agentId, TokenUsage delta)
    {
        if (!OwnsAgent(agentId))
        {
            return;
        }

        usage += delta;
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(CacheReadText));
        OnPropertyChanged(nameof(UsageChipText));
        OnPropertyChanged(nameof(UsageTooltip));
        RaiseUsageSummary();
    }

    /// <summary>
    /// One turn of this window ended — completed, stopped, failed or unreported alike. The denominator of the averages is
    /// every turn the session ran (a free or failed turn still took its place), so this is counted at the turn end and not
    /// when usage arrives: <see cref="AddTurn"/> only sees a turn whose provider reported something.
    /// </summary>
    public void NoteTurnEnded(int turn, TimeSpan? elapsed = null)
    {
        if (disposed)
        {
            return;
        }

        if (windowFirstTurn is null)
        {
            windowFirstTurn = turn;
            MaybeRereadTurnHistory();
        }

        if (elapsed is { } wall && wall > TimeSpan.Zero)
        {
            liveDuration += wall;
        }

        liveTurns++;
        RaiseUsageSummary();
    }

    /// <summary>
    /// A history read that landed after this window's first turn ended may have counted that turn's already-flushed row,
    /// and the live count counts it too. One re-read with the window's first turn number as the cutoff puts every row on
    /// exactly one side of the split; the flag keeps a session that keeps running from re-reading on every turn.
    /// </summary>
    private void MaybeRereadTurnHistory()
    {
        if (turnHistoryLoaded && !turnHistoryClean && !turnHistoryReread)
        {
            turnHistoryReread = true;
            RefreshTurnHistory();
        }
    }

    /// <summary>
    /// The root agent's context after a model request or a compaction (Core's <c>AgentContextMeasured</c>): C, the usable
    /// budget U of its model and the trigger line, the last two null when the window is unknown. A sub-agent's report is
    /// ignored — it has its own model and its own window.
    /// </summary>
    public void NoteContext(string agentId, int contextTokens, int? usableTokens, int? triggerTokens)
    {
        if (!OwnsAgent(agentId))
        {
            return;
        }

        this.usableTokens = usableTokens is > 0 ? usableTokens : null;
        this.triggerTokens = this.usableTokens is null ? null : triggerTokens;
        SetContext(contextTokens);
    }

    /// <summary>
    /// A manual compaction shrank the root context and reported no measurement of its own: the gauge shows the compactor's
    /// figure against the budget it last knew, until the next request reports a real size.
    /// </summary>
    public void NoteCompacted(int contextTokens) => SetContext(contextTokens);

    /// <summary>
    /// The root agent's budget as one line for the model: C against U and the trigger line, or C alone when the window is
    /// unknown. Null before the first request of the session. Deliberately English and fixed in shape: it is model input,
    /// not UI text, and it is appended to every request as a trailing dynamic suffix.
    /// </summary>
    public string? ContextBudgetLine => budgetLine;

    private string? BuildBudgetLine()
    {
        if (contextTokens is not > 0)
        {
            return null;
        }

        return usableTokens is > 0 && triggerTokens is > 0
            ? string.Concat(
                "Context ",
                Scale(contextTokens.Value),
                "/",
                Scale(usableTokens.Value),
                " (",
                PercentText(),
                "%); auto-compaction at ",
                Scale(triggerTokens.Value),
                ".")
            : string.Concat("Context ", Scale(contextTokens.Value), " tokens; window unknown.");
    }

    private void SetContext(int value)
    {
        contextTokens = Math.Max(0, value);
        contextLevel = LevelOf(contextTokens.Value, usableTokens, triggerTokens);
        ContextSegments = BuildSegments(ContextRatio ?? 0);
        budgetLine = BuildBudgetLine();
        OnPropertyChanged(nameof(HasContextMeasure));
        OnPropertyChanged(nameof(HasContextWindow));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextLevel));
        OnPropertyChanged(nameof(IsContextWarning));
        OnPropertyChanged(nameof(IsContextOver));
        OnPropertyChanged(nameof(ContextSegments));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(ContextTooltip));
    }

    /// <summary>
    /// Over at or past the trigger line; warning from 10 percentage points of U below it; normal otherwise, and always normal
    /// when the budget or the line is unknown (nothing to measure against).
    /// </summary>
    internal static ContextLevel LevelOf(int contextTokens, int? usableTokens, int? triggerTokens)
    {
        if (usableTokens is not > 0 || triggerTokens is not > 0)
        {
            return ContextLevel.Normal;
        }

        if (contextTokens >= triggerTokens.Value)
        {
            return ContextLevel.Over;
        }

        return (long)contextTokens * 100 >= (long)triggerTokens.Value * 100 - (long)usableTokens.Value * WarningMarginPercent
            ? ContextLevel.Warning
            : ContextLevel.Normal;
    }

    /// <summary>How far below the trigger line, in percentage points of U, the gauge starts to warn.</summary>
    internal const int WarningMarginPercent = 10;

    private bool OwnsAgent(string id) => string.Equals(id, agentId, StringComparison.Ordinal);

    /// <summary>
    /// The usage a reopened session already spent, summed from its stored messages. The prompt size of its last request is
    /// not recoverable from that sum, so the context bar stays empty until the next request reports one.
    /// </summary>
    public void SeedHistory(TokenUsage total)
    {
        if (total.InputTokens <= 0 && total.OutputTokens <= 0)
        {
            return;
        }

        usage += total;
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageTooltip));
        OnPropertyChanged(nameof(CacheReadText));
        OnPropertyChanged(nameof(UsageChipText));
        RaiseUsageSummary();
    }

// ---------------------------------------------------------------- the session usage summary

    /// <summary>
    /// The session's own totals in one place: what the strip's hover line sums up, plus the two numbers only the session's
    /// stored history knows — how many turns it ran and how long they took. The tokens are the strip's own <c>usage</c> (the
    /// stored message usage seeded when the history was replayed, plus every turn this window watched), so there is one total
    /// and not two that can disagree.
    /// <para>The turn count and the time come from <c>turn_metrics</c> — every recorded turn, a failed one included —
    /// read once off the UI thread when the strip is built; a database without that table falls back to
    /// <c>turn_checkpoints</c>, and a session that recorded neither only counts the turns this window watched.</para>
    /// </summary>
    public bool HasUsageSummary => usage.InputTokens > 0 || usage.OutputTokens > 0 || UsageTurnCount > 0;

    /// <summary>
    /// The rows of the summary popover, in the order it draws them. The list is rebuilt on every read, so a language switch
    /// and a finished turn both come through the same one notification.
    /// </summary>
    public IReadOnlyList<UsageStatRow> UsageSummaryRows =>
    [
        new(UsageStrings.InputLabel, UsageInputText),
        new(UsageStrings.OutputLabel, UsageOutputText),
        new(UsageStrings.CacheReadLabel, UsageCacheReadText),
        new(UsageStrings.CacheWriteLabel, UsageCacheWriteText),
        new(UsageStrings.CacheHitLabel, UsageCacheHitText),
        new(UsageStrings.CostLabel, UsageCostText),
        // The one model constant the hover carries ("C …"), so the popover answers everything the hover does.
        new(UsageStrings.WindowLabel, UsageWindowText),
        new(UsageStrings.TurnsLabel, UsageTurnsText),
        new(UsageStrings.DurationLabel, UsageDurationText),
        new(UsageStrings.AverageInputLabel, UsageAverageInputText),
        new(UsageStrings.AverageOutputLabel, UsageAverageOutputText),
        new(UsageStrings.AverageCostLabel, UsageAverageCostText),
    ];

    public string UsageSummaryTitle => UsageStrings.Title;

    public string UsageSummaryTooltip => UsageStrings.Tooltip;

    /// <summary>Where the numbers come from, and which of them a session without checkpoints cannot report.</summary>
    public string UsageSummaryHint => UsageStrings.HintText;

    /// <summary>True when the session has spent nothing yet: the popover then says so instead of drawing zeros.</summary>
    public bool IsUsageSummaryEmpty => !HasUsageSummary;

    public string UsageSummaryEmptyText => UsageStrings.EmptyText;

    public string UsageInputText => Scale(usage.InputTokens);

    public string UsageOutputText => Scale(usage.OutputTokens);

    public string UsageCacheReadText => Scale(usage.CacheReadTokens);

    /// <summary>Carries the hour-lived share when there is one, the way the hover line splits it: the hour is priced at
    /// twice the input rate, and a sum without the split would hide why a pause made the next write dearer.</summary>
    public string UsageCacheWriteText => usage.CacheWrite1hTokens > 0
        ? UsageStrings.CacheWriteSplitText(
            Scale(usage.CacheWriteTokens),
            Scale(usage.CacheWriteTokens - usage.CacheWrite1hTokens),
            Scale(usage.CacheWrite1hTokens))
        : Scale(usage.CacheWriteTokens);

    /// <summary>The model's context window, the one constant among the session's numbers; "—" when the model is unknown.</summary>
    public string UsageWindowText =>
        Metadata.ContextWindow is { } window ? Scale(window) : UsageStrings.UnknownText;

    /// <summary>"98.5%": the share of the input that came from the prompt cache, one decimal.</summary>
    public string UsageCacheHitText => Ratio(SessionUsageMath.CacheHitRatio(usage));

    /// <summary>The session's spend at the selected model's prices; "—" while the model has no price.</summary>
    public string UsageCostText =>
        SessionUsageMath.Cost(usage, Metadata.Pricing) is { } cost ? Price(cost) : UsageStrings.UnknownText;

    /// <summary>
    /// "5 已记录 · 2 本次": the turns of the stored history (checkpoints) and the turns this window watched complete, or the
    /// one number a session that only has one of them can show.
    /// </summary>
    public string UsageTurnsText => turnHistory.Turns == 0
        ? Turns(liveTurns)
        : liveTurns == 0
            ? Turns(turnHistory.Turns)
            : UsageStrings.TurnsValueText(Turns(turnHistory.Turns), Turns(liveTurns));

    /// <summary>"4.2 秒" / "3 分 05 秒": the wall clock of the recorded turns plus the ones this window watched end;
    /// "—" when neither side has a second.</summary>
    public string UsageDurationText
    {
        get
        {
            var total = turnHistory.Duration + liveDuration;
            return total > TimeSpan.Zero ? TextFormat.Elapsed(total) : UsageStrings.UnknownText;
        }
    }

    /// <summary>The turns the totals are divided by: the stored history plus the ones this window watched end, failures included.</summary>
    public int UsageTurnCount => turnHistory.Turns + liveTurns;

    public string UsageAverageInputText => Average(usage.InputTokens);

    public string UsageAverageOutputText => Average(usage.OutputTokens);

    public string UsageAverageCostText =>
        SessionUsageMath.Cost(usage, Metadata.Pricing) is { } cost && UsageTurnCount > 0
            ? Price(cost / UsageTurnCount)
            : UsageStrings.UnknownText;

    /// <summary>True once the session's recorded turns are known (or the read came back empty).</summary>
    public bool HasTurnHistory => turnHistoryLoaded;

    /// <summary>Reads the session's recorded turns off the UI thread; called once when the strip is built.</summary>
    public void RefreshTurnHistory()
    {
        if (disposed)
        {
            return;
        }

        var token = ++turnHistoryToken;
        _ = LoadTurnHistoryAsync(token);
    }

    private async Task LoadTurnHistoryAsync(int token)
    {
        // The cutoff is read before the await: a row this window already watched land must stay out of the history
        // count, because the live count already has it.
        var cutoff = windowFirstTurn;
        SessionTurnHistory history;
        try
        {
            history = await sources.TurnHistoryAsync(session.Id, CancellationToken.None, cutoff).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            // The summary is a readout: a store that cannot be read leaves the recorded turns unknown instead of failing.
            history = SessionTurnHistory.Empty;
        }

        if (disposed || token != turnHistoryToken)
        {
            return;
        }

        turnHistory = history;
        turnHistoryLoaded = true;
        turnHistoryClean = cutoff is not null || windowFirstTurn is null;
        OnPropertyChanged(nameof(HasTurnHistory));
        RaiseUsageSummary();

        // This read may have started before the first turn of this window ended and finished after its row landed, so the
        // same turn can sit in both numbers. One re-read with the cutoff settles which side of the line it is on.
        MaybeRereadTurnHistory();
    }

    /// <summary>One count of the turn column; the history and the live count are both plain counts.</summary>
    private static string Turns(int turns) => turns.ToString(CultureInfo.InvariantCulture);

    /// <summary>One share as a percentage with one decimal: the cache-hit rule of the strip's <c>RC</c> reading.</summary>
    private static string Ratio(double value) =>
        (Math.Clamp(value, 0, 1) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>One token total divided by the turns it took, scaled the way the totals are; no turns reads "—".</summary>
    private string Average(int total) =>
        UsageTurnCount > 0 ? Scale(total / UsageTurnCount) : UsageStrings.UnknownText;

    private void RaiseUsageSummary()
    {
        OnPropertyChanged(nameof(HasUsageSummary));
        OnPropertyChanged(nameof(IsUsageSummaryEmpty));
        OnPropertyChanged(nameof(UsageSummaryTitle));
        OnPropertyChanged(nameof(UsageSummaryTooltip));
        OnPropertyChanged(nameof(UsageSummaryHint));
        OnPropertyChanged(nameof(UsageSummaryRows));
        OnPropertyChanged(nameof(UsageInputText));
        OnPropertyChanged(nameof(UsageOutputText));
        OnPropertyChanged(nameof(UsageCacheReadText));
        OnPropertyChanged(nameof(UsageCacheWriteText));
        OnPropertyChanged(nameof(UsageCacheHitText));
        OnPropertyChanged(nameof(UsageCostText));
        OnPropertyChanged(nameof(UsageTurnsText));
        OnPropertyChanged(nameof(UsageDurationText));
        OnPropertyChanged(nameof(UsageTurnCount));
        OnPropertyChanged(nameof(UsageAverageInputText));
        OnPropertyChanged(nameof(UsageAverageOutputText));
        OnPropertyChanged(nameof(UsageAverageCostText));
    }    // ---------------------------------------------------------------- the resource button

    /// <summary>The resource button's label; the window it opens has the detail.</summary>
    public string ResourcesLabel => InstrumentsStrings.Resources;

    /// <summary>The button's tooltip: how many automation agents run, or that a server failed, otherwise what the window is.</summary>
    public string ResourcesTooltip => runningResources > 0
        ? ResourceInspectorStrings.ButtonRunningTooltip(runningResources)
        : mcpFailed
            ? ResourceInspectorStrings.ButtonFailedTooltip
            : ResourceInspectorStrings.ButtonTooltip;

    /// <summary>False only when no window wired the overlay (a standalone view); the button is then disabled.</summary>
    public bool CanOpenInspector => openInspector is not null;

    /// <summary>Opens the resource window for this session.</summary>
    public RelayCommand OpenInspectorCommand { get; }

    /// <summary>Automation agents of this session that are queued or running; the number beside the green dot.</summary>
    public int RunningResources => runningResources;

    /// <summary>The green dot and its number: at least one automation agent runs.</summary>
    public bool HasRunningResources => runningResources > 0;

    /// <summary>The dot is red instead of green: an MCP server failed to connect (a running agent's count stays beside it).</summary>
    public bool HasResourceProblem => mcpFailed;

    /// <summary>The button carries a dot at all: something runs, or a server failed.</summary>
    public bool ShowResourceDot => runningResources > 0 || mcpFailed;

    /// <summary>
    /// Records how many automation agents run right now; the session pushes it whenever that set changes. Equal counts are
    /// ignored, so a redundant push costs nothing.
    /// </summary>
    public void NoteRunningResources(int running)
    {
        var next = Math.Max(0, running);
        if (next == runningResources)
        {
            return;
        }

        runningResources = next;
        RaiseResourceButton();
    }

    /// <summary>Re-reads the one thing the strip needs besides the pushed count: the composer's provider can have changed.</summary>
    public void RefreshCounters()
    {
        if (disposed)
        {
            return;
        }

        // The composer's provider can have changed since the last request; a reading from another account stops showing here.
        RaiseQuota();
        // So can the cache lifetime's configuration, until a request reports the ttl that actually went out.
        SeedCacheTtl();
    }

    /// <summary>An MCP server changed state, possibly on a pool thread; the dot is only read on the UI thread.</summary>
    private void OnMcpStateChanged()
    {
        if (disposed)
        {
            return;
        }

        if (Environment.CurrentManagedThreadId == uiThread)
        {
            ReadMcpFailed();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(ReadMcpFailed);
    }

    private void ReadMcpFailed()
    {
        if (disposed)
        {
            return;
        }

        var failed = sources.McpFailed();
        if (failed == mcpFailed)
        {
            return;
        }

        mcpFailed = failed;
        RaiseResourceButton();
    }

    private void RaiseResourceButton()
    {
        OnPropertyChanged(nameof(RunningResources));
        OnPropertyChanged(nameof(HasRunningResources));
        OnPropertyChanged(nameof(HasResourceProblem));
        OnPropertyChanged(nameof(ShowResourceDot));
        OnPropertyChanged(nameof(ResourcesTooltip));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        sources.McpStateChanged -= OnMcpStateChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone; re-reading here would touch
        // bound controls off their thread.
        if (Environment.CurrentManagedThreadId != uiThread || disposed)
        {
            return;
        }

        RaiseUsageSummary();
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(ContextTooltip));
        OnPropertyChanged(nameof(UsageTooltip));
        RaiseQuota();
        OnPropertyChanged(nameof(ResourcesLabel));
        OnPropertyChanged(nameof(ResourcesTooltip));
    }

    /// <summary>The one token scaler (<c>900</c>, <c>36k</c>, <c>1.0M</c>), shared by the strip's readings and the
    /// sub-agent tab's own readout so the two can never drift apart.</summary>
    internal static string Scale(int value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>A cost that stays readable at both ends: <c>$0.0041</c>, <c>$0.041</c>, <c>$1.23</c>.</summary>
    private static string Price(double value) => value switch
    {
        >= 10 => "$" + value.ToString("0.0", CultureInfo.InvariantCulture),
        >= 1 => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        >= 0.01 => "$" + value.ToString("0.000", CultureInfo.InvariantCulture),
        _ => "$" + value.ToString("0.0000", CultureInfo.InvariantCulture),
    };
}
