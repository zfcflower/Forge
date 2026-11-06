using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>What one agent run produced: its final assistant text and the usage accumulated so far.</summary>
public sealed record AgentRunResult(string Text, TokenUsage Usage, StopReason Stop);

public sealed class AgentTurnLimitException(string message) : Exception(message);

/// <summary>Everything the loop needs; assembled once by <see cref="AgentHost"/> and shared by all agents.</summary>
public sealed record AgentLoopServices(
    Func<ModelSettings, IChatProvider> Providers,
    ToolSurface Tools,
    IApprovalPolicy Policy,
    IApprovalHandler? ApprovalHandler,
    ISessionStore? Store,
    ISessionCatalog? Sessions,
    IArtifactStore? Artifacts,
    IContextCompactor Compactor,
    ITokenCounter TokenCounter,
    AgentOptions Options,
    IReadOnlyList<string> SubAgentToolNames,
    Func<string?>? DynamicContext);

/// <summary>
/// Drives one agent: stream the model turn, emit deltas, persist the assistant message, approve and run
/// the tool calls, write one tool message per turn. Read-only calls of a turn run concurrently; anything
/// with a side effect runs alone, in call order. The loop also owns the host-wide bound on how many
/// agents are actively running, holding one permit per agent only while that agent does its own work.
/// A sub-agent run is steered at its tool-batch boundaries: the calls that have not started are answered
/// as skipped and the instruction becomes the next user message; pending follow-ups keep a run alive
/// where it would otherwise finish.
/// </summary>
public sealed class AgentLoop(AgentLoopServices services)
{
    private const string SkippedResult = "skipped: the agent was redirected";

    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private readonly AgentSlotPool slots = new(services.Options.MaxParallelSubAgents);

    public async Task<AgentRunResult> RunAsync(
        AgentInstance instance,
        ChatMessage input,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);

        using var slot = await AcquireAsync(instance, sink, ct).ConfigureAwait(false);
        using var scope = AgentScope.Enter(sink, instance.Id, slot);
        var control = instance.Control;
        if (control is not null && control.Admit() is { Length: > 0 } queued)
        {
            input = WithInstructions(input, queued);
        }

        var tools = new ToolSelection(
            services.Tools,
            instance.Definition.ToolNames,
            instance.Depth < services.Options.MaxSubAgentDepth,
            services.SubAgentToolNames,
            instance.Depth == 0);
        var provider = services.Providers(instance.Definition.Model);

        await PersistAsync(instance, input, null, ct).ConfigureAwait(false);

