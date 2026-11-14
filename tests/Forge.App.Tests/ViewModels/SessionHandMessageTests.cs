using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="SessionWorkspaceViewModel.HandMessageAsync"/>, the one hand-off the checks panel and a Mermaid block share:
/// an idle session runs the message as a turn; a busy one, a turn that was stopped (withdrawn) and a turn that threw all
/// leave the message in the composer's draft - appended after what the user already typed, never replacing it.
/// The turns run for real (an agent host over a scripted provider and in-memory stores).
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionHandMessageTests
{
    private const string Message = "fix the diagram";
    private static readonly LocText Origin = LocText.Literal("hand-off test");

    [Fact]
    public Task An_idle_session_runs_the_message_as_a_turn_and_leaves_the_draft_alone() => RunAsync(
        gated: false,
        async (workspace, _) =>
        {
            workspace.Draft = "unsent";

            var ran = await workspace.HandMessageAsync(Message, Origin);

            Assert.True(ran);
            Assert.Equal("unsent", workspace.Draft);
        });

    [Fact]
    public Task A_busy_session_takes_the_message_into_an_empty_draft_without_a_second_turn() => RunAsync(
        gated: true,
        async (workspace, provider) =>
        {
            var running = workspace.RunAutomationTurnAsync("first", Origin, agent: null);
            await UntilAsync(() => !workspace.IsAutomationIdle);

            var ran = await workspace.HandMessageAsync(Message, Origin);

            Assert.False(ran);
            Assert.Equal(Message, workspace.Draft);
            provider.Release();
            await running;
            Assert.Equal(1, provider.Calls);
        });

    [Fact]
    public Task A_busy_session_appends_the_message_after_the_users_draft() => RunAsync(
        gated: true,
        async (workspace, provider) =>
        {
            var running = workspace.RunAutomationTurnAsync("first", Origin, agent: null);
            await UntilAsync(() => !workspace.IsAutomationIdle);
            workspace.Draft = "half a thought";

            var ran = await workspace.HandMessageAsync(Message, Origin);

            Assert.False(ran);
            Assert.Equal(ComposerDraft.Append("half a thought", Message), workspace.Draft);
            Assert.StartsWith("half a thought", workspace.Draft, StringComparison.Ordinal);
            provider.Release();
            await running;
        });

    [Fact]
    public Task A_turn_that_was_stopped_is_withdrawn_and_the_message_goes_to_the_draft() => RunAsync(
        gated: true,
        async (workspace, _) =>
        {
            var handing = workspace.HandMessageAsync(Message, Origin);
            await UntilAsync(() => !workspace.IsAutomationIdle);

            workspace.Cancel();
            var ran = await handing;

            Assert.False(ran);
            Assert.Equal(Message, workspace.Draft);
        });

    [Fact]
    public Task A_turn_that_throws_keeps_the_message_in_the_draft_after_what_the_user_typed() => RunAsync(
        gated: false,
        async (workspace, _) =>
        {
            workspace.Draft = "typed first";
            workspace.TurnStarted += static (_, _) => throw new InvalidOperationException("boom");

            var ran = await workspace.HandMessageAsync(Message, Origin);

            Assert.False(ran);
            Assert.Equal(ComposerDraft.Append("typed first", Message), workspace.Draft);
        });

    [Fact]
    public Task A_blank_message_is_refused() => RunAsync(
        gated: false,
        async (workspace, _) =>
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => workspace.HandMessageAsync("  ", Origin));
            Assert.Equal(string.Empty, workspace.Draft);
        });

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 600 && !condition(); attempt++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private static async Task RunAsync(bool gated, Func<SessionWorkspaceViewModel, GatedProvider, Task> body)
    {
        var root = TestTempFiles.NewDirectory("forge-hand-message-");
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var git = new GitCli();
            Assert.True((await git.RunAsync(repository, ["init"], CancellationToken.None)).Success);

            await SharedUi.Session.Dispatch(
                async () =>
                {
                    var provider = new GatedProvider(gated);
                    var store = new MemorySessionStore();
                    var info = new SessionInfo("hand-message-session", repository, "hand-off", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
                    var runtime = new SessionRuntime(
                        new AgentRuntimeServices(
                            _ => provider,
                            HostClient: null,
                            store,
                            store,
                            new EmptyTodoStore("t1", DateTimeOffset.UnixEpoch),
                            new ModelSettings("scripted-model")),
                        info);
                    var workspace = new SessionWorkspaceViewModel(
                        info,
                        runtime,
                        new TurnCheckpointService(git, new EmptyCheckpointStore()),
                        store,
                        TestProviderServices.Unconfigured(root));

                    await body(workspace, provider);
                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    /// <summary>Answers at once, or - gated - holds the turn open until released or cancelled.</summary>
    private sealed class GatedProvider(bool gated) : IChatProvider
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public string Id => "gated";

        public void Release() => release.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            if (gated)
            {
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }
}
