using System.Runtime.CompilerServices;
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
/// The signal a delivered message waits on (forge-app "@ 会话提及与授权"): a tab releases it at every turn boundary, which is the
/// single place <c>IsRunning</c> changes - the invariant a delivery's wait depends on. Driven through the composer on a real
/// runtime (scripted provider, real store, real loop), so what is asserted is the state a waiting delivery reads.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionTurnSignalTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("forge-turn-signal-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public Task A_turn_that_ends_completes_the_signal_a_delivery_waits_on() =>
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

            try
            {
                // An idle tab asks for nothing: a delivery reads this and runs its turn at once.
                Assert.True(tab.TurnEndedSignal.IsCompleted);

                tab.Draft = "first message";
                tab.SendCommand.Execute(null);
                await provider.FirstRequest.Task.WaitAsync(TimeSpan.FromMinutes(1));
                Assert.True(tab.IsRunning);

                // A delivery arriving now waits on this task; it must stay pending for exactly as long as the turn runs.
                var signal = tab.TurnEndedSignal;
                Assert.False(signal.IsCompleted);

                provider.Release();
                await signal.WaitAsync(TimeSpan.FromMinutes(1));

                Assert.False(tab.IsRunning);
                Assert.True(tab.TurnEndedSignal.IsCompleted);
            }
            finally
            {
                // A failed assertion must not leave the gated request hanging: the turn is stopped and everything released here.
                provider.Release();
                tab.Dispose();
                store.Dispose();
            }
        });

    /// <summary>Answers the first request only when the test lets it go, so the turn is genuinely in flight while it runs.</summary>
    private sealed class GatedProvider : IChatProvider
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first request arrived: the turn has started and is waiting for this answer.</summary>
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "gated";

        public void Release() => release.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            FirstRequest.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new TextDelta("done");
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                TokenUsage.Empty,
                StopReason.EndTurn);
        }
    }
}
