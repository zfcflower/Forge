using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The composer's running-turn commands on <see cref="SessionWorkspaceViewModel"/>: Queue keeps the draft and sends it
/// when the turn ends, Redirect stops the turn and sends right away, Stop pauses the queue until the next send, and
/// Interject reaches the running root agent at its next tool batch (or falls back to the front of the queue). The turns
/// run for real over a scripted provider whose first request waits on a gate.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ComposerQueueTests
{
    private const int PumpDelayMilliseconds = 5;
    private const string SessionId = "composer-session";

    [Fact]
    public async Task Queue_keeps_the_message_and_sends_it_when_the_turn_ends()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

            workspace.Draft = "second";
            Assert.True(workspace.QueueCommand.CanExecute(null));
            Assert.False(workspace.SendCommand.CanExecute(null));
            workspace.QueueCommand.Execute(null);

            Assert.Equal(string.Empty, workspace.Draft);
            var chip = Assert.Single(workspace.Queued);
            Assert.Equal("second", chip.Text);
            Assert.False(chip.IsInterjection);

            provider.Release();
            Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning && workspace.Queued.Count == 0));
            Assert.Equal(["first", "second"], provider.Seen);
        });
    }

    [Fact]
    public async Task A_queued_message_can_be_removed_before_it_is_sent()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

            workspace.Draft = "never sent";
            workspace.QueueCommand.Execute(null);
            workspace.Queued[0].RemoveCommand.Execute(null);
            Assert.Empty(workspace.Queued);

            provider.Release();
            Assert.True(await PumpUntilAsync(() => !workspace.IsRunning));
            await PumpAsync(40);
            Assert.Equal(1, provider.Calls);
        });
    }

    [Fact]
    public async Task A_queued_review_message_removed_before_it_is_sent_reports_the_withdrawal_once()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));
            var withdrawals = 0;
            Assert.True(workspace.TryQueueReview("review text", () => withdrawals++));
            var chip = Assert.Single(workspace.Queued);
            Assert.Equal("review text", chip.Text);
            Assert.Equal(0, withdrawals);
            chip.RemoveCommand.Execute(null);
            Assert.Empty(workspace.Queued);
            Assert.Equal(1, withdrawals);
            provider.Release();
            Assert.True(await PumpUntilAsync(() => !workspace.IsRunning));
            await PumpAsync(40);
            Assert.Equal(1, provider.Calls);
            Assert.Equal(1, withdrawals);
        });
    }

    [Fact]
    public async Task A_queued_review_message_that_is_sent_never_reports_a_withdrawal()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));
            var withdrawals = 0;
            Assert.True(workspace.TryQueueReview("review text", () => withdrawals++));
            Assert.False(workspace.TryQueueReview("   ", () => withdrawals++));
            Assert.Single(workspace.Queued);
            provider.Release();
            Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning && workspace.Queued.Count == 0));
            Assert.Equal(["first", "review text"], provider.Seen);
            Assert.Equal(0, withdrawals);
        });
    }

    [Fact]
    public async Task Redirect_stops_the_turn_and_sends_the_new_message()
    {
        await RunAsync(
            async (workspace, provider) =>
            {
                workspace.Draft = "first";
                workspace.SendCommand.Execute(null);
                Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

                workspace.Draft = "go another way";
                Assert.True(workspace.RedirectCommand.CanExecute(null));
                workspace.RedirectCommand.Execute(null);

                Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning));
                Assert.Equal("go another way", provider.Seen[^1]);
                Assert.Empty(workspace.Queued);
            },
            blockUntilCancelled: true);
    }

    [Fact]
    public async Task Stop_pauses_the_queue_until_the_next_send()
    {
        await RunAsync(
            async (workspace, provider) =>
            {
                workspace.Draft = "first";
                workspace.SendCommand.Execute(null);
                Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

                workspace.Draft = "later";
                workspace.QueueCommand.Execute(null);
                workspace.CancelCommand.Execute(null);

                Assert.True(await PumpUntilAsync(() => !workspace.IsRunning));
                await PumpAsync(40);
                Assert.Equal(1, provider.Calls);
                Assert.Single(workspace.Queued);
                Assert.True(workspace.QueuePaused);

                // An empty send resumes the queue with its head.
                Assert.True(workspace.SendCommand.CanExecute(null));
                workspace.SendCommand.Execute(null);
                Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning));
                Assert.Equal("later", provider.Seen[^1]);
                Assert.Empty(workspace.Queued);
            },
            blockUntilCancelled: true);
    }

    [Fact]
    public async Task Interject_reaches_the_running_agent_after_its_tool_batch()
    {
        await RunAsync(
            async (workspace, provider) =>
            {
                workspace.Draft = "first";
                workspace.SendCommand.Execute(null);
                Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

                workspace.Draft = "also mind the tests";
                workspace.InterjectCommand.Execute(null);
                var chip = Assert.Single(workspace.Queued);
                Assert.True(chip.IsInterjection);
                Assert.False(chip.RemoveCommand.CanExecute(null));

                provider.Release();
                Assert.True(await PumpUntilAsync(() => !workspace.IsRunning && workspace.Queued.Count == 0));
                await PumpAsync(40);

                // The second request is the same turn continuing after the tool batch, with the interjection last.
                Assert.Equal(2, provider.Calls);
                Assert.Equal("also mind the tests", provider.Seen[^1]);
            },
            firstCallsTool: true);
    }

    [Fact]
    public async Task An_interjection_the_turn_never_reached_is_sent_next()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

            // The first answer has no tool calls, so there is no batch boundary to deliver at.
            workspace.Draft = "one more thing";
            workspace.InterjectCommand.Execute(null);
            provider.Release();

            Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning && workspace.Queued.Count == 0));
            Assert.Equal("one more thing", provider.Seen[^1]);
        });
    }

    [Fact]
    public async Task Choosing_an_agent_and_effort_sets_the_root_definition_for_the_next_turn()
    {
        await RunAsync(async (workspace, provider) =>
        {
            var composer = workspace.Composer;
            Assert.Null(composer.CurrentDefinition());

            composer.SelectedAgent = composer.Agents.Single(agent => agent.Key == "Plan");
            composer.Effort = ThinkingEffort.High;
            var definition = composer.CurrentDefinition();
            Assert.NotNull(definition);
            Assert.Equal("Plan", definition.Name);
            Assert.Equal(new ModelSettings("gpt-5.1", ThinkingEffort.High, ProviderId: "test"), definition.Model);
            // The slider's position is the accepted step list's index, so a four-step model puts High third.
            Assert.Equal(3.0, composer.EffortValue);

            workspace.Draft = "plan it";
            workspace.SendCommand.Execute(null);
            provider.Release();
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && !workspace.IsRunning));
            Assert.Equal(new ModelSettings("gpt-5.1", ThinkingEffort.High, ProviderId: "test"), provider.Models[0]);

            composer.SelectedAgent = composer.Agents[0];
            composer.Effort = ThinkingEffort.Off;
            Assert.Null(composer.CurrentDefinition());
        });
    }

    [Fact]
    public async Task An_image_only_draft_sends_and_its_images_reach_the_model_and_the_transcript()
    {
        await RunAsync(async (workspace, provider) =>
        {
            Assert.False(workspace.SendCommand.CanExecute(null));
            workspace.Composer.RestoreAttachments([Image("AAAA"), Image("BBBB")]);
            Assert.True(workspace.SendCommand.CanExecute(null));

            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1));
            Assert.False(workspace.Composer.HasAttachments);
            provider.Release();
            Assert.True(await PumpUntilAsync(() => !workspace.IsRunning));

            Assert.Equal([2], provider.Images);
            Assert.Equal([string.Empty], provider.Seen);
            var user = Assert.Single(workspace.Transcript.Items.OfType<UserMessageViewModel>());
            Assert.False(user.HasText);
            Assert.Equal(2, user.Images.Count);
        });
    }

    [Fact]
    public async Task A_queued_message_keeps_its_images_until_it_is_sent()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

            workspace.Draft = "with a picture";
            workspace.Composer.RestoreAttachments([Image("AAAA")]);
            workspace.QueueCommand.Execute(null);

            var chip = Assert.Single(workspace.Queued);
            Assert.True(chip.HasAttachments);
            Assert.False(workspace.Composer.HasAttachments);

            provider.Release();
            Assert.True(await PumpUntilAsync(() => provider.Calls == 2 && !workspace.IsRunning && workspace.Queued.Count == 0));
            Assert.Equal(["first", "with a picture"], provider.Seen);
            Assert.Equal([0, 1], provider.Images);
        });
    }

    [Fact]
    public async Task An_interjection_with_an_image_is_delivered_to_the_running_turn_instead_of_queued()
    {
        await RunAsync(
            async (workspace, provider) =>
            {
                workspace.Draft = "first";
                workspace.SendCommand.Execute(null);
                Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

                workspace.Draft = "queued later";
                workspace.QueueCommand.Execute(null);
                workspace.Draft = "see this";
                workspace.Composer.RestoreAttachments([Image("AAAA")]);
                workspace.InterjectCommand.Execute(null);

                // Handed to the running turn: an interjection chip ahead of the queued message, with its image.
                Assert.Equal(2, workspace.Queued.Count);
                Assert.Equal("see this", workspace.Queued[0].Text);
                Assert.True(workspace.Queued[0].IsInterjection);
                Assert.True(workspace.Queued[0].HasAttachments);
                Assert.False(workspace.Composer.HasAttachments);

                provider.Release();
                Assert.True(await PumpUntilAsync(() => !workspace.IsRunning && workspace.Queued.Count == 0 && provider.Calls >= 3));

                // The second request continues the first turn after the tool batch, carrying the interjection and its
                // image; the queued message only starts the next turn.
                Assert.Equal("see this", provider.Seen[1]);
                Assert.Equal(1, provider.Images[1]);
                Assert.Equal("queued later", provider.Seen[2]);
                Assert.Equal(0, provider.Images[2]);
            },
            firstCallsTool: true);
    }

    [Fact]
    public async Task An_image_interjection_the_turn_never_reached_is_sent_next_with_its_image()
    {
        await RunAsync(async (workspace, provider) =>
        {
            workspace.Draft = "first";
            workspace.SendCommand.Execute(null);
            Assert.True(await PumpUntilAsync(() => provider.Calls == 1 && workspace.IsRunning));

            // The first turn ends without a tool batch, so the interjection is never delivered and comes back.
            workspace.Draft = "look";
            workspace.Composer.RestoreAttachments([Image("AAAA"), Image("BBBB")]);
            workspace.InterjectCommand.Execute(null);
            Assert.True(Assert.Single(workspace.Queued).IsInterjection);

            provider.Release();
            Assert.True(await PumpUntilAsync(() => !workspace.IsRunning && workspace.Queued.Count == 0 && provider.Calls == 2));
            Assert.Equal(["first", "look"], provider.Seen);
            Assert.Equal([0, 2], provider.Images);
        });
    }

    [Fact]
    public async Task Attachments_the_provider_cannot_carry_block_sending_until_removed()
    {
        await RunAsync(async (workspace, provider) =>
        {
            // The test provider speaks Chat Completions, which has no shape for video.
            workspace.Draft = "watch this";
            workspace.Composer.RestoreAttachments([new FileBlock("video/mp4", "AAAA", "clip.mp4")]);

            Assert.True(workspace.Composer.AttachmentsBlocked);
            Assert.False(workspace.SendCommand.CanExecute(null));
            workspace.SendCommand.Execute(null);
            await PumpAsync(5);
            Assert.Equal(0, provider.Calls);
            Assert.Equal("watch this", workspace.Draft);
            Assert.Single(workspace.Composer.Files);

            workspace.Composer.Files[0].RemoveCommand.Execute(null);
            Assert.False(workspace.Composer.AttachmentsBlocked);
            Assert.True(workspace.SendCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task Carrying_the_composer_state_keeps_draft_files()
    {
        await RunAsync(async (workspace, _) =>
        {
            var pdf = new FileBlock("application/pdf", "AAAA", "a.pdf");
            workspace.Draft = "draft";
            workspace.Composer.RestoreAttachments([Image("AAAA"), pdf]);

            var carried = workspace.TakeComposerState();
            Assert.Equal([Image("AAAA"), pdf], carried.DraftAttachments);

            workspace.RestoreComposerState(carried);
            Assert.Equal(pdf, Assert.Single(workspace.Composer.Files).Block);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Carrying_the_composer_state_keeps_the_draft_images()
    {
        await RunAsync(async (workspace, _) =>
        {
            workspace.Draft = "draft";
            workspace.Composer.RestoreAttachments([Image("AAAA")]);

            var carried = workspace.TakeComposerState();
            Assert.False(workspace.Composer.HasAttachments);
            Assert.Equal([Image("AAAA")], carried.DraftAttachments);

            workspace.RestoreComposerState(carried);
            Assert.Equal("draft", workspace.Draft);
            Assert.Equal([Image("AAAA")], workspace.Composer.Attachments.Select(static image => image.Block));
            await Task.CompletedTask;
        });
    }

    private static ImageBlock Image(string data) => new("image/png", data);

    private static async Task RunAsync(
        Func<SessionWorkspaceViewModel, GatedProvider, Task> body,
        bool blockUntilCancelled = false,
        bool firstCallsTool = false)
    {
        var root = TestTempFiles.NewDirectory("forge-composer-");
        try
        {
            var session = SharedUi.Session;
            await session.Dispatch(
                async () =>
                {
                    var provider = new GatedProvider(blockUntilCancelled, firstCallsTool);
                    var store = new FakeSessionStore();
                    var info = new SessionInfo(SessionId, root, "composer", DateTimeOffset.UnixEpoch);
                    using var runtime = new SessionRuntime(
                        new AgentRuntimeServices(
                            _ => provider,
                            HostClient: null,
                            store,
                            new EmptySessionCatalog(DateTimeOffset.UnixEpoch),
                            new EmptyTodoStore("t1", DateTimeOffset.UnixEpoch),
                            new ModelSettings("gpt-5.1")),
                        info);
                    using var workspace = new SessionWorkspaceViewModel(
                        info,
                        runtime,
                        checkpoints: null,
                        store,
                        ConfiguredProviders(root),
                        composerPreferences: new ComposerPreferences(),
                        fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
                        skillSource: () => []);
                    await body(workspace, provider);
                    return true;
                },
                CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>One configured provider serving the scripted model, so the composer has a model and no missing-provider notice.</summary>
    private static ProviderServices ConfiguredProviders(string root)
    {
        var settingsPath = Path.Combine(root, "composer-providers.json");
        File.WriteAllText(
            settingsPath,
            """
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
                    "baseUrl": "https://example.invalid",
                    "auth": { "kind": "api-key" },
                    "models": ["gpt-5.1"],
                    "defaultModel": "gpt-5.1"
                  }
                ]
              }
            }
            """);
        var dataDir = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(dataDir);
        // Fake handler, never used for network here; the workspace does not own it so it is left for reclamation.
        return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
    }

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition, 600);

    private static async Task PumpAsync(int iterations)
    {
        for (var i = 0; i < iterations; i++)
        {
            await Task.Delay(PumpDelayMilliseconds);
        }
    }

    /// <summary>
    /// Answers every request with one text message; the first request waits for <see cref="Release"/> (or, with
    /// <c>blockUntilCancelled</c>, until the turn is cancelled), and with <c>firstCallsTool</c> it asks for an unknown
    /// tool so the turn has a tool batch to continue after.
    /// </summary>
    private sealed class GatedProvider(bool blockUntilCancelled, bool firstCallsTool) : IChatProvider
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> seen = new();
        private readonly ConcurrentQueue<ModelSettings> models = new();
        private readonly ConcurrentQueue<int> images = new();
        private int calls;

        public string Id => "gated";

        public int Calls => Volatile.Read(ref calls);

        public IReadOnlyList<string> Seen => [.. seen];

        public IReadOnlyList<ModelSettings> Models => [.. models];

        /// <summary>How many images the last user message of each request carried.</summary>
        public IReadOnlyList<int> Images => [.. images];

        public void Release() => gate.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var last = request.Messages.LastOrDefault(message => message.Role == MessageRole.User);
            seen.Enqueue(last is null ? string.Empty : string.Concat(last.Content.OfType<TextBlock>().Select(block => block.Text)));
            models.Enqueue(request.Model);
            images.Enqueue(last is null ? 0 : last.Content.OfType<ImageBlock>().Count());
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                if (blockUntilCancelled)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                else
                {
                    await gate.Task.WaitAsync(ct);
                }

                if (firstCallsTool)
                {
                    var toolCall = new ToolCallBlock("call-1", "missing_tool", "{}");
                    yield return new MessageCompleted(
                        new ChatMessage(MessageRole.Assistant, [toolCall]),
                        new TokenUsage(1, 1),
                        StopReason.ToolUse);
                    yield break;
                }
            }

            yield return new TextDelta("ok");
            yield return new MessageCompleted(ChatMessage.Assistant("ok"), new TokenUsage(1, 1), StopReason.EndTurn);
        }
    }

    /// <summary>In-memory message log; the checkpoints own the assertions, so the log only has to behave like a store.</summary>
    private sealed class FakeSessionStore : ISessionStore, ISessionCatalog
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, List<StoredMessage>> log = new(StringComparer.Ordinal);

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", workspacePath, title, DateTimeOffset.UnixEpoch));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
        {
            lock (gate)
            {
                if (!log.TryGetValue(sessionId, out var messages))
                {
                    messages = [];
                    log[sessionId] = messages;
                }

                messages.Add(new StoredMessage(messages.Count + 1, message, usage, DateTimeOffset.UnixEpoch));
                return Task.FromResult((long)messages.Count);
            }
        }

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            ReadCoreAsync(sessionId, fromSeq, toSeq);

        private async IAsyncEnumerable<StoredMessage> ReadCoreAsync(string sessionId, long fromSeq, long? toSeq)
        {
            List<StoredMessage> snapshot;
            lock (gate)
            {
                snapshot = log.TryGetValue(sessionId, out var messages) ? [.. messages] : [];
            }

            foreach (var message in snapshot)
            {
                if (message.Seq >= fromSeq && (toSeq is null || message.Seq <= toSeq))
                {
                    yield return message;
                }
            }

            await Task.Yield();
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult(log.TryGetValue(sessionId, out var messages) ? (long)messages.Count : 0L);
            }
        }

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

}
