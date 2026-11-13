using System.Diagnostics;
using System.Text;
using Forge.Abstractions;
using Forge.Core.Prompts;

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
    Func<string?>? DynamicContext,
    Action<FirstRequestCacheReport>? CacheDiagnostics = null);

/// <summary>
/// Usage of the first model request an instance made in this process, with how its session's context began. It answers
/// whether an inherited context really hit the provider cache and whether a proxy forwards <c>cache_control</c>.
/// </summary>
public sealed record FirstRequestCacheReport(
    string SessionId,
    string AgentId,
    SessionOrigin? Origin,
    string ProviderId,
    string Model,
    TokenUsage Usage)
{
    /// <summary>The line written to the diagnostics trace: a fixed tag and key=value pairs.</summary>
    public override string ToString() =>
        string.Concat(
            "[cache] first-request session=", SessionId,
            " agent=", AgentId,
            " origin=", Origin?.ToString() ?? "unknown",
            " provider=", ProviderId,
            " model=", Model,
            " input=", Usage.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " cache_read=", Usage.CacheReadTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " cache_write=", Usage.CacheWriteTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " output=", Usage.OutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

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
    private CompactionPolicy compactionPolicy = services.Options.Compaction;

    /// <summary>
    /// The compaction policy in force. It starts as <see cref="AgentOptions.Compaction"/> and is replaced through
    /// <see cref="AgentHost.SetCompactionPolicy"/>; every decision reads it once, before the request it is about, so a change
    /// reaches a running loop at its next model request. It never touches the request itself, so the prompt cache holds.
    /// </summary>
    internal CompactionPolicy CurrentCompaction
    {
        get => Volatile.Read(ref compactionPolicy);
        set => Volatile.Write(ref compactionPolicy, value);
    }

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

        await ResolveCacheStateAsync(instance, ct).ConfigureAwait(false);
        await PersistAsync(instance, input, null, ct).ConfigureAwait(false);

        for (var turn = 0; turn < services.Options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await CompactIfNeededAsync(instance, tools, sink, ct).ConfigureAwait(false);

            ChatMessage? assistant = null;
            var usage = TokenUsage.Empty;
            var stop = StopReason.Other;
            var messagesAtRequest = 0;
            var versionAtRequest = 0;
            var overflowRetried = false;
            while (true)
            {
                var registry = tools.Current();
                await RecordFingerprintAsync(instance, registry, ct).ConfigureAwait(false);
                var request = BuildRequest(instance, registry.Definitions);
                messagesAtRequest = instance.Messages.Count;
                versionAtRequest = instance.HistoryVersion;
                instance.BeginText();
                var received = false;
                try
                {
                    await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
                    {
                        received = true;
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
                                if (completed.RateLimit is { } limit)
                                {
                                    sink.Emit(new AgentRateLimitUpdated(instance.Id, limit));
                                }

                                if (!string.IsNullOrEmpty(completed.Notice))
                                {
                                    sink.Emit(new AgentNotice(instance.Id, completed.Notice));
                                }

                                break;
                        }
                    }
                }
                catch (Exception exception) when (!overflowRetried
                                                  && !received
                                                  && !ct.IsCancellationRequested
                                                  && Providers.ProviderErrors.IsContextOverflow(exception))
                {
                    // Overflow fallback: the window was misjudged (or unknown). Compact once and resend the same turn once;
                    // when nothing could be compacted, or the resend overflows again, the provider error stands.
                    overflowRetried = true;
                    if (!await CompactAfterOverflowAsync(instance, tools, sink, ct).ConfigureAwait(false))
                    {
                        throw;
                    }

                    continue;
                }

                break;
            }

            if (assistant is null)
            {
                throw new ProviderException($"Provider '{provider.Id}' ended the stream without a completed message.", retryable: false);
            }

            RecordPromptUsage(instance, usage, messagesAtRequest, versionAtRequest);
            instance.Usage += usage;
            ReportFirstRequest(instance, provider, usage);
            instance.CommitText(Text(assistant));
            await PersistAsync(instance, assistant, usage, ct).ConfigureAwait(false);
            ReportContext(instance, sink, ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter)));
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

            await DeliverInterjectionsAsync(instance, sink).ConfigureAwait(false);
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

    /// <summary>
    /// Hands the user's interjections to the model at a tool-batch boundary: all pending ones become one user message after
    /// the batch's results (typed texts first, then every attachment in order; see <see cref="Interjection.Merge"/>), and
    /// each is reported so the UI can show where it landed. Nothing is skipped.
    /// </summary>
    private async ValueTask DeliverInterjectionsAsync(AgentInstance instance, IAgentEventSink sink)
    {
        if (!instance.HasInterjections)
        {
            return;
        }

        var taken = instance.TakeInterjections();
        if (taken.Count == 0)
        {
            return;
        }

        var at = DateTimeOffset.UtcNow;
        foreach (var interjection in taken)
        {
            sink.Emit(new AgentInterjected(instance.Id, interjection.Text, at, interjection.Attachments));
        }

        await PersistAsync(instance, Interjection.Merge(taken), null, CancellationToken.None).ConfigureAwait(false);
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
        var cacheTtl = ResolveCacheTtl(instance);
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
            new CacheHint(CacheSystemAndTools: true, cacheThrough, cacheTtl))
        {
            CacheRoutingKey = instance.CacheState.RoutingKey ?? instance.SessionId,
            SessionId = instance.SessionId,
        };
    }

    /// <summary>
    /// The prompt-cache lifetime for this request: an explicit setting, or <c>"auto"</c> — the short lifetime until this instance
    /// has actually paused longer than its expiry, then the hour for the rest of the instance.
    /// </summary>
    private string ResolveCacheTtl(AgentInstance instance)
    {
        var configured = services.Options.CacheTtl;
        if (string.Equals(configured, "1h", StringComparison.OrdinalIgnoreCase))
        {
            instance.NoteCacheTtl("1h");
            return "1h";
        }

        if (string.Equals(configured, "5m", StringComparison.OrdinalIgnoreCase))
        {
            instance.NoteCacheTtl("5m");
            return "5m";
        }

        var gap = instance.TakeRequestGap(services.Options.CacheTtlClock.GetUtcNow());
        if (gap is { } paused && paused >= services.Options.CacheTtlPromotionGap)
        {
            instance.PromoteCacheTtl();
        }

        var ttl = instance.CacheTtlInUse ?? "5m";
        instance.NoteCacheTtl(ttl);
        return ttl;
    }

    /// <summary>
    /// Reads the session row once per instance for the cache routing key (the lineage root, so a handover or fork keeps
    /// routing to the same cache shard) and the origin for the first-request report. Best effort: a missing row or a store
    /// failure falls back to the session id.
    /// </summary>
    private async ValueTask ResolveCacheStateAsync(AgentInstance instance, CancellationToken ct)
    {
        var state = instance.CacheState;
        if (state.Resolved)
        {
            return;
        }

        if (services.Store is not { } store)
        {
            state.Resolved = true;
            return;
        }

        try
        {
            if (await store.GetSessionAsync(instance.SessionId, ct).ConfigureAwait(false) is { } session)
            {
                state.RoutingKey = string.IsNullOrEmpty(session.LineageRootId) ? session.Id : session.LineageRootId;
                state.Origin = session.Origin;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }

        state.Resolved = true;
    }

    /// <summary>
    /// <c>prefix_fingerprint</c> of what this request sends ahead of the messages. Recomputed only when the run's tool
    /// registry is a different instance (a published tool change); written back only when the value changed, best effort.
    /// </summary>
    private async ValueTask RecordFingerprintAsync(AgentInstance instance, ToolRegistry registry, CancellationToken ct)
    {
        var state = instance.CacheState;
        if (ReferenceEquals(state.FingerprintTools, registry))
        {
            return;
        }

        state.FingerprintTools = registry;
        var fingerprint = PromptFingerprint.Compute(instance.Prompt.StablePrefix, registry.Definitions);
        if (string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        state.Fingerprint = fingerprint;
        if (services.Sessions is not { } sessions)
        {
            return;
        }

        try
        {
            await sessions.SetPrefixFingerprintAsync(instance.SessionId, fingerprint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // NotSupportedException from a catalog without the lifecycle members, or a store failure: the fingerprint
            // only feeds a hint in the handover dialog and must not fail the turn.
        }
    }

    private void ReportFirstRequest(AgentInstance instance, IChatProvider provider, TokenUsage usage)
    {
        var state = instance.CacheState;
        if (state.FirstRequestReported)
        {
            return;
        }

        state.FirstRequestReported = true;
        var report = new FirstRequestCacheReport(
            instance.SessionId,
            instance.Id,
            state.Origin,
            provider.Id,
            instance.Definition.Model.Model,
            usage);
        try
        {
            if (services.CacheDiagnostics is { } diagnostics)
            {
                diagnostics(report);
            }
            else
            {
                Trace.WriteLine(report.ToString());
            }
        }
        catch (Exception)
        {
        }
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

    /// <summary>
    /// Automatic compaction before a request: C ≥ trigger·U starts it and it aims at target·U, where U is the usable input
    /// budget of the instance's own model. An attempt that leaves C at or above the trigger line (for example an inherited
    /// prefix that alone exceeds it) is fruitless: the next attempt waits until the instance's own content grew by
    /// 0.1·U, and after two consecutive fruitless attempts automatic compaction stops until a manual one or another model.
    /// </summary>
    private async Task CompactIfNeededAsync(AgentInstance instance, ToolSelection tools, IAgentEventSink sink, CancellationToken ct)
    {
        var policy = CurrentCompaction;
        if (!policy.Enabled || services.Compactor is NoopCompactor)
        {
            return;
        }

        var state = SyncModel(instance);
        if (state.Stalled || policy.BudgetFor(instance.Definition.Model) is not { } budget)
        {
            return;
        }

        var trigger = budget.TriggerTokens(policy.TriggerRatio);
        var current = ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter));
        if (current < trigger)
        {
            return;
        }

        if (state.FruitlessBaseline is { } baseline && OwnTokens(instance) < (long)baseline + budget.RegrowthTokens)
        {
            return;
        }

        var target = budget.TargetTokens(policy.TargetRatio);
        var result = await TryCompactAsync(
            instance,
            new CompactionRequest(target, KeepRecent(budget.KeepRecentTokens, target), CompactionReason.Auto),
            ct).ConfigureAwait(false);
        var after = current;
        if (result is not null)
        {
            sink.Emit(new AgentCompacted(instance.Id, result.TokensBefore, result.TokensAfter));
            after = ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter));
            sink.Emit(new AgentContextMeasured(instance.Id, after, budget.Usable, trigger));
            if (after < trigger)
            {
                state.ResetHysteresis();
                return;
            }
        }

        state.FruitlessAttempts++;
        state.FruitlessBaseline = OwnTokens(instance);
        if (state.FruitlessAttempts >= CompactionPolicy.MaxFruitlessAttempts)
        {
            state.Stalled = true;
            sink.Emit(new AgentCompactionStalled(instance.Id, after, trigger));
        }
    }

    /// <summary>Forced compaction after the provider rejected the request as too long; false when nothing changed.</summary>
    private async Task<bool> CompactAfterOverflowAsync(AgentInstance instance, ToolSelection tools, IAgentEventSink sink, CancellationToken ct)
    {
        var policy = CurrentCompaction;
        if (!policy.Enabled || services.Compactor is NoopCompactor)
        {
            return false;
        }

        SyncModel(instance);
        var current = ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter));
        var target = Math.Max(1, (int)(current * policy.TargetRatio));
        var keepRecent = CompactionPolicy.MaxKeepRecentTokens;
        if (policy.BudgetFor(instance.Definition.Model) is { } budget)
        {
            target = Math.Min(target, budget.TargetTokens(policy.TargetRatio));
            keepRecent = budget.KeepRecentTokens;
        }

        var result = await TryCompactAsync(
            instance,
            new CompactionRequest(target, KeepRecent(keepRecent, target), CompactionReason.Overflow),
            ct).ConfigureAwait(false);
        if (result is null)
        {
            return false;
        }

        sink.Emit(new AgentCompacted(instance.Id, result.TokensBefore, result.TokensAfter));
        ReportContext(instance, sink, ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter)));
        return true;
    }

    /// <summary>
    /// The automatic and overflow paths keep at most half their target verbatim, so a low target is still reachable; a
    /// summary that could not be produced counts as no compaction (fruitless for the automatic path).
    /// </summary>
    private static int KeepRecent(int keepRecent, int target) => Math.Max(1, Math.Min(keepRecent, target / 2));

    private async Task<CompactionResult?> TryCompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct)
    {
        try
        {
            return await services.Compactor.CompactAsync(instance, request, ct).ConfigureAwait(false);
        }
        catch (CompactionFailedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Manual compaction: no threshold and regardless of <see cref="CompactionPolicy.Enabled"/>; everything but the newest
    /// keepRecent = min(20 000, 0.2·U) tokens (20 000 when the window is unknown) is summarized, with
    /// <paramref name="instructions"/> as additional focus, and automatic compaction is re-enabled for the instance. A
    /// summary that could not be produced throws <see cref="CompactionFailedException"/>. Must not run while a turn of the
    /// same instance does.
    /// </summary>
    internal async Task<CompactionResult?> CompactNowAsync(AgentInstance instance, IAgentEventSink? sink, string? instructions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var state = SyncModel(instance);
        state.ResetHysteresis();
        if (services.Compactor is NoopCompactor)
        {
            return null;
        }

        var keepRecent = CurrentCompaction.BudgetFor(instance.Definition.Model)?.KeepRecentTokens
                         ?? CompactionPolicy.MaxKeepRecentTokens;
        var result = await services.Compactor.CompactAsync(
            instance,
            new CompactionRequest(1, keepRecent, CompactionReason.Manual, string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim()),
            ct).ConfigureAwait(false);
        if (result is not null && sink is not null)
        {
            sink.Emit(new AgentCompacted(instance.Id, result.TokensBefore, result.TokensAfter));
            // Outside a turn there is no tool selection: the count the last run cached stands in (0 before any run).
            ReportContext(instance, sink, ContextTokens(instance, instance.Compaction.ToolTokens));
        }

        return result;
    }

    /// <summary>
    /// Reports C (<see cref="ContextTokens"/>) against the instance's usable budget U and trigger line under the policy in
    /// force: after every model request and after every compaction, so a context gauge follows the real prompt size. The
    /// budget is null when the model's window is unknown.
    /// </summary>
    private void ReportContext(AgentInstance instance, IAgentEventSink sink, int contextTokens)
    {
        var policy = CurrentCompaction;
        var budget = policy.BudgetFor(instance.Definition.Model);
        sink.Emit(new AgentContextMeasured(
            instance.Id,
            contextTokens,
            budget?.Usable,
            budget?.TriggerTokens(policy.TriggerRatio))
        {
            CacheTtl = instance.CacheTtlInUse,
        });
    }

    /// <summary>
    /// Current context size C: the last real prompt size of this instance on this model plus an estimate of what was
    /// appended since that request; the full estimate (messages, stable prefix and the <paramref name="toolTokens"/> of the
    /// tool definitions the request carries) when no real usage applies. Real usage already includes the definitions.
    /// </summary>
    internal int ContextTokens(AgentInstance instance, int toolTokens)
    {
        var state = instance.Compaction;
        var messages = instance.Messages;
        if (state.LastPromptTokens > 0
            && state.ModelKey == ModelKey(instance.Definition.Model)
            && state.HistoryVersionAtRequest == instance.HistoryVersion
            && state.MessagesAtRequest <= messages.Count)
        {
            return (int)Math.Min(int.MaxValue, (long)state.LastPromptTokens + Estimate(messages, state.MessagesAtRequest, ownOnly: false));
        }

        return (int)Math.Min(
            int.MaxValue,
            (long)Estimate(messages, 0, ownOnly: false) + services.TokenCounter.Count(instance.Prompt.StablePrefix) + Math.Max(0, toolTokens));
    }

    /// <summary>Per-definition overhead of the wire wrapper (type, name and parameter keys) beyond the counted text.</summary>
    internal const int ToolDefinitionOverheadTokens = 8;

    /// <summary>Estimated tokens of <paramref name="definitions"/>: name, description and schema text plus a small wrapper each.</summary>
    internal static int DefinitionTokens(IReadOnlyList<ToolDefinition> definitions, ITokenCounter counter)
    {
        long total = 0;
        foreach (var definition in definitions)
        {
            total += (long)counter.Count(definition.Name)
                     + counter.Count(definition.Description)
                     + counter.Count(definition.InputSchemaJson)
                     + ToolDefinitionOverheadTokens;
        }

        return (int)Math.Min(int.MaxValue, total);
    }

    private int OwnTokens(AgentInstance instance) => Estimate(instance.Messages, 0, ownOnly: true);

    private int Estimate(List<AgentMessage> messages, int from, bool ownOnly)
    {
        if (from >= messages.Count)
        {
            return 0;
        }

        var slice = new List<ChatMessage>(messages.Count - from);
        for (var index = from; index < messages.Count; index++)
        {
            if (!ownOnly || !messages[index].Inherited)
            {
                slice.Add(messages[index].Message);
            }
        }

        return slice.Count == 0 ? 0 : services.TokenCounter.Count(slice);
    }

    /// <summary>Usage recorded for another model no longer applies, and neither does that model's hysteresis.</summary>
    private static CompactionState SyncModel(AgentInstance instance)
    {
        var state = instance.Compaction;
        var key = ModelKey(instance.Definition.Model);
        if (state.ModelKey != key)
        {
            state.ModelKey = key;
            state.ForgetUsage();
            state.ResetHysteresis();
        }

        return state;
    }

    private static void RecordPromptUsage(AgentInstance instance, TokenUsage usage, int messagesAtRequest, int versionAtRequest)
    {
        // InputTokens already counts cache reads and writes in every adapter (Anthropic adds them up, OpenAI's prompt_tokens
        // includes cached tokens), so it is the whole prompt; adding the cache fields again would count them twice.
        var state = SyncModel(instance);
        state.LastPromptTokens = Math.Max(0, usage.InputTokens);
        state.MessagesAtRequest = messagesAtRequest;
        state.HistoryVersionAtRequest = versionAtRequest;
    }

    private static string ModelKey(ModelSettings model) => string.Concat(model.ProviderId, "\u001f", model.Model);

    private async ValueTask<(ContentBlock[] Results, string? Steering)> ExecuteToolsAsync(
        AgentInstance instance,
        ToolSelection tools,
        ToolCallBlock[] calls,
        SubAgentRun? control,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var results = new ContentBlock?[calls.Length];
        var context = new ToolContext(instance.SessionId, instance.WorkingDirectory, instance.Depth)
        {
            AgentId = instance.Id,
            AgentName = instance.Definition.Name,
        };
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
        public ToolRegistry Current() => CurrentSelection().Registry;

        /// <summary>
        /// Estimated tokens of the current registry's definitions, counted once per published version and instance (the
        /// instance's tool selection is fixed, and only the run holding the instance touches <paramref name="state"/>).
        /// </summary>
        public int DefinitionTokens(CompactionState state, ITokenCounter counter)
        {
            var current = CurrentSelection();
            if (state.ToolTokensVersion != current.Version)
            {
                state.ToolTokens = AgentLoop.DefinitionTokens(current.Registry.Definitions, counter);
                state.ToolTokensVersion = current.Version;
            }

            return state.ToolTokens;
        }

        private Selected CurrentSelection()
        {
            var published = surface.Current;
            var cached = Volatile.Read(ref selected);
            if (cached is not null && cached.Version == published.Version)
            {
                return cached;
            }

            var fresh = new Selected(published.Version, published.Tools.Select(toolNames, canSpawn, subAgentTools, isRoot));
            Volatile.Write(ref selected, fresh);
            return fresh;
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
