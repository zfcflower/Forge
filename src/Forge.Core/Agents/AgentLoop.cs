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
    Func<string, string?>? DynamicContext,
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

    /// <summary>
    /// The pool would deadlock the root agent at zero permits, so a caller that hands over a smaller number still gets one.
    /// The settings section clamps to the same floor; this is the belt for a host configured in code.
    /// </summary>
    private const int SubAgentLimitsHardFloor = 1;

    /// <summary>What a call that the user stopped on its own returns to the model: an error result, and the turn goes on.</summary>
    internal const string CancelledByUserResult = "Cancelled by the user: this tool call was stopped before it finished.";

    private readonly SemaphoreSlim approvalGate = new(1, 1);
    private readonly AgentSlotPool slots = new(services.Options.MaxParallelSubAgents);
    private readonly ToolCallCancellations toolCalls = new();

    /// <summary>
    /// Stops one running tool call of this tree (the root or any sub-agent) without stopping its turn: the call's own token
    /// fires (a shell command's process tree is killed, a spawned child is cancelled), and the loop answers the call with
    /// <see cref="CancelledByUserResult"/> as an error result so the model continues. False when no such call is running -
    /// it already finished, it never started, or it is still waiting for approval.
    /// </summary>
    internal bool CancelToolCall(string agentId, string callId) => toolCalls.Cancel(agentId, callId);

    private CompactionPolicy compactionPolicy = services.Options.Compaction;

    /// <summary>
    /// Replaces the cap on how many agents of this tree may run at once (the <c>agents</c> settings section). Raising it lets
    /// agents that are already queued start at once; lowering it never interrupts an agent in the middle of a turn, it only
    /// stops new ones from starting until the tree is back under the new cap. It changes no request and resets no state, so
    /// the prompt cache is untouched. Thread-safe, and a no-op when the cap did not move.
    /// </summary>
    /// <returns>Whether the cap moved.</returns>
    internal bool SetMaxParallelSubAgents(int limit)
    {
        // The pool holds the only copy of the cap, so the value read back can never disagree with the one enforced.
        var clamped = Math.Max(SubAgentLimitsHardFloor, limit);
        return slots.SetLimit(clamped);
    }

    /// <summary>The cap in force on how many agents of this tree run at once; see <see cref="SetMaxParallelSubAgents"/>.</summary>
    internal int MaxParallelSubAgents => slots.Limit;

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
        // The turn's wall clock starts once the slot is held — the same moment the App's running marker counts from — so
        // a queued child's wait for the slot never lands inside any turn's elapsed.
        var turnStartedAt = DateTimeOffset.UtcNow;
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
            instance.Depth == 0,
            // Only the root and the sessions the user drives hold the delivery tool; a model-spawned child never does.
            instance.Depth == 0 || instance.IsUserSession);
        var provider = services.Providers(instance.Definition.Model);

        await ResolveCacheStateAsync(instance, ct).ConfigureAwait(false);
        await PersistAsync(instance, input, null, ct).ConfigureAwait(false);

        var noEvidenceLimit = services.Options.NoEvidenceTurnLimit;
        var noEvidenceTurns = 0;
        ToolCallBlock[]? previousCalls = null;

        for (var turn = 0; turn < services.Options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            // The first request of a turn is where a compaction may happen: the context is then whatever the previous turn
            // left, so a turn never loses the evidence it is in the middle of collecting. Later requests of the same turn
            // only compact at the emergency line near the window (or when the provider refuses the request as too long).
            await CompactIfNeededAsync(instance, tools, sink, urgent: turn > 0, ct).ConfigureAwait(false);

            ChatMessage? assistant = null;
            var usage = TokenUsage.Empty;
            var streamElapsed = TimeSpan.Zero;
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
                        // A wait on the provider's local budget is not content: `received` guards the overflow retry
                        // (one compact-and-resend only while nothing has been observed), and a wait line is nothing that
                        // resending would duplicate. It is forwarded as a quiet status for the turn it is holding up.
                        received |= chunk is not BudgetWait;
                        switch (chunk)
                        {
                            case BudgetWait wait:
                                sink.Emit(new AgentBudgetWait(instance.Id, provider.Id, wait.Wait));
                                break;
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
                                streamElapsed += completed.StreamElapsed;
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
                    // when nothing could be compacted, or the resend overflows again, the provider error stands. The resend
                    // cannot double-count streaming time: it only happens while nothing has been observed, so the abandoned
                    // attempt yielded no MessageCompleted and its measurement never landed anywhere - the resent attempt
                    // measures its own stream from its own first increment.
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
            // The turn's model-streaming time lands on the same instance-cumulative counter as the usage line above:
            // a turn's own share is the difference against the turn-start snapshot, so the event below carries the
            // cumulative value just like it carries the cumulative usage.
            instance.StreamElapsed += streamElapsed;
            ReportFirstRequest(instance, provider, usage);
            instance.CommitText(Text(assistant));
            var lastSeq = await PersistAsync(instance, assistant, usage, ct).ConfigureAwait(false);
            ReportContext(instance, sink, ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter)));
            await TouchAsync(instance, ct).ConfigureAwait(false);

            var calls = ToolCalls(assistant);
            if (calls.Length == 0)
            {
                if (await ContinueWithControlAsync(instance, control, sink, ct).ConfigureAwait(false))
                {
                    continue;
                }

                sink.Emit(new AgentTurnCompleted(instance.Id, instance.Usage, stop, instance.StreamElapsed, turnStartedAt, DateTimeOffset.UtcNow, lastSeq));
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

            // A run that keeps asking the same question makes no progress: every search of the turn matched nothing, or the
            // batch repeats the previous one call for call. Ending here returns what was produced; letting the turn budget
            // run out would throw it away (see forge-core "无进展收尾"). A steer is the user speaking, so it clears the count.
            if (execution.Steering is not null)
            {
                noEvidenceTurns = 0;
            }
            else if (IsNoEvidence(calls, previousCalls, execution.Results))
            {
                noEvidenceTurns++;
                if (noEvidenceLimit > 0 && noEvidenceTurns >= noEvidenceLimit)
                {
                    // A queued instruction is the user asking for something else: deliver it and keep going rather than
                    // stopping on top of it. Stopping is what is left when nobody has anything to add.
                    if (await ContinueWithControlAsync(instance, control, sink, ct).ConfigureAwait(false))
                    {
                        noEvidenceTurns = 0;
                    }
                    else
                    {
                        return await StopWithoutEvidenceAsync(instance, noEvidenceTurns, stop, turnStartedAt, sink, ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                noEvidenceTurns = 0;
            }

            previousCalls = calls;
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
            IncludeToolImages = SendsToolImages(instance.Definition.Model),
        };
    }

    /// <summary>
    /// Whether this turn's tool results may carry their images to the model: the user's switch, and a model whose catalogue
    /// entry is known not to take image input. Both are asked here, once per request, so a change applies from the next turn.
    /// </summary>
    private bool SendsToolImages(ModelSettings model)
    {
        if (!services.Options.SendToolImagesToModel())
        {
            return false;
        }

        return services.Options.ModelInputs?.Invoke(model) is not { } inputs || (inputs & ModelInputs.Image) != 0;
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

    /// <summary>
    /// The trailing text of one request: the instance's own dynamic suffix, then whatever the host adds for that agent id.
    /// The id matters — a sub-agent's window and budget are its own, so the host must not answer with the root's numbers.
    /// </summary>
    private string DynamicText(AgentInstance instance)
    {
        var suffix = instance.Prompt.DynamicSuffix;
        var callback = services.DynamicContext?.Invoke(instance.Id);
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
    /// Automatic compaction before a request. The zero-cost reduction rung runs first: when the context is over the
    /// line it replaces old tool results (never the inherited prefix, never the keepRecent window) with receipts, and
    /// the summarizer is only asked when the receipts were not enough. A reduction that brings the context back under
    /// the line also revives a stalled instance; one that does not leaves the fruitless bookkeeping untouched. Next comes
    /// the irreducible floor: when the inherited prefix, the stable prefix, the tool definitions and the messages the
    /// summarizer must keep already exceed the line, no summary can help, so the instance stalls with
    /// <see cref="CompactionStallReason.Irreducible"/> instead of spending a request. On the first request of a turn
    /// C ≥ trigger·U starts either; a request later in the same turn only acts at the emergency line, so a turn that is
    /// still collecting evidence keeps its raw tool results. An attempt that leaves C at or above the line it used is
    /// fruitless: the next attempt waits until the instance's own content grew by 0.1·U, and after two consecutive
    /// fruitless attempts automatic summarization stops until a manual compaction, another model, or a reduction that
    /// succeeds.
    /// </summary>
    private async Task CompactIfNeededAsync(
        AgentInstance instance,
        ToolSelection tools,
        IAgentEventSink sink,
        bool urgent,
        CancellationToken ct)
    {
        var policy = CurrentCompaction;
        if (!policy.Enabled || services.Compactor is NoopCompactor)
        {
            return;
        }

        var state = SyncModel(instance);
        if (policy.BudgetFor(instance.Definition.Model) is not { } budget)
        {
            return;
        }

        var trigger = budget.TriggerTokens(policy.TriggerRatio);
        var line = urgent ? budget.EmergencyTokens(policy.EmergencyRatio) : trigger;
        var toolTokens = tools.DefinitionTokens(instance.Compaction, services.TokenCounter);
        var current = ContextTokens(instance, toolTokens);
        if (current < line)
        {
            return;
        }

        // The reduction rung: no model call, so it runs even for a stalled instance (it is also the one thing that can
        // bring one back under the line) and without the fruitless wait. It aims at the midpoint between the trigger
        // line and the summarizer's target, derived rather than configured.
        var midpoint = budget.TargetTokens((policy.TriggerRatio + policy.TargetRatio) / 2);
        var reduction = ToolResultReduction.Apply(
            instance.Messages,
            budget.KeepRecentTokens,
            current - midpoint,
            services.TokenCounter);
        if (reduction is { Messages: { } reduced })
        {
            instance.ReplaceHistory(reduced);
            var reducedAfter = ContextTokens(instance, toolTokens);
            sink.Emit(new AgentContextReduced(
                instance.Id, current, reducedAfter, reduction.DroppedResults,
                reduction.ReadTokens, reduction.BashTokens, reduction.ImageTokens, reduction.OtherTokens));
            sink.Emit(new AgentContextMeasured(instance.Id, reducedAfter, budget.Usable, line) { Usage = instance.Usage });
            if (reducedAfter < line)
            {
                state.ResetHysteresis();
                return;
            }

            // A partial reduction does not clear the fruitless bookkeeping: an inherited prefix that alone exceeds the
            // line must not earn a summarizer request every turn just because the receipts moved a little.
            current = reducedAfter;
        }

        if (state.Stalled)
        {
            // An irreducible block can lift by itself: a higher line (another trigger setting) or a smaller tool set. New
            // numbers are the only way a floor that exceeded the line stops exceeding it; anything else would just spend
            // another summarizer request to learn the same thing. An ineffective summary stays off until a manual
            // compaction or another model resets it.
            if (state.StallReason != CompactionStallReason.Irreducible
                || (line <= state.IrreducibleLine && toolTokens >= state.IrreducibleToolTokens))
            {
                return;
            }

            state.ResetHysteresis();
        }

        var target = budget.TargetTokens(policy.TargetRatio);
        var keepRecent = KeepRecent(budget.KeepRecentTokens, target);

        // The floor no summary can get below. When it already exceeds the line the summarizer cannot help, and asking it
        // would spend a request (and its batches) to prove that; a lower floor than the line means the attempt is still
        // worth making even when the target is out of reach.
        var floor = IrreducibleTokens(instance, toolTokens, keepRecent);
        if (floor >= line)
        {
            state.Stalled = true;
            state.StallReason = CompactionStallReason.Irreducible;
            state.IrreducibleLine = line;
            state.IrreducibleToolTokens = toolTokens;
            sink.Emit(new AgentCompactionStalled(instance.Id, current, trigger) { Reason = CompactionStallReason.Irreducible });
            return;
        }

        if (state.FruitlessBaseline is { } baseline && OwnTokens(instance) < (long)baseline + budget.RegrowthTokens)
        {
            return;
        }

        var result = await TryCompactAsync(
            instance,
            new CompactionRequest(target, keepRecent, CompactionReason.Auto),
            ct).ConfigureAwait(false);
        var after = current;
        if (result is not null)
        {
            sink.Emit(new AgentCompacted(instance.Id, result.TokensBefore, result.TokensAfter));
            after = ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter));
            sink.Emit(new AgentContextMeasured(instance.Id, after, budget.Usable, trigger) { Usage = instance.Usage });
            if (after < line)
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
            state.StallReason = CompactionStallReason.Ineffective;
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
            // The instance's cumulative usage, so a shell reading these can follow a turn request by request instead of
            // waiting for its end; a compaction changes the context, not the spend, and reports what already stands.
            Usage = instance.Usage,
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

    /// <summary>
    /// Tokens automatic summarization can never drop: the inherited prefix (untouchable — it is what makes a fork's prompt
    /// cache reusable), the stable prefix and tool definitions every request carries, and the newest messages the summarizer
    /// must keep (<paramref name="keepRecent"/> is what it is asked for, cut to what the instance actually has). The current
    /// summary counts as zero: a pass can always rewrite it smaller, so charging its size here would abandon an instance
    /// that a summary could still help. This is a lower bound, and it is only computed once the context is over the line.
    /// </summary>
    private int IrreducibleTokens(AgentInstance instance, int toolTokens, int keepRecent)
    {
        var inherited = new List<ChatMessage>();
        foreach (var entry in instance.Messages)
        {
            if (entry.Inherited)
            {
                inherited.Add(entry.Message);
            }
        }

        return (int)Math.Min(
            int.MaxValue,
            (long)services.TokenCounter.Count(inherited)
            + services.TokenCounter.Count(instance.Prompt.StablePrefix)
            + Math.Max(0, toolTokens)
            + Math.Min(keepRecent, OwnTokens(instance)));
    }

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

    /// <summary>The slot-yield a tool may use while it waits on another agent (see <see cref="ToolContext.YieldSlot"/>).</summary>
    /// <param name="ct">The turn's token, not a call's: the slot is taken back with it, so cancelling one waiting call cannot leave the agent without its permit.</param>
    private static IAsyncDisposable YieldCurrentSlot(CancellationToken ct) =>
        AgentScope.Current is { } scope ? (IAsyncDisposable)scope.Permit.Detach(ct) : NoYield.Instance;

    private sealed class NoYield : IAsyncDisposable
    {
        public static readonly NoYield Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Ends a run that stopped making progress: the note becomes a real message — persisted, streamed as a delta and returned —
    /// so the transcript, the screen and the parent agent all say the same thing about why the run ended early. Only streamed
    /// text would leave the store behind; only a return value would leave the screen behind.
    /// </summary>
    private async ValueTask<AgentRunResult> StopWithoutEvidenceAsync(
        AgentInstance instance,
        int turns,
        StopReason stop,
        DateTimeOffset turnStartedAt,
        IAgentEventSink sink,
        CancellationToken ct)
    {
        var text = NoEvidenceText(instance, turns);
        instance.AppendText(text);
        instance.CommitText(text);
        var lastSeq = await PersistAsync(instance, new ChatMessage(MessageRole.Assistant, [new TextBlock(text)]), null, ct).ConfigureAwait(false);
        sink.Emit(new AgentTextDelta(instance.Id, text));
        sink.Emit(new AgentTurnCompleted(instance.Id, instance.Usage, stop, instance.StreamElapsed, turnStartedAt, DateTimeOffset.UtcNow, lastSeq));
        return new AgentRunResult(text, instance.Usage, stop);
    }

    /// <summary>
    /// Whether one turn produced no new evidence. Two shapes qualify: every call was a read-only search and every result says
    /// it matched nothing, or the whole batch repeats the previous turn's byte for byte. Deliberately narrow: a shell, edit or
    /// build failure says nothing about the question being asked, so those never count, and a single matching or successful
    /// call clears the streak.
    /// </summary>
    private static bool IsNoEvidence(ToolCallBlock[] calls, ToolCallBlock[]? previous, ContentBlock[] results)
    {
        if (previous is not null && SameBatch(calls, previous))
        {
            return true;
        }

        if (calls.Length != results.Length)
        {
            return false;
        }

        for (var index = 0; index < calls.Length; index++)
        {
            if (!IsSearchTool(calls[index].Name)
                || results[index] is not ToolResultBlock { IsError: false } result
                || !IsEmptySearchResult(result))
            {
                return false;
            }
        }

        return calls.Length > 0;
    }

    private static bool SameBatch(ToolCallBlock[] calls, ToolCallBlock[] previous)
    {
        if (calls.Length != previous.Length)
        {
            return false;
        }

        for (var index = 0; index < calls.Length; index++)
        {
            if (!string.Equals(calls[index].Name, previous[index].Name, StringComparison.Ordinal)
                || !string.Equals(calls[index].ArgumentsJson, previous[index].ArgumentsJson, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The read-only tools whose empty answer means "the question is unchanged". The names are Forge.IO's, and the loop only
    /// compares them: depending on Forge.IO from Forge.Core would point the module arrow the wrong way, so they are spelled
    /// here where the decision is made.
    /// </summary>
    private static bool IsSearchTool(string name) =>
        string.Equals(name, "grep", StringComparison.Ordinal)
        || string.Equals(name, "find", StringComparison.Ordinal)
        || string.Equals(name, "ls", StringComparison.Ordinal);

    private static bool IsEmptySearchResult(ToolResultBlock result) =>
        result.Blocks is [TextBlock text]
        && (text.Text.StartsWith("no matches", StringComparison.Ordinal) || string.Equals(text.Text, "(empty)", StringComparison.Ordinal));

    /// <summary>What a no-progress stop returns: everything the run produced, and one line saying why it ended early.</summary>
    private static string NoEvidenceText(AgentInstance instance, int turns)
    {
        var partial = instance.PartialText;
        var reason = $"stopped: {turns} consecutive turns returned no new evidence (every search matched nothing).";
        return partial.Length == 0 ? $"[{reason} No result was produced.]" : $"{partial}\n\n[{reason} Partial result above.]";
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
            Tools = tools.Current(),
            YieldSlot = _ => YieldCurrentSlot(ct),
            FileVersions = instance.FileVersions,
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
        var registry = tools.Current();
        if (!registry.TryGet(call.Name, out var tool))
        {
            return new ToolResultBlock(call.Id, UnknownToolMessage(registry, call.Name), IsError: true);
        }

        var request = new ApprovalRequest(call.Name, call.ArgumentsJson, ToolRegistry.SideEffectOf(tool, call), instance.WorkingDirectory);
        if (!await IsApprovedAsync(request, ct).ConfigureAwait(false))
        {
            sink.Emit(new AgentApprovalDenied(instance.Id, call));
            return new ToolResultBlock(call.Id, "The user denied this tool call.", IsError: true);
        }

        // The call's own token, linked to the turn's: a turn cancel still reaches every call, and CancelToolCall reaches this
        // one only. Registered before the start event, so a stop pressed as soon as the row shows finds it.
        var callSource = toolCalls.Begin(instance.Id, call.Id, ct);
        ToolResult result;
        try
        {
            sink.Emit(new AgentToolStarted(instance.Id, call));
            var callContext = context with { Progress = tail => sink.Emit(new AgentToolProgress(instance.Id, call, tail)) };
            result = await tool.InvokeAsync(call, callContext, callSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (callSource.IsCancellationRequested)
        {
            // Stopped on its own (the turn's token is untouched, checked above): a normal error result, so the model sees what
            // happened and the turn continues with the other calls.
            result = new ToolResult(CancelledByUserResult, IsError: true);
        }
        catch (Exception exception)
        {
            result = new ToolResult(exception.Message, IsError: true);
        }
        finally
        {
            toolCalls.End(instance.Id, call.Id, callSource);
        }

        result = await SpillAsync(instance, tool, result, ct).ConfigureAwait(false);
        sink.Emit(new AgentToolFinished(instance.Id, call, result));
        return new ToolResultBlock(call.Id, result.Blocks, result.IsError);
    }

    /// <summary>
    /// The answer to a call for a tool this agent does not hold. It names the tools it does hold: an agent told only that
    /// "bash" is unknown has nothing to switch to, and no way to tell a typo from a kind that is simply not offered to it.
    /// </summary>
    private static string UnknownToolMessage(ToolRegistry registry, string name)
    {
        if (registry.Definitions.Count == 0)
        {
            return $"Unknown tool '{name}'.";
        }

        var builder = new StringBuilder($"Unknown tool '{name}'. Available: ");
        for (var index = 0; index < registry.Definitions.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(registry.Definitions[index].Name);
        }

        return builder.Append('.').ToString();
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
        if (services.Artifacts is not { } artifacts || result.IsError || threshold <= 0)
        {
            return result;
        }

        if (tool is IBoundedOutputTool || string.Equals(tool.Definition.Name, ReadArtifactTool.Name, StringComparison.Ordinal))
        {
            return result;
        }

        // Only the text is stored: an image is already bounded by whoever produced it, and it stays a block of the result (the provider adapters decide how the model receives it).
        var text = result.Blocks is [TextBlock only] ? only.Text : string.Join('\n', result.Blocks.OfType<TextBlock>().Select(static block => block.Text));
        if (text.Length <= threshold)
        {
            return result;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var info = await artifacts
            .PutAsync(instance.SessionId, string.Concat(tool.Definition.Name, "-", instance.Id), bytes, ct)
            .ConfigureAwait(false);
        var preview = Math.Clamp(services.Options.ArtifactPreviewChars, 0, text.Length);
        var builder = new StringBuilder(preview + 256);
        // Head and tail: the end of a command's output (an error summary, a result line) is as useful as its start.
        var head = preview - preview / 2;
        var tail = preview / 2;
        builder.Append(text, 0, head);
        if (tail > 0)
        {
            builder.Append("\n[… ").Append(text.Length - head - tail).Append(" characters omitted …]\n").Append(text, text.Length - tail, tail);
        }

        builder.Append("\n\n[output truncated:").Append(bytes.Length).Append(" bytes stored as artifact '").Append(info.Id)
            .Append("'. Read it with the read_artifact tool: artifact_id \"").Append(info.Id)
            .Append("\", offset, length.]");
        var spilled = new List<ContentBlock> { new TextBlock(builder.ToString()) };
        spilled.AddRange(result.Blocks.OfType<ImageBlock>());
        return new ToolResult(spilled, result.IsError);
    }

    /// <summary>
    /// Persists one conversation message and answers the seq the store gave it, 0 when no store is attached: the run-end
    /// event carries that seq so the turn's metrics row can name its exact stream position.
    /// </summary>
    private async Task<long> PersistAsync(AgentInstance instance, ChatMessage message, TokenUsage? usage, CancellationToken ct)
    {
        var seq = services.Store is null
            ? 0
            : await services.Store.AppendAsync(instance.SessionId, message, usage, ct).ConfigureAwait(false);
        instance.Add(message, seq);
        return seq;
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
        bool isRoot,
        bool canMessage)
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

            var fresh = new Selected(published.Version, published.Tools.Select(toolNames, canSpawn, subAgentTools, isRoot, canMessage));
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
