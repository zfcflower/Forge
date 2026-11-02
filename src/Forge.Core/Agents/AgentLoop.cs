using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>What one agent run produced: its final assistant text and the usage accumulated so far.</summary>
public sealed record AgentRunResult(string Text, TokenUsage Usage, StopReason Stop);

public sealed class AgentTurnLimitException(string message) : Exception(message);

/// <summary>Everything the loop needs; assembled once by <see cref="AgentHost"/> and shared by all agents.</summary>
public sealed record AgentLoopServices(
    Func<ModelSettings, IChatProvider> Providers,
    ToolRegistry Tools,
    IApprovalPolicy Policy,
    IApprovalHandler? ApprovalHandler,
    ISessionStore? Store,
    IArtifactStore? Artifacts,
    IContextCompactor Compactor,
    ITokenCounter TokenCounter,
    AgentOptions Options,
    string SpawnToolName,
    Func<string?>? DynamicContext);

/// <summary>
/// Drives one agent: stream the model turn, emit deltas, persist the assistant message, approve and run
/// the tool calls, write one tool message per turn. Read-only calls of a turn run concurrently; anything
/// with a side effect runs alone, in call order. The loop also owns the host-wide bound on how many
/// agents are actively running, holding one permit per agent only while that agent does its own work.
/// </summary>
public sealed class AgentLoop(AgentLoopServices services)
{
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

        using var slot = await slots.AcquireAsync(ct).ConfigureAwait(false);
        using var scope = AgentScope.Enter(sink, instance.Id, slot);
        var tools = services.Tools.Select(
            instance.Definition.ToolNames,
            instance.Depth < services.Options.MaxSubAgentDepth,
            services.SpawnToolName);
        var definitions = tools.Definitions;
        var provider = services.Providers(instance.Definition.Model);

        await PersistAsync(instance, input, null, ct).ConfigureAwait(false);

        for (var turn = 0; turn < services.Options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await CompactIfNeededAsync(instance, sink, ct).ConfigureAwait(false);

            var request = BuildRequest(instance, definitions);
            ChatMessage? assistant = null;
            var usage = TokenUsage.Empty;
            var stop = StopReason.Other;

            await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case TextDelta text:
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
            await PersistAsync(instance, assistant, usage, ct).ConfigureAwait(false);

            var calls = ToolCalls(assistant);
            if (calls.Length == 0)
            {
                sink.Emit(new AgentTurnCompleted(instance.Id, instance.Usage, stop));
                return new AgentRunResult(Text(assistant), instance.Usage, stop);
            }

            var results = await ExecuteToolsAsync(instance, tools, calls, sink, ct).ConfigureAwait(false);
            await PersistAsync(instance, new ChatMessage(MessageRole.Tool, results), null, CancellationToken.None).ConfigureAwait(false);
        }

        throw new AgentTurnLimitException(
            $"Agent '{instance.Id}' did not finish within {services.Options.MaxTurns} turns.");
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

    private async Task<ContentBlock[]> ExecuteToolsAsync(
        AgentInstance instance,
        ToolRegistry tools,
        ToolCallBlock[] calls,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var results = new ContentBlock?[calls.Length];
        var context = new ToolContext(instance.SessionId, instance.WorkingDirectory, instance.Depth);
        try
        {
            var index = 0;
            while (index < calls.Length)
            {
                var end = index;
                while (end < calls.Length && IsReadOnly(tools, calls[end].Name))
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
        }
        catch (Exception)
        {
            var fallback = ct.IsCancellationRequested ? "Cancelled." : "The tool call did not complete.";
            await PersistAsync(instance, new ChatMessage(MessageRole.Tool, Complete(calls, results, fallback)), null, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        return Complete(calls, results, "Cancelled.");
    }

    private async Task RunBatchAsync(
        AgentInstance instance,
        ToolRegistry tools,
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
        ToolRegistry tools,
        ToolCallBlock block,
        ToolContext context,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var call = new ToolCall(block.Id, block.Name, block.ArgumentsJson);
        if (!tools.TryGet(call.Name, out var tool))
        {
            return new ToolResultBlock(call.Id, $"Unknown tool '{call.Name}'.", IsError: true);
        }

        var request = new ApprovalRequest(call.Name, call.ArgumentsJson, tool.SideEffect, instance.WorkingDirectory);
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

    private static ContentBlock[] Complete(ToolCallBlock[] calls, ContentBlock?[] results, string fallback)
    {
        var content = new ContentBlock[calls.Length];
        for (var index = 0; index < calls.Length; index++)
        {
            content[index] = results[index] ?? new ToolResultBlock(calls[index].Id, fallback, IsError: true);
        }

        return content;
    }

    private static bool IsReadOnly(ToolRegistry tools, string name) =>
        tools.TryGet(name, out var tool) && tool.SideEffect == ToolSideEffect.ReadOnly;

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
