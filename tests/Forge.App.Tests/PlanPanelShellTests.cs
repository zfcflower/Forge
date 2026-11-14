using System.Net;
using System.Net.Sockets;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The shell's side of the plan panel (forge-app spec, "计划面板(W7)"): the open command needs an active session and
/// toggles the overlay, a session switch closes it, and <see cref="IPlanPanelHost.HandoffToAgent"/> writes the
/// hand-off message into the composer and sends it (or reports the draft that blocks it, or the read-only session
/// that cannot take it). The turns run over the real composition root on a throw-away data root; without a configured
/// provider they fail on their first stream event, which is all these tests need.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PlanPanelShellTests
{
    /// <summary>
    /// How long the waits for a real turn poll for: a runtime build plus one model request over a loopback listener is much
    /// slower than the pump's default on a loaded machine, and a test that reports a missing turn must not be timing noise.
    /// </summary>
    private const int TurnWaitIterations = 6000;

    private const string HandoffPrompt = "Finish step \"收尾这一步\" (todo id t1): do the work it describes, then mark it done with the todo tool and record its summary and verification.";

    [Fact]
    public async Task Open_plan_needs_an_active_session()
    {
        var session = SharedUi.Session;
        await session.Dispatch(
            async () =>
            {
                await using var harness = await GitAppHarness.Open();
                var viewModel = new MainWindowViewModel(harness.Composition);
                try
                {
                    Assert.Null(viewModel.ActiveSession);
                    Assert.False(viewModel.OpenPlanCommand.CanExecute(null));
                    Assert.Null(viewModel.Plan);
                }
                finally
                {
                    harness.Detach(viewModel);
                }

                return 0;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Open_plan_toggles_the_panel()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-toggle-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    Assert.True(viewModel.OpenPlanCommand.CanExecute(null));
                    Assert.Null(viewModel.Plan);

                    viewModel.OpenPlanCommand.Execute(null);
                    Assert.NotNull(viewModel.Plan);
                    var opened = viewModel.Plan;

                    viewModel.OpenPlanCommand.Execute(null);
                    Assert.Null(viewModel.Plan);
                    Assert.True(viewModel.OpenPlanCommand.CanExecute(null));

                    // Opening again creates a fresh panel, not a re-shown corpse.
                    viewModel.OpenPlanCommand.Execute(null);
                    Assert.NotNull(viewModel.Plan);
                    Assert.NotSame(opened, viewModel.Plan);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
        }
    }

    [Fact]
    public async Task Switching_the_session_closes_the_panel()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-switch-");
        var otherDir = TestTempFiles.NewDirectory("forge-plan-switch-other-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    viewModel.OpenPlanCommand.Execute(null);
                    Assert.NotNull(viewModel.Plan);

                    // One live session per workspace: the second session needs its own directory.
                    var second = await Task.Run(() => harness.Composition.Sessions.CreateSessionAsync(otherDir, "second", CancellationToken.None));
                    await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(second.Id, SessionStatus.Archived, CancellationToken.None));
                    await viewModel.OpenSessionByIdAsync(second.Id, CancellationToken.None);

                    Assert.Equal(second.Id, viewModel.ActiveSession!.SessionId);
                    Assert.Null(viewModel.Plan);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
            TestTempFiles.DeleteTree(otherDir);
        }
    }

    [Fact]
    public async Task Handoff_on_an_idle_session_writes_the_prompt_sends_it_and_reports_sent()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-handoff-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);

                    var outcome = ((IPlanPanelHost)viewModel).HandoffToAgent(HandoffPrompt);

                    Assert.Equal(PlanHandoff.Sent, outcome);
                    Assert.Equal(string.Empty, live.Draft);
                    Assert.True(
                        await TestPump.UntilAsync(
                            () => live.Transcript.Items.Any(item => item is UserMessageViewModel message && message.Text == HandoffPrompt),
                            TurnWaitIterations),
                        "the hand-off never became a sent user message");
                    Assert.True(await TestPump.UntilAsync(() => !live.IsRunning, TurnWaitIterations), "the turn never finished");

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
        }
    }

    [Fact]
    public async Task Handoff_does_not_overwrite_an_unsent_draft()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-blocked-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);
                    live.Draft = "用户还没发的文字";

                    var outcome = ((IPlanPanelHost)viewModel).HandoffToAgent(HandoffPrompt);

                    Assert.Equal(PlanHandoff.BlockedByDraft, outcome);
                    Assert.Equal("用户还没发的文字", live.Draft);
                    Assert.False(live.IsRunning);
                    Assert.DoesNotContain(live.Transcript.Items, item => item is UserMessageViewModel message && message.Text == HandoffPrompt);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
        }
    }

    [Fact]
    public async Task Handoff_on_a_read_only_session_is_unavailable_and_keeps_the_draft()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-readonly-");
        var otherDir = TestTempFiles.NewDirectory("forge-plan-readonly-other-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;

                    // One live session per workspace: the read-only session needs its own directory.
                    var archived = await Task.Run(() => harness.Composition.Sessions.CreateSessionAsync(otherDir, "read-only", CancellationToken.None));
                    await Task.Run(() => harness.Composition.Catalog.SetStatusAsync(archived.Id, SessionStatus.Archived, CancellationToken.None));
                    await viewModel.OpenSessionByIdAsync(archived.Id, CancellationToken.None);
                    var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);
                    Assert.True(live.IsReadOnlyView);
                    live.Draft = "保留的草稿";

                    var outcome = ((IPlanPanelHost)viewModel).HandoffToAgent(HandoffPrompt);

                    Assert.Equal(PlanHandoff.Unavailable, outcome);
                    Assert.Equal("保留的草稿", live.Draft);
                    Assert.False(live.IsRunning);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
            TestTempFiles.DeleteTree(otherDir);
        }
    }

    [Fact]
    public async Task Handoff_while_a_turn_runs_queues_the_message()
    {
        // The running window a missing provider leaves is milliseconds wide, so this test points one configured
        // provider at a local TCP listener that accepts the chat request and never answers: the turn stays running
        // (blocked on its model request) for as long as the test needs it.
        using var server = new HangingChatServer();
        var (root, previous) = TempDataRoot.Redirect("forge-plan-queued-", "work");
        var previousKey = Environment.GetEnvironmentVariable("FORGE_W7_TEST_KEY");
        try
        {
            // The api-key provider resolves its key from the named environment variable; the key is written first.
            Environment.SetEnvironmentVariable("FORGE_W7_TEST_KEY", "test-key");
            File.WriteAllText(Path.Combine(root, "data", "settings.json"), ProviderSettings(server.Port));

            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    var composition = AppComposition.Create();
                    try
                    {
                        var info = await Task.Run(() => composition.Sessions.CreateSessionAsync(Path.Combine(root, "work"), "queued", CancellationToken.None));
                        var viewModel = new MainWindowViewModel(composition);
                        await viewModel.OpenSessionByIdAsync(info.Id, CancellationToken.None);
                        var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);

                        live.Draft = "first";
                        live.SendCommand.Execute(null);
                        Assert.True(
                            await TestPump.UntilAsync(() => live.IsRunning && server.Requests > 0, TurnWaitIterations),
                            "the turn never reached the blocked model request");

                        var outcome = ((IPlanPanelHost)viewModel).HandoffToAgent(HandoffPrompt);

                        Assert.Equal(PlanHandoff.Queued, outcome);
                        Assert.Equal(string.Empty, live.Draft);
                        var chip = Assert.Single(live.Queued);
                        Assert.Equal(HandoffPrompt, chip.Text);

                        // Let the turn die: the dropped connection fails the request and ends it.
                        server.Dispose();
                        Assert.True(await TestPump.UntilAsync(() => !live.IsRunning, TurnWaitIterations), "the turn never finished");

                        viewModel.Files.Attach(PanelContext.Empty);
                        viewModel.GitSync.Attach(PanelContext.Empty);
                        viewModel.Dispose();
                    }
                    finally
                    {
                        await composition.DisposeAsync();
                    }

                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_W7_TEST_KEY", previousKey);
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task Handoff_on_a_handing_over_session_is_unavailable_and_does_not_touch_the_draft()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-handing-over-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);
                    live.Draft = "还没发送的草稿";
                    live.IsHandingOver = true;

                    var outcome = ((IPlanPanelHost)viewModel).HandoffToAgent(HandoffPrompt);

                    Assert.Equal(PlanHandoff.Unavailable, outcome);
                    Assert.Equal("还没发送的草稿", live.Draft);
                    Assert.False(live.IsRunning);
                    Assert.DoesNotContain(live.Transcript.Items, item => item is UserMessageViewModel message && message.Text == HandoffPrompt);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
        }
    }

    [Fact]
    public async Task A_handoff_the_shell_declines_reports_plan_handoff_unavailable_in_the_status_line()
    {
        var sessionDir = TestTempFiles.NewDirectory("forge-plan-handoff-unavailable-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    await using var harness = await GitAppHarness.OpenWithSession(sessionDir);
                    var viewModel = harness.ViewModel;
                    var live = Assert.IsType<SessionWorkspaceViewModel>(viewModel.ActiveSession);
                    await Task.Run(
                        () => harness.Composition.Todos.AddAsync(new TodoAddRequest(live.SessionId, "交接中的步骤", null, null), CancellationToken.None));
                    live.IsHandingOver = true;

                    viewModel.OpenPlanCommand.Execute(null);
                    var panel = viewModel.Plan;
                    Assert.NotNull(panel);
                    Assert.True(await TestPump.UntilAsync(() => panel.Rows.Count == 2), "the panel never loaded its rows");

                    var step = Assert.Single(panel.Rows, static row => row.IsStepRow);
                    step.HandoffCommand!.Execute(null);

                    // The shell declined the hand-off: the status line says so, and nothing was written into the composer.
                    Assert.Equal(Loc.Instance[PlanStrings.HandoffUnavailable], panel.StatusText);
                    Assert.True(panel.StatusIsFailure);
                    Assert.Equal(string.Empty, live.Draft);

                    harness.Detach(viewModel);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(sessionDir);
        }
    }

    /// <summary>One configured provider whose base URL is the hanging listener: the shell catalog accepts it, so a turn
    /// really starts, and its first chat request blocks until the test drops the connection.</summary>
    private static string ProviderSettings(int port) =>
        $$"""
        {
          "providers": {
            "version": 1,
            "defaultId": "test",
            "items": [
              {
                "id": "test",
                "preset": "custom-openai",
                "name": "Test",
                "protocol": "openai-compatible",
                "baseUrl": "http://127.0.0.1:{{port}}",
                "auth": { "kind": "api-key", "envVar": "FORGE_W7_TEST_KEY" },
                "models": ["gpt-5.1"],
                "defaultModel": "gpt-5.1"
              }
            ]
          }
        }
        """;

    /// <summary>A loopback TCP listener that accepts chat requests, reads them and holds the connection open.</summary>
    private sealed class HangingChatServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Task loop;
        private readonly List<TcpClient> clients = [];
        private bool disposed;

        public HangingChatServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        private int requests;

        public int Requests => Volatile.Read(ref requests);

        private async Task AcceptLoopAsync()
        {
            while (!disposed)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref requests);
                lock (clients)
                {
                    clients.Add(client);
                }

                _ = HoldAsync(client);
            }
        }

        /// <summary>Reads the request headers so the client can finish sending, then holds the response open.</summary>
        private static async Task HoldAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                        if (read >= 4 && buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8) >= 0)
                        {
                            // Keep the connection open: the model "response" never comes until the test disposes us.
                            await Task.Delay(Timeout.InfiniteTimeSpan);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
                {
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            listener.Stop();
            lock (clients)
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }

                clients.Clear();
            }

            loop.Wait(TimeSpan.FromSeconds(10));
        }
    }
}
