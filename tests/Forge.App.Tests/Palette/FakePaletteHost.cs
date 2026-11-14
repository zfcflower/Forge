using Forge.App.Services;
using Forge.IO;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The palette's shell, replaced by a recorder: the snapshots it hands back, the answers (or failures) its refreshes and
/// its content search give, a gate per query so a test can hold an answer in flight, and a record of every
/// <see cref="ActivateAsync"/> call. Nothing here touches the file system, a language server or a window.
/// <para>Cancellation is counted rather than inferred: every call registers on the token it was given, so a test asserts
/// that the palette really cancelled the work it said it would instead of only observing that no answer landed.</para>
/// </summary>
internal sealed class FakePaletteHost : IPaletteHost
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, TaskCompletionSource<GrepResult>> heldSearches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrepResult> searchAnswers = new(StringComparer.Ordinal);
    private readonly List<SearchRecord> searches = [];
    private readonly List<ActivationRecord> activations = [];
    private TaskCompletionSource<IReadOnlyList<string>>? heldFilesRefresh;
    private int filesRefreshes;
    private int sessionsRefreshes;
    private int symbolsRefreshes;
    private int filesCancellations;
    private int searchCancellations;

    public string? Workspace { get; set; } = "D:/forge/demo";

    public string WorkspaceLabel { get; set; } = "demo";

    public IReadOnlyList<PaletteCommand> Commands { get; set; } = PaletteCommandCatalog.All;

    // ---- cached snapshots: what the palette shows before a refresh answers ----

    public IReadOnlyList<string> CachedFiles { get; set; } = [];

    public IReadOnlyList<PaletteSessionSnapshot> CachedSessions { get; set; } = [];

    public PaletteSymbolSnapshot CachedSymbols { get; set; } = PaletteSymbolSnapshot.Empty;

    // ---- refresh and search answers ----

    public IReadOnlyList<string> FilesAnswer { get; set; } = [];

    public IReadOnlyList<PaletteSessionSnapshot> SessionsAnswer { get; set; } = [];

    public PaletteSymbolSnapshot SymbolsAnswer { get; set; } = PaletteSymbolSnapshot.Empty;

    public Exception? FilesFailure { get; set; }

    public Exception? SessionsFailure { get; set; }

    public Exception? SymbolsFailure { get; set; }

    /// <summary>Thrown by every content search instead of answering; null means the search answers normally.</summary>
    public Exception? SearchFailure { get; set; }

    // ---- recorded calls ----

    public int FilesRefreshes => Volatile.Read(ref filesRefreshes);

    public int SessionsRefreshes => Volatile.Read(ref sessionsRefreshes);

    public int SymbolsRefreshes => Volatile.Read(ref symbolsRefreshes);

    public int FilesCancellations => Volatile.Read(ref filesCancellations);

    public int SearchCancellations => Volatile.Read(ref searchCancellations);

    /// <summary>Every content search the palette requested, in order, with whether its token has been cancelled.</summary>
    public IReadOnlyList<SearchRecord> Searches
    {
        get
        {
            lock (gate)
            {
                return [.. searches];
            }
        }
    }

    /// <summary>Every activation the palette asked for, in order, with whether the palette was still open at that moment.</summary>
    public IReadOnlyList<ActivationRecord> Activations
    {
        get
        {
            lock (gate)
            {
                return [.. activations];
            }
        }
    }

    /// <summary>Asked by <see cref="ActivateAsync"/> so a test can pin that the palette closes before it activates.</summary>
    public Func<bool>? ObserveOpen { get; set; }

    /// <summary>Holds the file refresh in flight, so the cached list is the only rows a test can see.</summary>
    public void HoldFilesRefresh()
    {
        lock (gate)
        {
            heldFilesRefresh = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Answers the held file refresh, so a test can see the rows of the answer replace the cached ones.</summary>
    public void ReleaseFilesRefresh(IReadOnlyList<string> files)
    {
        TaskCompletionSource<IReadOnlyList<string>>? held;
        lock (gate)
        {
            held = heldFilesRefresh;
            heldFilesRefresh = null;
        }

        held?.SetResult(files);
    }

    /// <summary>Holds the content search of <paramref name="query"/> until <see cref="ReleaseSearch"/> is called.</summary>
    public void HoldSearch(string query)
    {
        lock (gate)
        {
            heldSearches[query] = new TaskCompletionSource<GrepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleaseSearch(string query, GrepResult result)
    {
        TaskCompletionSource<GrepResult>? held;
        lock (gate)
        {
            heldSearches.Remove(query, out held);
        }

        held?.SetResult(result);
    }

    /// <summary>The answer <paramref name="query"/> gets; a query without one answers <see cref="GrepResult.Empty"/>.</summary>
    public void Answer(string query, GrepResult result)
    {
        lock (gate)
        {
            searchAnswers[query] = result;
        }
    }

    public async Task<IReadOnlyList<string>> RefreshFilesAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref filesRefreshes);
        using var registration = ct.Register(() => Interlocked.Increment(ref filesCancellations));
        if (FilesFailure is { } failure)
        {
            throw failure;
        }

        TaskCompletionSource<IReadOnlyList<string>>? held;
        lock (gate)
        {
            held = heldFilesRefresh;
        }

        if (held is not null)
        {
            return await held.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        await Task.Yield();
        return FilesAnswer;
    }

    public async Task<IReadOnlyList<PaletteSessionSnapshot>> RefreshSessionsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref sessionsRefreshes);
        if (SessionsFailure is { } failure)
        {
            throw failure;
        }

        await Task.Yield();
        return SessionsAnswer;
    }

    public async Task<PaletteSymbolSnapshot> RefreshSymbolsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref symbolsRefreshes);
        if (SymbolsFailure is { } failure)
        {
            throw failure;
        }

        await Task.Yield();
        return SymbolsAnswer;
    }

    public async Task<GrepResult> SearchContentAsync(string query, int limit, CancellationToken ct)
    {
        TaskCompletionSource<GrepResult>? held;
        GrepResult answer;
        lock (gate)
        {
            searches.Add(new SearchRecord(query, limit, ct));
            heldSearches.TryGetValue(query, out held);
            if (!searchAnswers.TryGetValue(query, out answer!))
            {
                answer = GrepResult.Empty;
            }
        }

        ct.Register(() => Interlocked.Increment(ref searchCancellations));
        if (SearchFailure is { } failure)
        {
            throw failure;
        }

        if (held is not null)
        {
            return await held.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        await Task.Yield();
        return answer;
    }

    /// <summary>Thrown by <see cref="ActivateAsync"/> after recording the call; null means the activation succeeds.</summary>
    public Exception? ActivationFailure { get; set; }

    public async Task ActivateAsync(PaletteItem item, CancellationToken ct)
    {
        var open = ObserveOpen?.Invoke() ?? false;
        lock (gate)
        {
            activations.Add(new ActivationRecord(item, open));
        }

        if (ActivationFailure is { } failure)
        {
            await Task.Yield();
            throw failure;
        }
    }

    /// <summary>One content search as the palette requested it, with the token it handed over.</summary>
    internal sealed class SearchRecord
    {
        private readonly CancellationToken token;

        public SearchRecord(string query, int limit, CancellationToken token)
        {
            Query = query;
            Limit = limit;
            this.token = token;
        }

        public string Query { get; }

        /// <summary>The match budget the palette asked the engine for.</summary>
        public int Limit { get; }

        public bool IsCancelled => token.IsCancellationRequested;
    }

    /// <summary>One activation: the row and whether the palette was still open when the host was called.</summary>
    internal sealed record ActivationRecord(PaletteItem Item, bool WasOpen);
}
