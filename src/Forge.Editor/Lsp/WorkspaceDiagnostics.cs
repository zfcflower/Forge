// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.Languages;
using Forge.Editor.Text;

namespace Forge.Editor.Lsp;

/// <summary>One diagnostic of one document, resolved to a file path so a list can show and open it.</summary>
public sealed record WorkspaceDiagnostic(
    string Path,
    string Workspace,
    PositionEncoding Encoding,
    DiagnosticSeverity Severity,
    LspRange Range,
    string Message,
    string? Code,
    string? Source)
{
    /// <summary>1-based line, for display; the position encoding stays the session's business.</summary>
    public int Line => Range.Start.Line + 1;

    public int Column => Range.Start.Character + 1;
}

/// <summary>
/// A source of the workspace's current diagnostics: one change event plus the flattened list. The panel reads through this
/// instead of the aggregator so its filtering, sorting and truncation can be exercised without a language server process.
/// </summary>
public interface IWorkspaceDiagnosticSource
{
    event EventHandler? Changed;

    IReadOnlyList<WorkspaceDiagnostic> Snapshot();
}

/// <summary>
/// Every diagnostic the pool currently holds, from every language session of every workspace, as one list.
/// <para>The pool owns the sessions and their stores; this type only subscribes to what they publish and re-raises one
/// change notification, so a panel has one event to watch instead of one per session. Reading is lock-free
/// (<see cref="DiagnosticsStore"/> snapshots are immutable) and the flattening happens where the caller asks for it, so
/// there is no background timer and no work at all while nothing changes.</para>
/// </summary>
public sealed class WorkspaceDiagnostics : IWorkspaceDiagnosticSource, IDisposable
{
    private readonly LanguageSessionPool pool;
    private readonly object gate = new();
    private readonly Dictionary<LanguageSession, EventHandler<DiagnosticsChangedEventArgs>> subscriptions = [];
    private int disposed;

    public WorkspaceDiagnostics(LanguageSessionPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        this.pool = pool;
        pool.SessionStarted += OnSessionStarted;
        pool.SessionClosed += OnSessionClosed;

        // Sessions that were already running before this aggregator existed are subscribed here, so a panel opened later
        // still sees their diagnostics.
        Reconcile();
    }

    /// <summary>Some document's diagnostics changed, or a session appeared or went away.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The flattened list: severity, then path, then line. Dialogs of a document whose session retired are simply gone, so
    /// the list never shows a file the pool no longer serves.
    /// </summary>
    public IReadOnlyList<WorkspaceDiagnostic> Snapshot()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return [];
        }

        Reconcile();
        var sessions = pool.Sessions;
        List<WorkspaceDiagnostic> items = [];
        foreach (var session in sessions)
        {
            if (session.State == LanguageSessionState.Disposed)
            {
                // The pool only drops a disposed session when something looks it up again; its store would otherwise keep the
                // last diagnostics it ever received on the list as if they were current.
                continue;
            }

            var workspace = session.WorkspacePath;
            var encoding = session.PositionEncoding;
            foreach (var published in session.Diagnostics.Snapshot())
            {
                if (LspUri.ToPath(published.Uri) is not { Length: > 0 } path)
                {
                    continue;
                }

                foreach (var diagnostic in published.Diagnostics)
                {
                    items.Add(new WorkspaceDiagnostic(
                        path,
                        workspace,
                        encoding,
                        diagnostic.Severity,
                        diagnostic.Range,
                        diagnostic.Message,
                        diagnostic.Code,
                        diagnostic.Source));
                }
            }
        }

        items.Sort(static (left, right) =>
        {
            var bySeverity = left.Severity.CompareTo(right.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            var byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            if (byPath != 0)
            {
                return byPath;
            }

            var byLine = left.Range.Start.Line.CompareTo(right.Range.Start.Line);
            return byLine != 0 ? byLine : left.Range.Start.Character.CompareTo(right.Range.Start.Character);
        });

        return items;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pool.SessionStarted -= OnSessionStarted;
        pool.SessionClosed -= OnSessionClosed;
        List<(LanguageSession Session, EventHandler<DiagnosticsChangedEventArgs> Handler)> active;
        lock (gate)
        {
            active = [.. subscriptions.Select(static pair => (pair.Key, pair.Value))];
            subscriptions.Clear();
        }

        foreach (var (session, handler) in active)
        {
            session.Diagnostics.Changed -= handler;
        }
    }

    private void OnSessionStarted(object? sender, LanguageSession session) => Attach(session, raise: true);

    private void OnSessionClosed(object? sender, LanguageSession session) => Detach(session, raise: true);

    private void OnDiagnosticsChanged(object? sender, DiagnosticsChangedEventArgs e) => RaiseChanged();

    private void RaiseChanged()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Attach(LanguageSession session, bool raise)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || subscriptions.ContainsKey(session))
            {
                return;
            }

            EventHandler<DiagnosticsChangedEventArgs> handler = OnDiagnosticsChanged;
            subscriptions[session] = handler;
            session.Diagnostics.Changed += handler;
        }

        if (raise)
        {
            RaiseChanged();
        }
    }

    private void Detach(LanguageSession session, bool raise)
    {
        EventHandler<DiagnosticsChangedEventArgs>? handler;
        lock (gate)
        {
            if (!subscriptions.Remove(session, out handler))
            {
                return;
            }
        }

        session.Diagnostics.Changed -= handler;
        if (raise)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Brings the subscription set in line with the pool: a session the pool retired without announcing it (it was already
    /// disposed when looked up) is dropped here instead of staying subscribed forever. Chaining a session on has no visible
    /// effect until it publishes, so this never raises <see cref="Changed"/> — a listener that reacts by calling
    /// <see cref="Snapshot"/> would otherwise call itself.
    /// </summary>
    private void Reconcile()
    {
        var live = pool.Sessions;
        List<LanguageSession> stale = [];
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            foreach (var session in subscriptions.Keys)
            {
                if (!live.Contains(session))
                {
                    stale.Add(session);
                }
            }
        }

        foreach (var session in stale)
        {
            Detach(session, raise: false);
        }

        foreach (var session in live)
        {
            Attach(session, raise: false);
        }
    }
}
