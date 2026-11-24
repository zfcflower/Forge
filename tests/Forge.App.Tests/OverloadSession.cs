using Avalonia.Controls;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// One session with a live runtime, driven through the workspace's own turn path (composer, gate, event switch, turn finally)
/// and through the provider pipeline the shell builds: the retry middleware is always in the path, because that is where the
/// behaviour under test lives. Shared by the wiring cases (scripted endpoint, sleeps removed) and the end-to-end cases (a real
/// HTTP adapter over a fake transport, real waits).
/// </summary>
internal sealed class OverloadSession : IDisposable
{
    private readonly string directory;
    private readonly HttpClient http = new();
    private readonly SqliteSessionStore store;
    private Window? window;

    private OverloadSession(string directory, SqliteSessionStore store, SessionWorkspaceViewModel tab)
    {
        this.directory = directory;
        this.store = store;
        Tab = tab;
    }

    public SessionWorkspaceViewModel Tab { get; }

    /// <summary>Turns that produced a card; a session can hold a notice round of its own (a provider that cannot be resolved).</summary>
    public int Turns => Tab.Transcript.Rounds.Count(static round => round.Card is not null);

    /// <param name="endpoint">The provider the pipeline wraps.</param>
    /// <param name="mechanicalWaits">
    /// True replaces the retry ladder's sleeps with no-ops and its jitter with the identity: what a case that pins the ladder's
    /// numbers wants. False keeps the real middleware — the end-to-end cases let the server's own <c>Retry-After</c> decide the
    /// wait, so nothing has to be injected.
    /// </param>
    public static async Task<OverloadSession> StartAsync(IChatProvider endpoint, bool mechanicalWaits = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-overload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SqliteSessionStore(directory);
        var created = await ((ISessionCatalog)store).CreateAsync(
            new SessionCreate(directory, "overload session"),
            CancellationToken.None);

        var runtime = new SessionRuntime(
            new AgentRuntimeServices(
                ProviderFactory: _ => mechanicalWaits
                    ? new RetryingChatProvider(
                        endpoint,
                        delayAsync: static (_, _) => ValueTask.CompletedTask,
                        jitter: static ceiling => ceiling)
                    : new RetryingChatProvider(endpoint),
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            created);

        var tab = new SessionWorkspaceViewModel(
            created,
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, new HttpClient()))
        {
            // Nothing fires behind an assertion unless the case asks for it.
            AutoResumeDelay = TimeSpan.FromMinutes(5),
        };
        return new OverloadSession(directory, store, tab);
    }

    /// <summary>
    /// Sends one message and waits for <paramref name="expectRuns"/> turns to close. Without <paramref name="autoResume"/>
    /// the automatic round stays five minutes away.
    /// </summary>
    public async Task RunTurnAsync(string text, int expectRuns, bool autoResume = false)
    {
        var runs = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Tab.TurnCompleted += _ =>
        {
            if (Interlocked.Increment(ref runs) >= expectRuns)
            {
                done.TrySetResult();
            }
        };

        Tab.AutoResumeDelay = autoResume ? TimeSpan.Zero : TimeSpan.FromMinutes(5);
        Tab.Draft = text;
        Tab.SendCommand.Execute(null);

        await done.Task.WaitAsync(TimeSpan.FromMinutes(2));

        // The timeline rides the frame coalescer: a turn's completion can be observed before the frame that paints its rows.
        SettleFrames();
    }

    /// <summary>The quiet, non-error lines of the transcript, in order.</summary>
    public List<string> Notes() =>
        Tab.Transcript.FlatRows()
            .OfType<TurnNoticeViewModel>()
            .Where(static note => !note.IsError)
            .Select(static note => note.Text)
            .ToList();

    /// <summary>Pumps a few frames, for the cases that assert nothing further happens.</summary>
    public void SettleFrames()
    {
        window ??= new Window { Width = 320, Height = 200 };
        if (!window.IsVisible)
        {
            window.Show();
        }

        SharedUi.Settle(window);
    }

    public void Dispose()
    {
        window?.Close();
        Tab.Dispose();
        store.Dispose();
        http.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
