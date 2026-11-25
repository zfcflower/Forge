using System.Runtime.CompilerServices;
using Forge.Abstractions;using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// An agent-session tab interjects into its own running turn, exactly like a root tab interjects into the root's: the message
/// reaches the instance that is running <em>this</em> session's turn at its next tool-batch boundary, and a turn that ended
/// without reaching one hands it back to the queue instead of losing it.
/// <para>Driven through the composer's own commands on a real runtime (scripted provider, real store, real loop), so what is
/// asserted is the chip the user sees and the message the model receives - not the branch that decides between them.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class AgentSessionInterjectTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("forge-agent-interject-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public Task An_agent_session_interjects_into_its_own_run_and_requeues_what_the_loop_never_took() =>
        SharedUi.RunAsync(async () =>
        {
            var store = new SqliteSessionStore(directory);
            var root = await ((ISessionCatalog)store).CreateAsync(
                new SessionCreate(directory, "root session"),
                CancellationToken.None);
            var provider = new GatedProvider();
            var runtime = new SessionRuntime(
                new AgentRuntimeServices(
                    ProviderFactory: _ => provider,
                    HostClient: null,
                    Store: store,
                    Catalog: store,
                    Todos: new SqliteTodoStore(directory),
                    Model: new ModelSettings("test-model")),
                root);
            using var http = new HttpClient();
            var tab = new SessionWorkspaceViewModel(
                root with { Id = "draft-1", ParentSessionId = root.Id, ChildDriver = AgentSessionKinds.ChildDriverUser },
                runtime,
                checkpoints: null,
                store,
                new ProviderServices(Path.Combine(directory, "settings.json"), directory, http),
                agentSession: new AgentSessionLaunch(root, Ordinal: 1, IsDraft: true, Forge.Core.Prompts.BuiltInAgents.Worker))
            {
                // Nothing fires behind an assertion unless the case asks for it.
                AutoResumeDelay = TimeSpan.FromMinutes(5),
            };

            // What the strip showed at each turn end: the first one is where the undelivered interjection has to reappear.
            var atTurnEnd = new List<IReadOnlyList<(string Text, bool IsInterjection)>>();
            var turns = 0;
            var secondTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.TurnCompleted += _ =>
            {
                atTurnEnd.Add([.. tab.Queued.Select(static chip => (chip.Text, chip.IsInterjection))]);
                if (Interlocked.Increment(ref turns) == 2)
                {
                    secondTurn.TrySetResult();
                }
            };

            try
            {
                tab.Draft = "first message";
                tab.SendCommand.Execute(null);

                // The first request is in flight: the turn is running on this tab's own child instance, so Enter can interject.
                await provider.FirstRequest.Task.WaitAsync(TimeSpan.FromMinutes(1));
                Assert.True(tab.IsRunning);
                Assert.Equal("user", (await store.GetSessionAsync(tab.AgentChildSessionId!, CancellationToken.None))!.ChildDriver);

                tab.Draft = "hold on, do it the other way";
                Assert.True(tab.InterjectCommand.CanExecute(null));
                tab.InterjectCommand.Execute(null);

                var chip = Assert.Single(tab.Queued);
                Assert.True(chip.IsInterjection);
                Assert.Equal(SessionStrings.ComposerInterjecting, chip.Label);
                Assert.Empty(tab.Draft);

                // The answer ends the turn before any tool batch: the loop never reached the boundary, so the message comes
                // back to the queue (a plain queued message now) and the next turn sends it.
                provider.Release();
                await secondTurn.Task.WaitAsync(TimeSpan.FromMinutes(2));

                var firstTurnEnd = Assert.Single(atTurnEnd[0]);
                var handedBack = firstTurnEnd;
                Assert.Equal("hold on, do it the other way", handedBack.Text);
                Assert.False(handedBack.IsInterjection);
                // The second turn took the queue as its own message, so nothing waits once it ended either.
                Assert.Empty(atTurnEnd[1]);

                // The second turn carried it as its own user message: that is the message the model read.
                lock (provider.Captured)
                {
                    Assert.Equal(2, provider.Captured.Count);
                    var sent = provider.Captured[1][^1];
                    Assert.Equal(MessageRole.User, sent.Role);
                    Assert.Equal("hold on, do it the other way", Assert.IsType<TextBlock>(Assert.Single(sent.Content)).Text);
                }

                Assert.Empty(tab.Queued);
            }
            finally
            {
                // A failed assertion must not leave the gated request hanging: the turn is stopped and everything released here.
                provider.Release();
                tab.Dispose();
                store.Dispose();
            }
        });

    /// <summary>
    /// Answers the first request only when the test lets it go, so the turn is genuinely in flight while the composer is driven.
    /// Requests after that answer at once, and every request's messages are kept: the case reads what the model was sent.
    /// </summary>
    private sealed class GatedProvider : IChatProvider
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        /// <summary>Completes when the first request arrived: the turn has started and is waiting for this answer.</summary>
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IReadOnlyList<ChatMessage>> Captured { get; } = [];

        public string Id => "gated";

        public void Release() => release.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var index = Interlocked.Increment(ref calls);
            lock (Captured)
            {
                Captured.Add(request.Messages);
            }

            var text = index == 1 ? "working on it" : "done";
            if (index == 1)
            {
                FirstRequest.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            yield return new TextDelta(text);
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock(text)]),
                TokenUsage.Empty,
                StopReason.EndTurn);
        }
    }
}
