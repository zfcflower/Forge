using System.Runtime.CompilerServices;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Forge.Core.Storage;

namespace Forge.Core.Tests.Agents;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-agent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Path, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Provider that replays a scripted turn per call and keeps every request for assertions.</summary>
internal sealed class FakeProvider : IChatProvider
{
    private readonly Func<ChatRequest, int, CancellationToken, Task<IReadOnlyList<ChatStreamEvent>>> respond;
    private readonly List<ChatRequest> requests = [];
    private int calls;

    public FakeProvider(Func<ChatRequest, int, IReadOnlyList<ChatStreamEvent>> respond)
        : this((request, index, _) => Task.FromResult(respond(request, index)))
    {
    }

    public FakeProvider(Func<ChatRequest, int, CancellationToken, Task<IReadOnlyList<ChatStreamEvent>>> respond) =>
        this.respond = respond;

    public string Id => "fake";

    public IReadOnlyList<ChatRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int index;
        lock (requests)
        {
            requests.Add(request);
            index = calls++;
        }

        foreach (var value in await respond(request, index, ct).ConfigureAwait(false))
        {
            yield return value;
        }
    }
}

internal static class Turn
{
    public static IReadOnlyList<ChatStreamEvent> Say(string text, TokenUsage? usage = null) =>
    [
        new TextDelta(text),
        new MessageCompleted(ChatMessage.Assistant(text), usage ?? new TokenUsage(10, 5), StopReason.EndTurn),
    ];

    public static IReadOnlyList<ChatStreamEvent> Call(params (string Id, string Name, string Args)[] calls)
    {
        var content = new List<ContentBlock>(calls.Length + 1) { new TextBlock("calling") };
        foreach (var call in calls)
        {
            content.Add(new ToolCallBlock(call.Id, call.Name, call.Args));
        }

        return
        [
            new MessageCompleted(new ChatMessage(MessageRole.Assistant, content), new TokenUsage(10, 5), StopReason.ToolUse),
        ];
    }

    public static string LastUserText(ChatRequest request)
    {
        for (var index = request.Messages.Count - 1; index >= 0; index--)
        {
            if (request.Messages[index].Role == MessageRole.User)
            {
                return Text(request.Messages[index]);
            }
        }

        return string.Empty;
    }

    public static string Text(ChatMessage message)
    {
        var builder = new StringBuilder();
        foreach (var block in message.Content)
        {
            if (block is TextBlock text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    public static bool HasTool(ChatRequest request, string name) =>
        request.Tools.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
}

internal sealed class FakeTool : ITool
{
    private readonly Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> handler;

    public FakeTool(
        string name,
        ToolSideEffect sideEffect,
        Func<ToolCall, ToolContext, CancellationToken, ValueTask<ToolResult>> handler)
    {
        Definition = new ToolDefinition(name, $"Fake tool {name}.", """{"type":"object","properties":{}}""");
        SideEffect = sideEffect;
        this.handler = handler;
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect { get; }

    public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        handler(call, context, ct);

    public static FakeTool Echo(string name, ToolSideEffect sideEffect = ToolSideEffect.ReadOnly, string reply = "pong") =>
        new(name, sideEffect, (_, _, _) => new ValueTask<ToolResult>(new ToolResult(reply)));
}

/// <summary>Records how many tool bodies overlap, so a test can tell parallel from sequential execution.</summary>
internal sealed class ConcurrencyRecorder
{
    private readonly Lock gate = new();
    private readonly List<(string Name, bool Started)> timeline = [];
    private readonly Dictionary<string, int> active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> peak = new(StringComparer.Ordinal);
    private int readers;
    private int peakReaders;
    private int writers;
    private int peakWriters;

    public int PeakReaders => Volatile.Read(ref peakReaders);

    public int PeakWriters => Volatile.Read(ref peakWriters);

    public IReadOnlyList<string> Timeline
    {
        get
        {
            lock (gate)
            {
                return [.. timeline.Select(entry => string.Concat(entry.Started ? "+" : "-", entry.Name))];
            }
        }
    }

    public async Task RunAsync(string name, ToolSideEffect sideEffect, CancellationToken ct)
    {
        Enter(name, sideEffect);
        try
        {
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
        finally
        {
            Exit(name, sideEffect);
        }
    }

    private void Enter(string name, ToolSideEffect sideEffect)
    {
        lock (gate)
        {
            timeline.Add((name, true));
            active[name] = active.GetValueOrDefault(name) + 1;
            peak[name] = Math.Max(peak.GetValueOrDefault(name), active[name]);
            if (sideEffect == ToolSideEffect.ReadOnly)
            {
                readers++;
                peakReaders = Math.Max(peakReaders, readers);
            }
            else
            {
                writers++;
                peakWriters = Math.Max(peakWriters, writers);
            }
        }
    }

    private void Exit(string name, ToolSideEffect sideEffect)
    {
        lock (gate)
        {
            timeline.Add((name, false));
            active[name] = active.GetValueOrDefault(name) - 1;
            if (sideEffect == ToolSideEffect.ReadOnly)
            {
                readers--;
            }
            else
            {
                writers--;
            }
        }
    }
}

internal sealed class InMemorySessionStore : ISessionStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, SessionInfo> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StoredMessage>> messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SummaryRecord>> summaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> sequences = new(StringComparer.Ordinal);

    public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct)
    {
        var session = new SessionInfo(Guid.NewGuid().ToString("n"), workspacePath, title, DateTimeOffset.UtcNow);
        lock (gate)
        {
            sessions[session.Id] = session;
        }

        return Task.FromResult(session);
    }

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(sessions.GetValueOrDefault(sessionId));
        }
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct)
    {
        lock (gate)
        {
            IReadOnlyList<SessionInfo> all = [.. sessions.Values];
            return Task.FromResult(all);
        }
    }

    public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        lock (gate)
        {
            var seq = sequences.GetValueOrDefault(sessionId) + 1;
            sequences[sessionId] = seq;
            if (!messages.TryGetValue(sessionId, out var list))
            {
                list = [];
                messages[sessionId] = list;
            }

            list.Add(new StoredMessage(seq, message, usage, DateTimeOffset.UtcNow));
            return Task.FromResult(seq);
        }
    }

