using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Logging;
using Forge.Core.Storage;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// Everything a session reads from the app besides its own state: the app-wide MCP health the strip shows on the resource
/// button, the model metadata chain, the two ways to open the session's project folder, the recorded turns the usage summary
/// counts, and the writer the turn end hands its metrics to.
/// <para>The shell implements it once per window; the async members come back on the caller's context.</para>
/// </summary>
internal interface ISessionInstrumentSources
{
    /// <summary>True while at least one enabled MCP server is in the failed state: the resource button's dot turns red.</summary>
    bool McpFailed();

    /// <summary>
    /// An MCP server changed state or its tool list changed. Raised on any thread; a subscriber that touches UI state posts
    /// to the UI thread itself.
    /// </summary>
    event Action? McpStateChanged;

    /// <summary>What the metadata chain (local table → provider → user override) knows about one model.</summary>
    /// <summary>What is known about the model the composer shows, in the context of the provider that serves it.</summary>
    ModelMetadata ModelMetadata(string modelId, string? providerId = null);

    /// <summary>
    /// The cache lifetime this provider is configured for (auto/5m/1h), null when it has none: the value a freshly opened
    /// session shows before its first request resolves the policy into a concrete ttl. Config, not measurement - the
    /// measured value still arrives with the context event and takes over.
    /// </summary>
    string? PromptCacheTtl(string? providerId);

    /// <summary>
    /// The main turns a session already recorded: how many it ran and how long they took, summed for the usage summary.
    /// The source is <c>turn_metrics</c> — every turn, a failed or unreported one included — and a database without that
    /// table falls back to <c>turn_checkpoints</c>. Runs off the UI thread; a session that recorded nothing (a scratch
    /// session) is empty rather than a failure.
    /// </summary>
    Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null);

    /// <summary>
    /// The app's per-turn metrics writer, which the turn end hands one row through. Null (a standalone view, or a host that
    /// built no recorder) records nothing; nothing on this interface reads from it.
    /// </summary>
    TurnMetricsRecorder? TurnMetricsRecorder { get; }

    /// <summary>
    /// The session's turn-metrics store, when the app has one: the transcript replays a stored conversation's per-turn
    /// boundaries (elapsed time, tool count, outcome) from it — live turns never read it. Null in a standalone view.
    /// </summary>
    ITurnMetricsStore? TurnMetrics { get; }

    /// <summary>
    /// The session's turn ledger, when the app has one: the two observation windows read it (the reply card's turn entry
    /// and the session ledger dialog), and the transcript replay pairs its rows with the replayed rounds. Null in a
    /// standalone view; the managed history is rendered from the same rows by the loop.
    /// </summary>
    ITurnLedgerStore? TurnLedger { get; }
}

/// <summary>
/// The turns a session already recorded before this window opened it: the count and the wall clock summed over the turns
/// that recorded one. The token totals are not here — they come from the session's stored message usage, which the strip
/// already seeds itself with when the history is replayed.
/// </summary>
internal readonly record struct SessionTurnHistory(int Turns, TimeSpan Duration)
{
    /// <summary>Nothing recorded: not an error, just a session nobody has a recorded turn for (a scratch session).</summary>
    public static SessionTurnHistory Empty => default;

    public bool IsEmpty => Turns == 0 && Duration <= TimeSpan.Zero;

    /// <summary>
    /// Sums a session's turn metrics: one turn per row (a failed turn has a row too and counts, because it took its place in
    /// the average) and the wall clock of every row that reports an end after its start.
    /// </summary>
    public static SessionTurnHistory From(IReadOnlyList<TurnMetric> turns, int? maxTurnExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var count = 0;
        var duration = TimeSpan.Zero;
        foreach (var turn in turns)
        {
            if (maxTurnExclusive is { } limit && turn.Turn >= limit)
            {
                // A row this window already watched land: it belongs to the live count, not the history. The corrective
                // re-read passes the window's first turn number so a late read cannot double-count it.
                continue;
            }

            count++;
            if (turn.EndedAt > turn.StartedAt)
            {
                duration += turn.EndedAt - turn.StartedAt;
            }
        }

        return new SessionTurnHistory(count, duration);
    }

    /// <summary>
    /// Sums a session's checkpoints, the fallback for a database without the <c>turn_metrics</c> table: one turn each, and
    /// the wall clock of every turn that has an end. A turn that began and never completed (a crash) counts as a turn but
    /// contributes no time, because it has no duration to report.
    /// </summary>
    public static SessionTurnHistory From(IReadOnlyList<TurnCheckpoint> checkpoints, int? maxTurnExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        var turns = 0;
        var duration = TimeSpan.Zero;
        foreach (var checkpoint in checkpoints)
        {
            if (maxTurnExclusive is { } limit && checkpoint.Turn >= limit)
            {
                // Same split as the metrics overload: a row this window watched land stays on the live side.
                continue;
            }

            turns++;
            if (checkpoint.EndedAt is { } ended && ended > checkpoint.StartedAt)
            {
                duration += ended - checkpoint.StartedAt;
            }
        }

        return new SessionTurnHistory(turns, duration);
    }
}

