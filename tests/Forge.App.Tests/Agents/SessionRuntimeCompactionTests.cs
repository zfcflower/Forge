using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// The <c>compaction</c> settings apply to a live session: a save in the settings store reaches the host of a session that
/// already ran (no new session needed), the next turn's context report uses the new trigger line, and the settings read
/// before each turn win over a host built under older ones.
/// </summary>
public sealed class SessionRuntimeCompactionTests : IDisposable
{
    // A 200k window with no output limit reserves 20%: U = 160k.
    private const int Window = 200_000;
    private const int Usable = 160_000;

    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-runtime-compaction-" + Guid.NewGuid().ToString("N"));

    public SessionRuntimeCompactionTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    [Fact]
    public async Task A_settings_save_reaches_a_running_session_and_its_next_request()
    {
        var settings = new CompactionSettingsStore(Path.Combine(root, "settings.json"));
        await using var rig = new Rig(root, services => services.CompactionChanges = settings);

        var before = await rig.TurnAsync();
        Assert.Equal(0.85, rig.Runtime.CompactionPolicy!.TriggerRatio);
        Assert.Equal((Usable, (int)(Usable * 0.85)), (before.UsableTokens!.Value, before.TriggerTokens!.Value));

        settings.Save(new CompactionSettings(Enabled: false, TriggerPercent: 70, TargetPercent: 40));

        // Applied at once, without another turn: a turn running now would read it before its next model request.
        var applied = rig.Runtime.CompactionPolicy!;
        Assert.False(applied.Enabled);
        Assert.Equal(0.70, applied.TriggerRatio);
        Assert.Equal(0.40, applied.TargetRatio);
        Assert.NotNull(applied.ContextWindow); // the resolvers survive the swap

        var after = await rig.TurnAsync();
        Assert.Equal((int)(Usable * 0.70), after.TriggerTokens);

        // The prefix is untouched: both turns sent the same system prompt and tools, so the prompt cache still hits.
        var requests = rig.Provider.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].SystemPrompt, requests[1].SystemPrompt);
        Assert.Equal(requests[0].Tools.Select(static tool => tool.Name), requests[1].Tools.Select(static tool => tool.Name));
    }

    [Fact]
    public async Task Settings_read_before_each_turn_replace_the_policy_the_host_was_built_with()
    {
        var current = new CompactionSettings(Enabled: true, TriggerPercent: 90, TargetPercent: 50);
        await using var rig = new Rig(root, services => services.Compaction = () => current);

        var first = await rig.TurnAsync();
        Assert.Equal((int)(Usable * 0.90), first.TriggerTokens);
        var built = rig.Runtime.CompactionPolicy;

        // Unchanged settings keep the very same policy instance: nothing is re-applied.
        await rig.TurnAsync();
        Assert.Same(built, rig.Runtime.CompactionPolicy);

        current = new CompactionSettings(Enabled: true, TriggerPercent: 60, TargetPercent: 30);
        var changed = await rig.TurnAsync();
        Assert.Equal((int)(Usable * 0.60), changed.TriggerTokens);
        Assert.Equal(0.30, rig.Runtime.CompactionPolicy!.TargetRatio);
    }

    [Fact]
    public async Task A_save_before_the_first_turn_or_after_disposal_is_harmless()
    {
        var settings = new CompactionSettingsStore(Path.Combine(root, "settings.json"));
        var rig = new Rig(root, services => services.CompactionChanges = settings);
        try
        {
            // No host yet: nothing to apply, and the host is later built from the saved value.
            settings.Save(new CompactionSettings(Enabled: true, TriggerPercent: 75, TargetPercent: 50));
            Assert.Null(rig.Runtime.CompactionPolicy);
            var first = await rig.TurnAsync();
            Assert.Equal((int)(Usable * 0.75), first.TriggerTokens);
        }
        finally
        {
            await rig.DisposeAsync();
        }

        // The disposed session no longer listens.
        settings.Save(new CompactionSettings(Enabled: true, TriggerPercent: 80, TargetPercent: 50));
        Assert.Null(rig.Runtime.CompactionPolicy);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteSessionStore store;
        private readonly SqliteTodoStore todos;
        private bool disposed;

        public Rig(string root, Action<AgentRuntimeServices> configure)
        {
            var data = Path.Combine(root, "data-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "ws");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(workspace);
            store = new SqliteSessionStore(data);
            todos = new SqliteTodoStore(data);
            var services = new AgentRuntimeServices(
                _ => Provider,
                HostClient: null,
                store,
                store,
                todos,
                new ModelSettings("main-model"))
            {
                ContextWindow = static _ => Window,
            };
            configure(services);
            Runtime = new SessionRuntime(services, new SessionInfo("s1", workspace, "t", DateTimeOffset.UnixEpoch));
        }

        public ScriptedProvider Provider { get; } = new();

        public SessionRuntime Runtime { get; }

        /// <summary>Runs one turn and returns the root's last context report of it.</summary>
        public async Task<AgentContextMeasured> TurnAsync()
        {
            var events = new ConcurrentQueue<AgentEvent>();
            await Runtime.RunTurnAsync("hello", events.Enqueue, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Empty(events.OfType<AgentFailed>());
            return events.OfType<AgentContextMeasured>().Last(static e => e.AgentId == "root");
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Runtime.Dispose();
            await store.DisposeAsync();
            await todos.DisposeAsync();
        }
    }

    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly ConcurrentQueue<ChatRequest> queue = new();

        public IReadOnlyList<ChatRequest> Requests => [.. queue];

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            queue.Enqueue(request);
            await Task.Yield();
            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }
}