    public async IAsyncEnumerable<StoredMessage> ReadAsync(
        string sessionId,
        long fromSeq,
        long? toSeq,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        StoredMessage[] snapshot;
        lock (gate)
        {
            snapshot = messages.TryGetValue(sessionId, out var list)
                ? [.. list.Where(message => message.Seq >= fromSeq && message.Seq <= (toSeq ?? long.MaxValue))]
                : [];
        }

        foreach (var message in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(sequences.GetValueOrDefault(sessionId));
        }
    }

    public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct)
    {
        lock (gate)
        {
            if (!summaries.TryGetValue(summary.SessionId, out var list))
            {
                list = [];
                summaries[summary.SessionId] = list;
            }

            list.Add(summary);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            IReadOnlyList<SummaryRecord> found = summaries.TryGetValue(sessionId, out var list) ? [.. list] : [];
            return Task.FromResult(found);
        }
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct)
    {
        lock (gate)
        {
            var hits = new List<SearchHit>();
            if (messages.TryGetValue(sessionId, out var list))
            {
                foreach (var message in list)
                {
                    var text = MessageJson.ExtractSearchText(message.Message);
                    if (text.Contains(query, StringComparison.OrdinalIgnoreCase) && hits.Count < limit)
                    {
                        hits.Add(new SearchHit(message.Seq, text, 1d));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
        }
    }

    public IReadOnlyList<ChatMessage> Appended(string sessionId)
    {
        lock (gate)
        {
            return messages.TryGetValue(sessionId, out var list) ? [.. list.Select(message => message.Message)] : [];
        }
    }
}

internal sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (ArtifactInfo Info, byte[] Content)> items = new(StringComparer.Ordinal);

    public Task<ArtifactInfo> PutAsync(string sessionId, string name, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var info = new ArtifactInfo(Guid.NewGuid().ToString("n")[..12], sessionId, name, content.Length, DateTimeOffset.UtcNow);
        lock (gate)
        {
            items[info.Id] = (info, content.ToArray());
        }

        return Task.FromResult(info);
    }

    public Task<ArtifactInfo?> GetInfoAsync(string artifactId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(items.TryGetValue(artifactId, out var item) ? item.Info : null);
        }
    }

    public Task<int> DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        lock (gate)
        {
            var ids = items.Where(pair => pair.Value.Info.SessionId == sessionId).Select(static pair => pair.Key).ToList();
            foreach (var id in ids)
            {
                items.Remove(id);
            }

            return Task.FromResult(ids.Count);
        }
    }

    public Task<ReadOnlyMemory<byte>> ReadAsync(string artifactId, long offset, int? length, CancellationToken ct)
    {
        lock (gate)
        {
            if (!items.TryGetValue(artifactId, out var item))
            {
                throw new KeyNotFoundException($"Artifact '{artifactId}' does not exist.");
            }

            var start = (int)Math.Min(offset, item.Content.Length);
            var count = Math.Min(length ?? item.Content.Length - start, item.Content.Length - start);
            return Task.FromResult<ReadOnlyMemory<byte>>(item.Content.AsMemory(start, count));
        }
    }

    public string Text(string artifactId)
    {
        lock (gate)
        {
            return Encoding.UTF8.GetString(items[artifactId].Content);
        }
    }
}

internal static class AgentTestHost
{
    public static AgentHost Create(
        IChatProvider provider,
        IEnumerable<ITool>? tools = null,
        AgentOptions? options = null,
        PromptLayers? layers = null,
        IApprovalPolicy? policy = null,
        IApprovalHandler? handler = null,
        ISessionStore? store = null,
        IArtifactStore? artifacts = null,
        Func<string?>? dynamicContext = null,
        IEnumerable<AgentDefinition>? agents = null,
        IContextCompactor? compactor = null) =>
        new(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = tools ?? [],
            Layers = layers ?? new PromptLayers("test system", null, null),
            Options = options ?? new AgentOptions(),
            Policy = policy ?? new DefaultApprovalPolicy(),
            ApprovalHandler = handler,
            Store = store,
            Artifacts = artifacts,
            DynamicContext = dynamicContext,
            Agents = agents,
            Compactor = compactor,
        });

    public static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    public static AgentDefinition Definition(string name, ModelSettings? model = null, IReadOnlyList<string>? tools = null) =>
        new(name, $"{name} kind.", $"{name} role prompt.", model ?? new ModelSettings("default"), tools);
}
