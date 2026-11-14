using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Forge.Editor.Text;
using Forge.Editor.Tests.Support;

namespace Forge.Editor.Tests.Lsp;

/// <summary>
/// These tests install an in-process fake server through the process-wide
/// <see cref="LanguageServerProcess.DefaultFactory"/>, so the collection keeps them out of the parallel batch: no other
/// test class sees the factory while these run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FakeLanguageServerCollection
{
    public const string Name = "fake language server factory";
}

[Collection(FakeLanguageServerCollection.Name)]
public class WorkspaceDiagnosticsTests
{
    private const string FakeServerCommand = "forge-editor-tests-fake-server";

    [Fact]
    public async Task A_pool_without_sessions_reports_nothing()
    {
        await using var pool = NewPool(LanguageRegistry.Default);
        using var diagnostics = new WorkspaceDiagnostics(pool);

        Assert.Empty(diagnostics.Snapshot());
    }

    [Fact]
    public void A_missing_pool_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkspaceDiagnostics(null!));
    }

    [Fact]
    public async Task A_row_carries_the_file_path_workspace_severity_range_message_code_and_source()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        using var diagnostics = new WorkspaceDiagnostics(pool);

        var path = dir.PathOf("a.cs");
        var range = new LspRange(new LspPosition(2, 5), new LspPosition(2, 9));
        session.Diagnostics.Update(
            LspUri.FromPath(path),
            version: 4,
            [new LspDiagnostic(range, DiagnosticSeverity.Warning, "CS0168", "csharp", "The variable 'x' is never used.")]);

        var row = Assert.Single(diagnostics.Snapshot());

        Assert.Equal(Path.GetFullPath(path), row.Path);
        Assert.Equal(Path.GetFullPath(dir.Root), row.Workspace);
        Assert.Equal(PositionEncoding.Utf16, row.Encoding);
        Assert.Equal(DiagnosticSeverity.Warning, row.Severity);
        Assert.Equal(range, row.Range);
        Assert.Equal("The variable 'x' is never used.", row.Message);
        Assert.Equal("CS0168", row.Code);
        Assert.Equal("csharp", row.Source);
        Assert.Equal(3, row.Line); // one-based, taken from the range
        Assert.Equal(6, row.Column);
    }

    [Fact]
    public async Task A_published_document_that_is_not_a_file_is_left_out()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        using var diagnostics = new WorkspaceDiagnostics(pool);

        session.Diagnostics.Update("untitled:Untitled-1", version: null, [Diagnostic(0, DiagnosticSeverity.Error, "unsaved buffer")]);
        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("b.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "on disk")]);

        var row = Assert.Single(diagnostics.Snapshot());
        Assert.Equal(Path.GetFullPath(dir.PathOf("b.cs")), row.Path);
        Assert.Equal("on disk", row.Message);
    }

    [Fact]
    public async Task Changed_fires_when_a_session_publishes_diagnostics()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "first")]);

        Assert.Equal(1, changes);
        Assert.Single(diagnostics.Snapshot());

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 2, [Diagnostic(0, DiagnosticSeverity.Error, "second")]);

        Assert.Equal(2, changes);
        Assert.Equal("second", Assert.Single(diagnostics.Snapshot()).Message);
    }

    [Fact]
    public async Task Changed_fires_when_a_session_starts_and_when_it_closes()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        Assert.Equal(1, changes);

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "before close")]);
        Assert.Equal(2, changes);
        Assert.Single(diagnostics.Snapshot());

        await pool.CloseSessionAsync("csharp", dir.Root);

        Assert.Equal(3, changes);
        Assert.Empty(diagnostics.Snapshot());
    }

    [Fact]
    public async Task Changed_fires_when_a_workspace_closes_and_its_rows_go_away()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var closing = new TempDirectory();
        using var staying = new TempDirectory();
        var closingSession = await pool.GetSessionAsync("csharp", closing.Root);
        var stayingSession = await pool.GetSessionAsync("csharp", staying.Root);
        Assert.NotNull(closingSession);
        Assert.NotNull(stayingSession);
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        closingSession.Diagnostics.Update(LspUri.FromPath(closing.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "closing workspace")]);
        stayingSession.Diagnostics.Update(LspUri.FromPath(staying.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "staying workspace")]);
        Assert.Equal(2, changes);
        Assert.Equal(2, diagnostics.Snapshot().Count);

        await pool.CloseWorkspaceAsync(closing.Root);

        Assert.Equal(3, changes);
        var rows = diagnostics.Snapshot();
        Assert.DoesNotContain(rows, row => row.Workspace == Path.GetFullPath(closing.Root));
        var row = Assert.Single(rows);
        Assert.Equal("staying workspace", row.Message);
        Assert.Equal(Path.GetFullPath(staying.PathOf("a.cs")), row.Path);
        Assert.Equal(Path.GetFullPath(staying.Root), row.Workspace);
        Assert.Equal(1, pool.SessionCount);
    }

    [Fact]
    public async Task Changed_fires_when_the_idle_sweep_retires_a_session_and_its_rows_go_away()
    {
        var idleTimeout = TimeSpan.FromMilliseconds(300);
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers(), new LanguageSessionPoolOptions { IdleTimeout = idleTimeout });
        using var dir = new TempDirectory();
        var idle = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(idle);
        idle.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "idle after the sweep")]);
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        // The sweep only retires a session with no open documents, no lease and a LastUsedAt older than the timeout
        // (GetSessionAsync releases everything it takes), so the test lets the timeout genuinely elapse. The second session
        // is started right before the sweep: it is fresh, so it must survive it.
        Assert.Empty(idle.OpenDocuments);
        Assert.False(idle.HasActiveWork);
        await WaitUntilIdlePastAsync(idle, idleTimeout);
        var fresh = await pool.GetSessionAsync("fsharp", dir.Root);
        Assert.NotNull(fresh);
        fresh.Diagnostics.Update(LspUri.FromPath(dir.PathOf("b.fs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "fresh after the sweep")]);
        Assert.Equal(2, changes); // SessionStarted for the fresh session, then its first publication
        Assert.Equal(2, diagnostics.Snapshot().Count);

        await pool.SweepIdleSessionsAsync();

        Assert.Equal(3, changes); // SessionClosed for the retired session, and nothing for the fresh one
        var row = Assert.Single(diagnostics.Snapshot());
        Assert.Equal("fresh after the sweep", row.Message);
        Assert.Equal(LanguageSessionState.Disposed, idle.State);
        Assert.Same(fresh, Assert.Single(pool.Sessions));
        Assert.Equal(LanguageSessionState.Running, fresh.State);

        idle.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 2, [Diagnostic(0, DiagnosticSeverity.Error, "published after the sweep")]);

        Assert.Equal(3, changes); // the retired session's store no longer reaches the aggregator
        Assert.Single(diagnostics.Snapshot());
    }

    [Fact]
    public async Task Disposing_the_pool_raises_Changed_and_leaves_the_snapshot_empty()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "before dispose")]);
        Assert.Equal(1, changes);
        Assert.Single(diagnostics.Snapshot());

        await pool.DisposeAsync();

        // The aggregator itself is not disposed here: the pool's SessionClosed is what raises, and the pool holds no session
        // for the snapshot to read. (A disposed aggregator also returns [], which Dispose_stops_the_notifications_and_the_snapshot
        // covers; this test names the pool side because that is the event source.)
        Assert.Equal(2, changes);
        Assert.Empty(diagnostics.Snapshot());
        Assert.Equal(0, pool.SessionCount);

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 2, [Diagnostic(0, DiagnosticSeverity.Error, "after dispose")]);

        Assert.Equal(2, changes);
        Assert.Empty(diagnostics.Snapshot());
    }

    [Fact]
    public async Task Reconcile_drops_a_session_the_pool_forgot_without_an_event_and_later_publishes_do_not_reach_the_aggregator()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var retired = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(retired);
        using var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        retired.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "before")]);
        Assert.Equal(1, changes);
        Assert.Single(diagnostics.Snapshot());

        // A session that dies on its own is not closed through the pool: the next lookup drops its slot silently (no
        // SessionClosed), so only Reconcile can unsubscribe it. Rows of a disposed session are not listed even while the pool
        // still holds its slot, because nobody can act on diagnostics whose server is gone.
        await retired.DisposeAsync();
        Assert.Empty(diagnostics.Snapshot());

        var replacement = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(replacement);
        Assert.NotSame(retired, replacement);
        Assert.Equal(2, changes); // SessionStarted for the replacement

        // Two reads in a row: the first drops the stale subscription, the second must neither re-attach it nor raise.
        Assert.Empty(diagnostics.Snapshot());
        Assert.Empty(diagnostics.Snapshot());
        Assert.Equal(2, changes);

        retired.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 2, [Diagnostic(0, DiagnosticSeverity.Error, "after")]);

        Assert.Equal(2, changes);
        Assert.Empty(diagnostics.Snapshot());
    }

    [Fact]
    public async Task Every_session_contributes_rows_sorted_by_severity_then_path_then_line()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var csharp = await pool.GetSessionAsync("csharp", dir.Root);
        var fsharp = await pool.GetSessionAsync("fsharp", dir.Root);
        Assert.NotNull(csharp);
        Assert.NotNull(fsharp);
        Assert.NotSame(csharp, fsharp);
        using var diagnostics = new WorkspaceDiagnostics(pool);

        csharp.Diagnostics.Update(
            LspUri.FromPath(dir.PathOf("b.cs")),
            version: 1,
            [
                Diagnostic(5, DiagnosticSeverity.Error, "late error"),
                Diagnostic(1, DiagnosticSeverity.Error, "early error"),
                Diagnostic(1, DiagnosticSeverity.Warning, "warning"),
            ]);
        fsharp.Diagnostics.Update(
            LspUri.FromPath(dir.PathOf("a.fs")),
            version: 1,
            [
                Diagnostic(2, DiagnosticSeverity.Error, "error"),
                Diagnostic(0, DiagnosticSeverity.Warning, "warning"),
            ]);

        var rows = diagnostics.Snapshot();

        Assert.Equal(
            new[] { "a.fs:2:error", "b.cs:1:early error", "b.cs:5:late error", "a.fs:0:warning", "b.cs:1:warning" },
            rows.Select(row => $"{Path.GetFileName(row.Path)}:{row.Range.Start.Line}:{row.Message}"));
        Assert.Equal(
            new[]
            {
                DiagnosticSeverity.Error,
                DiagnosticSeverity.Error,
                DiagnosticSeverity.Error,
                DiagnosticSeverity.Warning,
                DiagnosticSeverity.Warning,
            },
            rows.Select(row => row.Severity));
        Assert.All(rows, row => Assert.Equal(Path.GetFullPath(dir.Root), row.Workspace));
    }

    [Fact]
    public async Task A_row_keeps_the_workspace_of_its_own_session()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var firstSession = await pool.GetSessionAsync("csharp", first.Root);
        var secondSession = await pool.GetSessionAsync("csharp", second.Root);
        Assert.NotNull(firstSession);
        Assert.NotNull(secondSession);
        using var diagnostics = new WorkspaceDiagnostics(pool);

        firstSession.Diagnostics.Update(LspUri.FromPath(first.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "first workspace")]);
        secondSession.Diagnostics.Update(LspUri.FromPath(second.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "second workspace")]);

        var rows = diagnostics.Snapshot();

        Assert.Equal(2, rows.Count);
        var firstRow = Assert.Single(rows, row => row.Message == "first workspace");
        Assert.Equal(Path.GetFullPath(first.PathOf("a.cs")), firstRow.Path);
        Assert.Equal(Path.GetFullPath(first.Root), firstRow.Workspace);
        var secondRow = Assert.Single(rows, row => row.Message == "second workspace");
        Assert.Equal(Path.GetFullPath(second.PathOf("a.cs")), secondRow.Path);
        Assert.Equal(Path.GetFullPath(second.Root), secondRow.Workspace);
    }

    [Fact]
    public async Task Dispose_stops_the_notifications_and_the_snapshot()
    {
        using var servers = new FakeServerFactory();
        await using var pool = NewPool(await RegistryWithFakeServers());
        using var dir = new TempDirectory();
        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        var diagnostics = new WorkspaceDiagnostics(pool);
        var changes = 0;
        diagnostics.Changed += (_, _) => changes++;

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 1, [Diagnostic(0, DiagnosticSeverity.Error, "before dispose")]);
        Assert.Equal(1, changes);
        Assert.Single(diagnostics.Snapshot());

        diagnostics.Dispose();
        diagnostics.Dispose(); // idempotent

        session.Diagnostics.Update(LspUri.FromPath(dir.PathOf("a.cs")), version: 2, [Diagnostic(0, DiagnosticSeverity.Error, "after dispose")]);
        await pool.CloseSessionAsync("csharp", dir.Root);

        Assert.Equal(1, changes);
        Assert.Empty(diagnostics.Snapshot());
    }

    private static LanguageSessionPool NewPool(LanguageRegistry registry, LanguageSessionPoolOptions? options = null) =>
        new(registry, (options ?? new LanguageSessionPoolOptions()) with { SweepInterval = TimeSpan.Zero });

    /// <summary>
    /// The sweep compares the session's wall-clock <see cref="LanguageSession.LastUsedAt"/> with
    /// <c>UtcNow - IdleTimeout</c> and the session has no clock seam, so the test lets the timeout genuinely elapse and waits
    /// on the pool's own predicate instead of sweeping a session that is still fresh.
    /// </summary>
    private static async Task WaitUntilIdlePastAsync(LanguageSession session, TimeSpan idleTimeout)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (session.LastUsedAt + idleTimeout > DateTimeOffset.UtcNow)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the session never reached the idle timeout");
            await Task.Delay(1);
        }
    }

    private static LspDiagnostic Diagnostic(int line, DiagnosticSeverity severity, string message) =>
        new(
            new LspRange(new LspPosition(line, 0), new LspPosition(line, 1)),
            severity,
            "X1",
            "test-server",
            message);

    private static async Task<LanguageRegistry> RegistryWithFakeServers()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteText(
            "languages.json",
            """{"languages":{"csharp":{"server":{"command":"__CMD__"}},"fsharp":{"server":{"command":"__CMD__"}}}}"""
                .Replace("__CMD__", FakeServerCommand, StringComparison.Ordinal));
        return await LanguageRegistry.LoadAsync(null, path);
    }

    /// <summary>Installs the in-process fake as the process-wide factory; the pool itself has no seam of its own.</summary>
    private sealed class FakeServerFactory : IDisposable
    {
        private readonly Func<LanguageServerLaunch, RestartPolicy, LanguageServerProcess>? _previous;

        public FakeServerFactory()
        {
            _previous = LanguageServerProcess.DefaultFactory;
            LanguageServerProcess.DefaultFactory = static (_, policy) => new FakeLanguageServerProcess(policy);
        }

        public void Dispose() => LanguageServerProcess.DefaultFactory = _previous;
    }
}
