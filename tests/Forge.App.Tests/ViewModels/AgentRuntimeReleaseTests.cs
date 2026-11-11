using System.Text.Json;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the four moments <see cref="AgentRuntimeManager.Release"/> may be called (see the forge-app spec, "子 Agent 区"
/// and "布局快照自管"): closing a tab whose session is idle, closing a tab while the turn still runs (released when the
/// turn ends), archiving the session, and app exit before the composition root tears its stores down. A second release
/// of the same session is a no-op, never an error.
///
/// The shell only reaches the manager through <see cref="MainWindowViewModel"/>, so the tab and archive cases run over a
/// throw-away data root and a real composition root; "released" is observed through the manager itself, which hands out a
/// new runtime once the old one is dropped.
/// </summary>
[Collection("forge-app-ui")]
public sealed class AgentRuntimeReleaseTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string SessionTitle = "session under test";

    [Fact]
    public async Task Closing_an_idle_session_tab_releases_its_runtime()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);

                harness.CloseSessionTab(info.Id);

                Assert.NotSame(runtime, harness.Composition.Runtimes.Get(info));
            });
    }

    [Fact]
    public async Task Closing_an_idle_session_tab_disposes_the_runtime_it_released()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);

                harness.CloseSessionTab(info.Id);

                await Assert.ThrowsAsync<ObjectDisposedException>(
                    () => runtime.RunTurnAsync("anything", _ => { }, CancellationToken.None));
            });
    }

    [Fact]
    public async Task Closing_a_tab_while_the_turn_runs_keeps_its_runtime()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);
                var session = await harness.StartTurnAsync(info);
                Assert.True(session.IsRunning, "the turn must be running before the tab is closed");

                harness.CloseSessionTab(info.Id);

                Assert.Same(runtime, harness.Composition.Runtimes.Get(info));
            });
    }

    [Fact]
    public async Task Closing_a_tab_while_the_turn_runs_releases_the_runtime_when_the_turn_ends()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);
                var session = await harness.StartTurnAsync(info);
                harness.CloseSessionTab(info.Id);

                Assert.True(await PumpUntilAsync(() => !session.IsRunning), "the turn never finished");

                Assert.NotSame(runtime, harness.Composition.Runtimes.Get(info));
            });
    }

    [Fact]
    public async Task Archiving_a_session_releases_its_runtime()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);

                await harness.ViewModel.Projects.ArchiveAsync(info.Id, CancellationToken.None);

                Assert.NotSame(runtime, harness.Composition.Runtimes.Get(info));
            });
    }

    [Fact]
    public async Task Shutdown_releases_every_runtime()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                var runtime = harness.Composition.Runtimes.Get(info);

                harness.ViewModel.Dispose();

                await Assert.ThrowsAsync<ObjectDisposedException>(
                    () => runtime.RunTurnAsync("anything", _ => { }, CancellationToken.None));
            });
    }

    /// <summary>The stores are torn down after the runtimes, so a released runtime still leaves a usable composition root.</summary>
    [Fact]
    public async Task Shutdown_releases_the_runtimes_while_the_stores_still_answer()
    {
        await RunAsync(
            async harness =>
            {
                var info = await harness.SessionInfoAsync();
                _ = harness.Composition.Runtimes.Get(info);

                harness.ViewModel.Dispose();

                var stored = await harness.Composition.Sessions.GetSessionAsync(info.Id, CancellationToken.None);
                Assert.NotNull(stored);
            });
    }

    [Fact]
    public async Task A_second_release_is_a_no_op()
    {
        var session = SharedUi.Session;
        var released = await session.Dispatch(
            () =>
            {
                var info = new SessionInfo("s1", Path.GetTempPath(), SessionTitle, DateTimeOffset.UnixEpoch);
                using var manager = new AgentRuntimeManager(Services());
                var runtime = manager.Get(info);
                manager.Release(info.Id);
                manager.Release(info.Id);
                var replacement = manager.Get(info);
                manager.Dispose();
                manager.Release(info.Id);
                return !ReferenceEquals(runtime, replacement);
            },
            CancellationToken.None);

        Assert.True(released, "the first release must drop the runtime");
    }

    private static AgentRuntimeServices Services() =>
        new(
            static _ => new MissingCredentialProvider("unconfigured", Forge.App.Localization.ProviderStrings.SectionMissingText()),
            HostClient: null,
            new EmptyStore(),
            new EmptyCatalog(),
            new EmptyTodoStore(),
            new ModelSettings("model"));

    private static async Task RunAsync(Func<Harness, Task> body)
    {
        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync();
                try
                {
                    await body(harness);
                }
                finally
                {
                    await harness.DisposeAsync();
                }

                return 0;
            },
            CancellationToken.None);
    }

    private static async Task<bool> PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        return condition();
    }

    /// <summary>One throw-away data root, a real composition root and the root view model restored over one session tab.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private const string OpenTabsKey = "open-tabs";

        private readonly string root;
        private readonly EnvironmentSnapshot previous;
        private readonly string sessionId;

        private Harness(string root, string sessionId, EnvironmentSnapshot previous, AppComposition composition, MainWindowViewModel viewModel)
        {
            this.root = root;
            this.sessionId = sessionId;
            this.previous = previous;
            Composition = composition;
            ViewModel = viewModel;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel ViewModel { get; }

        public static async Task<Harness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-runtime-release-" + Guid.NewGuid().ToString("N"));
            var data = Path.Combine(root, "data");
            var workspace = Path.Combine(root, "workspace");
            var general = Path.Combine(root, "general");
            var work = Path.Combine(root, "work");
            foreach (var directory in new[] { data, workspace, general, work })
            {
                Directory.CreateDirectory(directory);
            }

            var previous = EnvironmentSnapshot.Capture();
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);

            var composition = AppComposition.Create();
            var info = await Task.Run(() => composition.Sessions.CreateSessionAsync(work, SessionTitle, CancellationToken.None));
            var tabs = new OpenTabsSnapshot { Sessions = [info.Id], ActiveSession = info.Id };
            var json = JsonSerializer.Serialize(tabs, WorkbenchJsonContext.Default.OpenTabsSnapshot);
            await Task.Run(() => composition.UiState.PutAsync(OpenTabsKey, OpenTabsSnapshot.CurrentVersion, json, CancellationToken.None));

            var viewModel = new MainWindowViewModel(composition);
            await viewModel.InitializeAsync();
            return new Harness(root, info.Id, previous, composition, viewModel);
        }

        public async Task<SessionInfo> SessionInfoAsync()
        {
            var info = await Composition.Sessions.GetSessionAsync(sessionId, CancellationToken.None);
            return Assert.IsType<SessionInfo>(info);
        }

        public void CloseSessionTab(string id) =>
            ViewModel.Workbench.CloseTab(Assert.IsType<TabModel>(ViewModel.Workbench.FindTab(WorkbenchIds.SessionTab(id))));

        /// <summary>
        /// Sends a message so the session turns running. This runs no provider: without <c>settings.json</c> the turn fails
        /// on the first stream event, which is enough to hold the session running until the turn ends.
        /// </summary>
        /// <summary>Sends a message and waits until the turn runs: the turn-start check reads the store first, so the
        /// turn no longer starts synchronously inside <c>Execute</c>.</summary>
        public async Task<SessionWorkspaceViewModel> StartTurnAsync(SessionInfo info)
        {
            var session = Assert.IsType<SessionWorkspaceViewModel>(ViewModel.ActiveSession);
            Assert.Equal(info.Id, session.SessionId);
            session.Draft = "hello";
            session.SendCommand.Execute(null);
            await PumpUntilAsync(() => session.IsRunning);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            ViewModel.Files.Attach(PanelContext.Empty);
            ViewModel.Dispose();
            await Composition.DisposeAsync();

            previous.Restore();

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private readonly record struct EnvironmentSnapshot(string DataRoot, string Workspace, string GeneralWorkspace)
    {
        public static EnvironmentSnapshot Capture() =>
            new(
                Environment.GetEnvironmentVariable(AppPaths.DataVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable) ?? string.Empty,
                Environment.GetEnvironmentVariable(AppPaths.GeneralVariable) ?? string.Empty);

        public void Restore()
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, DataRoot);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Workspace);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, GeneralWorkspace);
        }
    }

    private sealed class EmptyStore : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", workspacePath, title, DateTimeOffset.UnixEpoch));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(1L);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            Empty();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => Task.FromResult(0L);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        private static async IAsyncEnumerable<StoredMessage> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class EmptyCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyTodoStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            Task.FromResult(new TodoItem("t1", sessionId, title, TodoStatus.Pending, note, 0, DateTimeOffset.UnixEpoch));

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }
}