/// <summary>
/// Reads the turns a session already recorded — <c>turn_metrics</c> when the database has that table, the turn checkpoints
/// when it does not (a database from before the feature, or one whose schema is older than the metrics table). The one
/// existence probe is cached for the life of the reader, so a session that records nothing still costs one PRAGMA per app run.
/// </summary>
/// <remarks>
/// This is the only read path of the feature, it runs when a strip is built (once per opened session) and it stays off the
/// UI thread; nothing here is polled.
/// </remarks>
internal sealed class TurnHistoryReader
{
    private readonly ITurnMetricsStore? metrics;
    private readonly ITurnCheckpointStore checkpoints;

    /// <summary>0 unknown, 1 the table exists, 2 it does not; a failed probe leaves it unknown so a later read retries.</summary>
    private int table;

    public TurnHistoryReader(ITurnMetricsStore? metrics, ITurnCheckpointStore checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        this.metrics = metrics;
        this.checkpoints = checkpoints;
    }

    /// <summary>True once the existence of the metrics table has been answered (by either answer); the strip does not read it.</summary>
    internal bool MetricsTableKnown => Volatile.Read(ref table) != 0;

    public async Task<SessionTurnHistory> ReadAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (metrics is { } store && await HasTableAsync(store, ct).ConfigureAwait(false))
        {
            return SessionTurnHistory.From(await store.ListAsync(sessionId, ct).ConfigureAwait(false), maxTurnExclusive);
        }

        return SessionTurnHistory.From(await checkpoints.ListAsync(sessionId, ct).ConfigureAwait(false), maxTurnExclusive);
    }

    private async Task<bool> HasTableAsync(ITurnMetricsStore store, CancellationToken ct)
    {
        var known = Volatile.Read(ref table);
        if (known != 0)
        {
            return known == 1;
        }

        var present = await store.ExistsAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref table, present ? 1 : 2);
        return present;
    }
}

/// <summary>The shell's implementation: composition services and the platform launchers.</summary>
internal sealed class SessionInstrumentSources : ISessionInstrumentSources
{
    private readonly AppComposition composition;
    private readonly TurnHistoryReader history;
    private readonly Lazy<string?> vsCode = new(ResolveVsCode, LazyThreadSafetyMode.PublicationOnly);

    public SessionInstrumentSources(AppComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        this.composition = composition;
        history = new TurnHistoryReader(composition.TurnMetrics, composition.TurnCheckpoints);
    }

    public bool HasVsCode => vsCode.Value is { Length: > 0 };

    public TurnMetricsRecorder? TurnMetricsRecorder => composition.MetricsRecorder;

    /// <summary>Composition owns the store; a standalone view has none, and replay then simply shows "—" for the values
    /// only the metrics table knows.</summary>
    public ITurnMetricsStore? TurnMetrics => composition.TurnMetrics;

    /// <summary>Composition owns the store; a standalone view has none, and the observation windows then stay hidden.</summary>
    public ITurnLedgerStore? TurnLedger => composition.TurnLedger;

