using Forge.Editor.Languages;
using Forge.Editor.Lsp;
using Forge.Editor.Tests.Support;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Lsp;

public class LanguageSessionTests
{
    private const string MissingCommand = "forge-editor-tests-no-such-server-7f3a";

    [Fact]
    public void Stderr_ring_keeps_the_newest_lines_and_reports_them_newest_first()
    {
        var ring = new StderrRing(capacity: 3, maxChars: 8);

        ring.Add("one");
        ring.Add("two");
        ring.Add(string.Empty);
        ring.Add("three");
        ring.Add("a very long line");
        ring.Add("four");

        Assert.Equal(new[] { "four", "a very l", "three" }, ring.Tail(10));
        Assert.Equal(new[] { "four" }, ring.Tail(1));
        Assert.Equal("four" + Environment.NewLine + "a very l", ring.Text(2));
        ring.Clear();
        Assert.Empty(ring.Tail());
    }

    [Fact]
    public void Stderr_ring_rejects_a_zero_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StderrRing(capacity: 0));
    }

    [Fact]
    public void Missing_executables_are_reported_with_their_command()
    {
        var error = Assert.Throws<LanguageServerNotFoundException>(() => LanguageServerProcess.ResolveExecutable(MissingCommand));
        Assert.Equal(MissingCommand, error.Command);
        Assert.Throws<ArgumentException>(() => LanguageServerProcess.ResolveExecutable(" "));
        Assert.Throws<LanguageServerNotFoundException>(() => LanguageServerProcess.ResolveExecutable(Path.Combine(Path.GetTempPath(), MissingCommand)));
    }

    [Fact]
    public void Existing_file_paths_resolve_to_their_full_path()
    {
        var assembly = typeof(LanguageSessionTests).Assembly.Location;

        Assert.Equal(Path.GetFullPath(assembly), LanguageServerProcess.ResolveExecutable(assembly));
    }

    [Fact]
    public async Task Starting_a_missing_server_throws_without_leaving_a_process_behind()
    {
        await using var process = new LanguageServerProcess(new LanguageServerLaunch(MissingCommand));

        Assert.Throws<LanguageServerNotFoundException>(() => process.Start());
        Assert.False(process.IsRunning);
        Assert.Null(process.ProcessId);
        Assert.Equal(0, process.CrashCount);
    }

    [Fact]
    public async Task Session_for_a_missing_server_is_unavailable_and_keeps_editing_working()
    {
        var logs = new List<string>();
        await using var session = new LanguageSession(
            new LanguageServerLaunch(MissingCommand),
            new LanguageSessionOptions { Log = logs.Add, LanguageId = "x" });
        var states = new List<LanguageSessionState>();
        session.StateChanged += (_, _) => states.Add(session.State);
        using var dir = new TempDirectory();
        var document = new TextDocument("text", dir.PathOf("a.x"));

        Assert.False(await session.TryStartAsync());

        Assert.Equal(LanguageSessionState.Unavailable, session.State);
        Assert.Contains(MissingCommand, session.LastError);
        Assert.NotEmpty(logs);
        Assert.Contains(LanguageSessionState.Starting, states);
        await session.OpenAsync(document);
        Assert.Empty(session.OpenDocuments);
        Assert.Null(await session.RequestHoverAsync(document, 0));
        Assert.Null(await session.RequestCompletionAsync(document, 0));
        Assert.Null(await session.RequestSemanticTokensAsync(document));
        await Assert.ThrowsAsync<LanguageServerCrashedException>(() => session.StartAsync());
    }

    [Fact]
    public async Task Session_touch_updates_the_last_used_time()
    {
        await using var session = new LanguageSession(new LanguageServerLaunch(MissingCommand));
        var before = session.LastUsedAt;
        await Task.Delay(5);

        session.Touch();

        Assert.True(session.LastUsedAt > before);
        Assert.Equal(Directory.GetCurrentDirectory(), session.WorkspacePath);
    }


    [Fact]
    public async Task Session_disposal_moves_to_disposed_and_is_idempotent()
    {
        var session = new LanguageSession(new LanguageServerLaunch(MissingCommand));
        var states = new List<LanguageSessionState>();
        session.StateChanged += (_, _) => states.Add(session.State);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(LanguageSessionState.Disposed, session.State);
        Assert.Equal(new[] { LanguageSessionState.Disposed }, states);
        Assert.False(await session.TryStartAsync());
    }

    [Fact]
    public async Task Server_that_exits_during_initialisation_leaves_the_session_unavailable()
    {
        var logs = new List<string>();
        await using var session = new LanguageSession(
            new LanguageServerLaunch("dotnet", ["--version"]),
            new LanguageSessionOptions { Log = logs.Add });

        var started = await session.TryStartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

        Assert.False(started);
        Assert.Equal(LanguageSessionState.Unavailable, session.State);
        Assert.Null(session.Client);
        Assert.Contains(logs, line => line.Contains("failed to initialize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pool_returns_no_session_for_languages_without_a_server_or_with_a_missing_server()
    {
        var registry = await RegistryWithServer(MissingCommand);
        var logs = new List<string>();
        await using var pool = new LanguageSessionPool(
            registry,
            new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero, Log = logs.Add });
        using var dir = new TempDirectory();

        Assert.Null(await pool.GetSessionAsync("nope", dir.Root));
        Assert.Null(await pool.GetSessionAsync((string?)null, dir.Root));
        Assert.Null(await pool.GetSessionAsync(new LanguageDefinition("plain", [".txt"], [], null), dir.Root));
        Assert.Null(await pool.GetSessionAsync("csharp", dir.Root));
        Assert.Equal(0, pool.SessionCount);
        Assert.Contains(logs, line => line.Contains(MissingCommand, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pool_open_document_without_a_session_does_not_mark_the_document()
    {
        var registry = await RegistryWithServer(MissingCommand);
        await using var pool = new LanguageSessionPool(registry, new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero });
        using var dir = new TempDirectory();
        var document = new TextDocument("class A {}", dir.PathOf("a.cs"));

        var session = await pool.OpenDocumentAsync(dir.Root, document);

        Assert.Null(session);
        Assert.Null(document.LanguageId);
        await pool.CloseDocumentAsync(dir.Root, document);
    }

    [Fact]
    public async Task Pool_rejects_work_after_disposal_and_tolerates_closing_unknown_sessions()
    {
        var registry = await RegistryWithServer(MissingCommand);
        var pool = new LanguageSessionPool(registry, new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero });
        using var dir = new TempDirectory();

        await pool.CloseSessionAsync("csharp", dir.Root);
        await pool.CloseWorkspaceAsync(dir.Root);
        await pool.SweepIdleSessionsAsync();
        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Null(await pool.GetSessionAsync("csharp", dir.Root));
        Assert.Equal(0, pool.SessionCount);
        Assert.Empty(pool.Sessions);
    }

    [Fact]
    public async Task Pool_sweep_rejects_cancelled_tokens()
    {
        await using var pool = new LanguageSessionPool(LanguageRegistry.Default, new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.SweepIdleSessionsAsync(cts.Token));
    }

    [Fact]
    public async Task Pool_with_a_zero_idle_timeout_has_no_background_sweep_and_still_disposes_cleanly()
    {
        await using var pool = new LanguageSessionPool(
            await RegistryWithServer(MissingCommand),
            new LanguageSessionPoolOptions { IdleTimeout = TimeSpan.Zero, SweepInterval = TimeSpan.FromMilliseconds(10) });

        await pool.SweepIdleSessionsAsync();
        Assert.Equal(0, pool.SessionCount);
    }

    [Fact]
    public async Task Sessions_are_not_retained_by_a_pool_that_could_not_start_them()
    {
        var registry = await RegistryWithServer("dotnet");
        await using var pool = new LanguageSessionPool(registry, new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero });
        using var dir = new TempDirectory();

        Assert.Null(await pool.GetSessionAsync("csharp", dir.Root, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token));
        Assert.Empty(pool.Sessions);
    }

    [Fact]
    public async Task Restart_with_no_attempts_allowed_fails_immediately_with_the_stderr_tail()
    {
        await using var process = new LanguageServerProcess(
            new LanguageServerLaunch(MissingCommand),
            new RestartPolicy { MaxAttempts = 0 });

        var error = await Assert.ThrowsAsync<LanguageServerCrashedException>(() => process.RestartAsync());

        Assert.Contains("failed to stay up after 0 restart attempts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_backoff_gives_up_on_a_missing_executable_after_the_delay()
    {
        await using var process = new LanguageServerProcess(
            new LanguageServerLaunch(MissingCommand),
            new RestartPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(30), MaxDelay = TimeSpan.FromMilliseconds(60) });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<LanguageServerNotFoundException>(() => process.RestartAsync());

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(25));
        Assert.Equal(1, process.CrashCount);
    }

    [Fact]
    public async Task Process_that_exits_on_its_own_raises_exited_with_its_exit_code()
    {
        await using var process = new LanguageServerProcess(new LanguageServerLaunch("dotnet", ["--version"]));
        var exited = new TaskCompletionSource<LanguageServerExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, args) => exited.TrySetResult(args);

        var streams = process.Start();
        var args = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await process.StopAsync();

        Assert.False(args.Expected);
        Assert.Equal(0, args.ExitCode);
        Assert.False(process.IsRunning);
        Assert.NotNull(streams.Input);
    }

    [Fact]
    public async Task Sync_pump_coalesces_a_burst_of_changes_into_one_notification()
    {
        var process = new FakeLanguageServerProcess();
        await using var session = SessionWith(process);
        using var dir = new TempDirectory();
        var document = new TextDocument("start", dir.PathOf("a.x")) { LanguageId = "x" };

        await session.OpenAsync(document);
        await process.WaitForMethodAsync("textDocument/didOpen");

        process.HoldWrites();
        document.Insert(5, "1");
        await process.WaitForWriteAsync();
        document.Insert(6, "2");
        document.Insert(7, "3");
        document.Insert(8, "4");
        process.ReleaseWrites();

        var first = await process.WaitForMethodAsync("textDocument/didChange");
        var coalesced = await process.WaitForMethodAsync("textDocument/didChange", 2);

        Assert.Equal(2, process.CountMethod("textDocument/didChange"));
        var firstChange = first.GetProperty("params").GetProperty("contentChanges")[0];
        Assert.True(firstChange.TryGetProperty("range", out _));
        Assert.Equal("1", firstChange.GetProperty("text").GetString());
        var coalescedChange = coalesced.GetProperty("params").GetProperty("contentChanges")[0];
        Assert.False(coalescedChange.TryGetProperty("range", out _));
        Assert.Equal("start1234", coalescedChange.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Crash_of_a_running_server_restarts_it_and_reopens_documents()
    {
        var policy = new RestartPolicy { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) };
        var process = new FakeLanguageServerProcess(policy);
        await using var session = SessionWith(process, policy);
        using var dir = new TempDirectory();
        var document = new TextDocument("class A {}", dir.PathOf("a.cs")) { LanguageId = "csharp" };

        await session.OpenAsync(document);
        await process.WaitForMethodAsync("textDocument/didOpen");
        var crashed = session.Client;
        Assert.NotNull(crashed);

        await process.CrashAsync();
        await WaitUntilAsync(() => session.RestartCount == 1);

        Assert.Equal(LanguageSessionState.Stopped, session.State);
        Assert.False(crashed.IsAlive);
        Assert.True(await session.TryStartAsync());
        var reopened = await process.WaitForMethodAsync("textDocument/didOpen", 2);

        Assert.Equal(LanguageSessionState.Running, session.State);
        Assert.NotSame(crashed, session.Client);
        Assert.True(session.Client!.IsAlive);
        Assert.Contains(document, session.OpenDocuments);
        var textDocument = reopened.GetProperty("params").GetProperty("textDocument");
        Assert.Equal("class A {}", textDocument.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Pool_reuses_a_running_session_for_the_same_workspace_and_language()
    {
        var registry = await RegistryWithServer(FakeServerCommand);
        using var dir = new TempDirectory();
        using var factory = new FakeProcessFactoryScope();
        await using var pool = new LanguageSessionPool(registry, new LanguageSessionPoolOptions { SweepInterval = TimeSpan.Zero });

        var first = await pool.GetSessionAsync("csharp", dir.Root);
        var second = await pool.GetSessionAsync("csharp", dir.Root);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(LanguageSessionState.Running, first.State);
    }

    [Fact]
    public async Task Idle_sweep_disposes_sessions_past_the_idle_timeout()
    {
        var registry = await RegistryWithServer(FakeServerCommand);
        using var dir = new TempDirectory();
        using var factory = new FakeProcessFactoryScope();
        await using var pool = new LanguageSessionPool(
            registry,
            new LanguageSessionPoolOptions { IdleTimeout = TimeSpan.Zero, SweepInterval = TimeSpan.Zero });

        var session = await pool.GetSessionAsync("csharp", dir.Root);
        Assert.NotNull(session);
        Assert.Equal(1, pool.SessionCount);

        await pool.SweepIdleSessionsAsync();

        Assert.Equal(0, pool.SessionCount);
        Assert.Empty(pool.Sessions);
        Assert.Equal(LanguageSessionState.Disposed, session.State);
    }

    private const string FakeServerCommand = "forge-editor-tests-fake-server";

    /// <summary>Installs the in-process fake as the process-wide factory; the pool itself has no seam of its own.</summary>
    private sealed class FakeProcessFactoryScope : IDisposable
    {
        public FakeProcessFactoryScope() =>
            LanguageServerProcess.DefaultFactory = static (_, policy) => new FakeLanguageServerProcess(policy);

        public void Dispose() => LanguageServerProcess.DefaultFactory = null;
    }

    private static LanguageSession SessionWith(LanguageServerProcess process, RestartPolicy? policy = null) =>
        new(
            new LanguageServerLaunch(FakeServerCommand),
            new LanguageSessionOptions
            {
                LanguageId = "x",
                RestartPolicy = policy ?? new RestartPolicy(),
                ProcessFactory = (_, _) => process,
            });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<LanguageRegistry> RegistryWithServer(string command)
    {
        using var dir = new TempDirectory();
        var user = dir.WriteText("languages.json", """{"languages":{"csharp":{"server":{"command":"__CMD__"}}}}""".Replace("__CMD__", command));
        return await LanguageRegistry.LoadAsync(null, user);
    }
}