        for (var turn = 0; turn < services.Options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await CompactIfNeededAsync(instance, sink, ct).ConfigureAwait(false);

            var request = BuildRequest(instance, tools.Current().Definitions);
            ChatMessage? assistant = null;
            var usage = TokenUsage.Empty;
            var stop = StopReason.Other;
            instance.BeginText();

            await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case TextDelta text:
                        instance.AppendText(text.Text);
                        sink.Emit(new AgentTextDelta(instance.Id, text.Text));
                        break;
                    case ThinkingDelta thinking:
                        sink.Emit(new AgentThinkingDelta(instance.Id, thinking.Text));
                        break;
                    case MessageCompleted completed:
                        assistant = completed.Message;
                        usage = completed.Usage;
                        stop = completed.Stop;
                        break;
                }
            }

            if (assistant is null)
            {
                throw new ProviderException($"Provider '{provider.Id}' ended the stream without a completed message.", retryable: false);
            }

            instance.Usage += usage;
            instance.CommitText(Text(assistant));
            await PersistAsync(instance, assistant, usage, ct).ConfigureAwait(false);
            await TouchAsync(instance, ct).ConfigureAwait(false);

            var calls = ToolCalls(assistant);
            if (calls.Length == 0)
            {
                if (await ContinueWithControlAsync(instance, control, sink, ct).ConfigureAwait(false))
                {
                    continue;
                }

                sink.Emit(new AgentTurnCompleted(instance.Id, instance.Usage, stop));
                return new AgentRunResult(Text(assistant), instance.Usage, stop);
            }

            var execution = await ExecuteToolsAsync(instance, tools, calls, control, sink, ct).ConfigureAwait(false);
            await PersistAsync(instance, new ChatMessage(MessageRole.Tool, execution.Results), null, CancellationToken.None)
                .ConfigureAwait(false);
            if (execution.Steering is { } steering)
            {
                await PersistAsync(instance, ChatMessage.User(steering), null, ct).ConfigureAwait(false);
            }
        }

        throw new AgentTurnLimitException(
            $"Agent '{instance.Id}' did not finish within {services.Options.MaxTurns} turns.");
    }

    /// <summary>
    /// Takes this agent's parallelism slot, reporting <see cref="AgentSubAgentQueued"/> only when the agent has to
    /// wait for one; a root agent has no lifecycle of its own and never reports.
    /// </summary>
    private async ValueTask<AgentSlot> AcquireAsync(AgentInstance instance, IAgentEventSink sink, CancellationToken ct)
    {
        if (instance.Control is not { } control)
        {
            return await slots.AcquireAsync(ct).ConfigureAwait(false);
        }

        if (slots.TryAcquire() is { } slot)
        {
            return slot;
        }

        control.ReportQueued();
        return await slots.AcquireAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The child would otherwise finish. A pending steer is delivered first (nothing was left to skip), then the
    /// follow-ups the user queued; either one keeps the loop going instead of ending the run.
    /// </summary>
    private async ValueTask<bool> ContinueWithControlAsync(
        AgentInstance instance,
        SubAgentRun? control,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        if (control is null)
        {
            return false;
        }

        if (control.TakeSteering() is { } redirected)
        {
            sink.Emit(new AgentSubAgentSteered(instance.Id, redirected, 0, DateTimeOffset.UtcNow));
            await PersistAsync(instance, ChatMessage.User(redirected), null, ct).ConfigureAwait(false);
            return true;
        }

        if (control.TakeFollowUps() is { } followUps)
        {
            foreach (var followUp in followUps)
            {
                await PersistAsync(instance, ChatMessage.User(followUp), null, ct).ConfigureAwait(false);
            }

            return true;
        }

        return false;
    }

    private static ChatMessage WithInstructions(ChatMessage message, string instructions)
    {
        var content = new List<ContentBlock>(message.Content.Count + 1);
        var text = new StringBuilder(instructions.Length + 32);
        foreach (var block in message.Content)
        {
            if (block is TextBlock value)
            {
                text.Append(value.Text);
            }
            else
            {
                content.Add(block);
            }
        }

        if (text.Length > 0)
        {
            text.Append("\n\n");
        }

        text.Append(instructions);
        content.Add(new TextBlock(text.ToString()));
        return message with { Content = content };
    }

    private ChatRequest BuildRequest(AgentInstance instance, IReadOnlyList<ToolDefinition> definitions)
    {
        var messages = new List<ChatMessage>(instance.Messages.Count + 1);
        foreach (var entry in instance.Messages)
        {
            messages.Add(entry.Message);
        }

        var cacheThrough = messages.Count >= 2 ? messages.Count - 2 : (int?)null;
        var dynamicText = DynamicText(instance);
        if (dynamicText.Length > 0)
        {
            messages.Add(new ChatMessage(MessageRole.User, [new TextBlock(dynamicText)]));
        }

        return new ChatRequest(
            instance.Prompt.StablePrefix,
            definitions,
            messages,
            instance.Definition.Model,
            new CacheHint(CacheSystemAndTools: true, cacheThrough));
    }

    private string DynamicText(AgentInstance instance)
    {
        var suffix = instance.Prompt.DynamicSuffix;
        var callback = services.DynamicContext?.Invoke();
        if (string.IsNullOrEmpty(suffix))
        {
            return string.IsNullOrEmpty(callback) ? string.Empty : callback;
        }

        if (string.IsNullOrEmpty(callback))
        {
            return suffix;
        }

        return string.Concat(suffix, "\n\n", callback);
    }

    private async Task CompactIfNeededAsync(AgentInstance instance, IAgentEventSink sink, CancellationToken ct)
    {
        if (services.Options.CompactionThresholdTokens is not { } threshold)
        {
            return;
        }

        if (services.TokenCounter.Count(instance.History) <= threshold)
        {
            return;
        }

        var result = await services.Compactor.CompactAsync(instance, threshold, ct).ConfigureAwait(false);
        if (result is not null)
        {
            sink.Emit(new AgentCompacted(instance.Id, result.TokensBefore, result.TokensAfter));
        }
    }

    private async ValueTask<(ContentBlock[] Results, string? Steering)> ExecuteToolsAsync(
        AgentInstance instance,
        ToolSelection tools,
        ToolCallBlock[] calls,
        SubAgentRun? control,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var results = new ContentBlock?[calls.Length];
        var context = new ToolContext(instance.SessionId, instance.WorkingDirectory, instance.Depth);
        string? steering = null;
        try
        {
            var index = 0;
            while (index < calls.Length)
            {
                if (control is not null && control.TakeSteering() is { } instruction)
                {
                    steering = instruction;
                    var dropped = calls.Length - index;
                    for (var skipped = index; skipped < calls.Length; skipped++)
                    {
                        results[skipped] = new ToolResultBlock(calls[skipped].Id, SkippedResult, IsError: true);
                    }

                    sink.Emit(new AgentSubAgentSteered(instance.Id, instruction, dropped, DateTimeOffset.UtcNow));
                    break;
                }

                ct.ThrowIfCancellationRequested();
                var current = tools.Current();
                var end = index;
                while (end < calls.Length && IsReadOnly(current, calls[end]))
                {
                    end++;
                }

                if (end == index)
                {
                    results[index] = await RunOneAsync(instance, tools, calls[index], context, sink, ct).ConfigureAwait(false);
                    index++;
                }
                else
                {
                    await RunBatchAsync(instance, tools, calls, results, index, end, context, sink, ct).ConfigureAwait(false);
                    index = end;
                }
            }

            if (steering is null && control is not null && control.TakeSteering() is { } trailing)
            {
                steering = trailing;
                sink.Emit(new AgentSubAgentSteered(instance.Id, trailing, 0, DateTimeOffset.UtcNow));
            }
        }
        catch (Exception)
        {
            var fallback = ct.IsCancellationRequested ? "Cancelled." : "The tool call did not complete.";
            await PersistAsync(instance, new ChatMessage(MessageRole.Tool, Complete(calls, results, fallback)), null, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        return (Complete(calls, results, "Cancelled."), steering);
    }

    private async Task RunBatchAsync(
        AgentInstance instance,
        ToolSelection tools,
        ToolCallBlock[] calls,
        ContentBlock?[] results,
        int from,
        int to,
        ToolContext context,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var batch = new Task<ContentBlock>[to - from];
        for (var index = from; index < to; index++)
        {
            batch[index - from] = RunOneAsync(instance, tools, calls[index], context, sink, ct);
        }

        var completed = await Task.WhenAll(batch).ConfigureAwait(false);
        for (var index = from; index < to; index++)
        {
            results[index] = completed[index - from];
        }
    }

    private async Task<ContentBlock> RunOneAsync(
        AgentInstance instance,
        ToolSelection tools,
        ToolCallBlock block,
        ToolContext context,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var call = new ToolCall(block.Id, block.Name, block.ArgumentsJson);
        if (!tools.Current().TryGet(call.Name, out var tool))
        {
            return new ToolResultBlock(call.Id, $"Unknown tool '{call.Name}'.", IsError: true);
        }

        var request = new ApprovalRequest(call.Name, call.ArgumentsJson, ToolRegistry.SideEffectOf(tool, call), instance.WorkingDirectory);
        if (!await IsApprovedAsync(request, ct).ConfigureAwait(false))
        {
            sink.Emit(new AgentApprovalDenied(instance.Id, call));
            return new ToolResultBlock(call.Id, "The user denied this tool call.", IsError: true);
        }

        sink.Emit(new AgentToolStarted(instance.Id, call));
        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(call, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new ToolResult(exception.Message, IsError: true);
        }

        result = await SpillAsync(instance, tool, result, ct).ConfigureAwait(false);
        sink.Emit(new AgentToolFinished(instance.Id, call, result));
        return new ToolResultBlock(call.Id, result.Content, result.IsError);
    }

    private async ValueTask<bool> IsApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var mode = services.Policy.Evaluate(request);
        if (mode != ApprovalMode.Ask)
        {
            return mode == ApprovalMode.Allow;
        }

        if (services.ApprovalHandler is not { } handler)
        {
            return false;
        }

        await approvalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await handler.RequestAsync(request, ct).ConfigureAwait(false) == ApprovalDecision.Allow;
        }
        finally
        {
            approvalGate.Release();
        }
    }

    private async Task<ToolResult> SpillAsync(AgentInstance instance, ITool tool, ToolResult result, CancellationToken ct)
    {
        var threshold = services.Options.ArtifactThresholdChars;
        if (services.Artifacts is not { } artifacts || result.IsError || threshold <= 0 || result.Content.Length <= threshold)
        {
            return result;
        }

        if (string.Equals(tool.Definition.Name, ReadArtifactTool.Name, StringComparison.Ordinal))
        {
            return result;
        }

        var bytes = Encoding.UTF8.GetBytes(result.Content);
        var info = await artifacts
            .PutAsync(instance.SessionId, string.Concat(tool.Definition.Name, "-", instance.Id), bytes, ct)
            .ConfigureAwait(false);
        var preview = Math.Clamp(services.Options.ArtifactPreviewChars, 0, result.Content.Length);
        var builder = new StringBuilder(preview + 256);
        builder.Append(result.Content, 0, preview);
        builder.Append("\n\n[output truncated: ").Append(bytes.Length).Append(" bytes stored as artifact '").Append(info.Id)
            .Append("'. Read it with the read_artifact tool: artifact_id \"").Append(info.Id)
            .Append("\", offset, length.]");
        return new ToolResult(builder.ToString(), result.IsError);
    }

    private async Task PersistAsync(AgentInstance instance, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        var seq = services.Store is null
            ? 0
            : await services.Store.AppendAsync(instance.SessionId, message, usage, ct).ConfigureAwait(false);
        instance.Add(message, seq);
    }

    /// <summary>Best-effort activity marker for the session list; a store failure must not fail the turn.</summary>
    private async ValueTask TouchAsync(AgentInstance instance, CancellationToken ct)
    {
        if (services.Sessions is not { } sessions)
        {
            return;
        }

        try
        {
            await sessions.TouchAsync(instance.SessionId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static ContentBlock[] Complete(ToolCallBlock[] calls, ContentBlock?[] results, string fallback)
    {
        var content = new ContentBlock[calls.Length];
        for (var index = 0; index < calls.Length; index++)
        {
            content[index] = results[index] ?? new ToolResultBlock(calls[index].Id, fallback, IsError: true);
        }

        return content;
    }

    private static bool IsReadOnly(ToolRegistry tools, ToolCallBlock block) =>
        tools.TryGet(block.Name, out var tool)
        && ToolRegistry.SideEffectOf(tool, new ToolCall(block.Id, block.Name, block.ArgumentsJson)) == ToolSideEffect.ReadOnly;

    /// <summary>
    /// One run's view of the host's live tool surface. The registry is selected once and reused while the published
    /// version stays the same, so a steady run allocates nothing per request and keeps sending the identical definition
    /// list (and the identical prompt cache entry); a version change, which only a tool replacement causes, re-selects with
    /// the same allow-list and depth rules. Refreshing is lock-free: two racing callers may each select a registry, and
    /// both publish an equivalent snapshot.
    /// </summary>
    private sealed class ToolSelection(
        ToolSurface surface,
        IReadOnlyList<string>? toolNames,
        bool canSpawn,
        IReadOnlyList<string> subAgentTools,
        bool isRoot)
    {
        private Selected? selected;

        /// <summary>The registry to use right now: the cached one unless the host published a new tool set.</summary>
        public ToolRegistry Current()
        {
            var published = surface.Current;
            var cached = Volatile.Read(ref selected);
            if (cached is not null && cached.Version == published.Version)
            {
                return cached.Registry;
            }

            var registry = published.Tools.Select(toolNames, canSpawn, subAgentTools, isRoot);
            Volatile.Write(ref selected, new Selected(published.Version, registry));
            return registry;
        }

        private sealed record Selected(int Version, ToolRegistry Registry);
    }

    private static ToolCallBlock[] ToolCalls(ChatMessage message)
    {
        List<ToolCallBlock>? calls = null;
        foreach (var block in message.Content)
        {
            if (block is ToolCallBlock call)
            {
                calls ??= new List<ToolCallBlock>(2);
                calls.Add(call);
            }
        }

        return calls is null ? [] : [.. calls];
    }

    private static string Text(ChatMessage message)
    {
        StringBuilder? builder = null;
        foreach (var block in message.Content)
        {
            if (block is not TextBlock text)
            {
                continue;
            }

            builder ??= new StringBuilder(text.Text.Length + 64);
            builder.Append(text.Text);
        }

        return builder?.ToString() ?? string.Empty;
    }
}