    public string? PromptCacheTtl(string? providerId) => composition.Providers.Catalog.Providers
        .FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
        ?.Definition.PromptCacheTtl;

    public bool McpFailed()
    {
        if (composition.Mcp is not { } mcp)
        {
            return false;
        }

        foreach (var state in mcp.States)
        {
            if (state is { Enabled: true, Status: Forge.Mcp.McpServerStatus.Failed })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Forwards the manager's state and tool-list events; hooked only while someone listens.</summary>
    public event Action? McpStateChanged
    {
        add
        {
            lock (changedGate)
            {
                if (changed is null && composition.Mcp is { } mcp)
                {
                    mcp.Manager.StateChanged += OnState;
                    mcp.Manager.ToolsChanged += OnTools;
                }

                changed += value;
            }
        }
        remove
        {
            lock (changedGate)
            {
                changed -= value;
                if (changed is null && composition.Mcp is { } mcp)
                {
                    mcp.Manager.StateChanged -= OnState;
                    mcp.Manager.ToolsChanged -= OnTools;
                }
            }
        }
    }

    private readonly object changedGate = new();
    private Action? changed;
    private int flushQueued;

    // Every open session's strip listens, and a flapping server raises a burst: one UI-thread pass fans the burst out to all
    // strips instead of one post per strip per event.
    private void OnState(Forge.Mcp.McpServerState state) => QueueFlush();

    private void OnTools() => QueueFlush();

    private void QueueFlush()
    {
        if (Interlocked.Exchange(ref flushQueued, 1) == 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Flush, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void Flush()
    {
        Volatile.Write(ref flushQueued, 0);
        Action? listeners;
        lock (changedGate)
        {
            listeners = changed;
        }

        listeners?.Invoke();
    }

    public ModelMetadata ModelMetadata(string modelId, string? providerId = null) =>
        composition.Providers.Catalog.MetadataFor(modelId, providerId);

    public void OpenInVsCode(string path)
    {
        if (vsCode.Value is not { Length: > 0 } executable || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TryStart(new ProcessStartInfo
        {
            FileName = executable,
            ArgumentList = { "-n", path },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>
    /// Reads the session's recorded turns and sums them. A store that cannot be read is an empty history, not an error:
    /// the summary then reports what this window watched instead of failing to open.
    /// </summary>
    public async Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return SessionTurnHistory.Empty;
        }

        try
        {
            return await history.ReadAsync(sessionId, ct, maxTurnExclusive).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Instruments.ZLogWarning($"turn history unreadable for {sessionId}: {exception.Message}");
            return SessionTurnHistory.Empty;
        }
    }

    private static void TryStart(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            AppLogs.Instruments.ZLogWarning($"launch failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the VS Code command-line shim. On Windows that is <c>code.cmd</c> in the install's <c>bin</c> or on PATH; on
    /// the other platforms the <c>code</c> script. Nothing is offered when it cannot be resolved.
    /// </summary>
    private static string? ResolveVsCode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "code.cmd", "code.exe" } : new[] { "code" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length == 0)
        {
            return null;
        }

        var installed = Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd");
        return File.Exists(installed) ? installed : null;
    }
}

/// <summary>
/// The null object: a session opened without the instrument wiring still has a strip, whose counters
/// read zero and whose buttons do nothing, instead of a broken binding.
/// </summary>
internal sealed class EmptySessionInstrumentSources : ISessionInstrumentSources
{
    public static EmptySessionInstrumentSources Instance { get; } = new();

    public bool McpFailed() => false;

    public event Action? McpStateChanged
    {
        add { }
        remove { }
    }

    public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => Abstractions.ModelMetadata.Unknown;

    public string? PromptCacheTtl(string? providerId) => null;

    public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null) =>
        Task.FromResult(SessionTurnHistory.Empty);

    /// <summary>Nothing is recorded without the app's recorder: a standalone view writes no metrics row at all.</summary>
    public TurnMetricsRecorder? TurnMetricsRecorder => null;

    public ITurnMetricsStore? TurnMetrics => null;

    public ITurnLedgerStore? TurnLedger => null;
}
