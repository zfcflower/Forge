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
    ITokenCounter TokenCounter,
    AgentOptions Options,
    IReadOnlyList<string> SubAgentToolNames,
    Func<string, string?>? DynamicContext,
    Action<FirstRequestCacheReport>? CacheDiagnostics = null,
    ITurnLedgerStore? TurnLedger = null);

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
        // The managed history is rebuilt from the session's ledger before the turn's own message joins it: the model reads
        // the fold block plus one entry per finished turn, never the raw exchange of turns that already closed. A session
        // with no ledger rows (or a host without a ledger) keeps whatever history it already carried.
        await ReloadManagedAsync(instance, ct).ConfigureAwait(false);
        var openingSeq = await PersistAsync(instance, input, null, ct).ConfigureAwait(false);

        // The turn's ledger row opens right after the opening user message is persisted: that message's seq is the row's
        // from-seq and its text is the row's task, and the row exists before the first model request, so every exit path -
        // normal end, no-evidence stop, the limit, a cancellation, a provider failure - has a row to settle. Best effort:
        // the shadow ledger observes the turn, it must never fail it (see SettleLedgerAsync for the settle half).
        var ledger = await BeginLedgerAsync(instance, input, openingSeq, ct).ConfigureAwait(false);
        // The run's own text memory, cleared here at the turn's start and fed on every delta: the display buffers are
        // wiped between the run's requests, so a run that said something in one request and died in a later one - before
        // that request's first delta - still has everything it said here. Every harness-terminated settle reads this; a
        // normal end never does, it answers with the reply the run ended on, exactly as the user saw it.
        instance.BeginRunText();

        var noEvidenceLimit = services.Options.NoEvidenceTurnLimit;
        var noEvidenceTurns = 0;
        ToolCallBlock[]? previousCalls = null;

        try
        {
            for (var turn = 0; turn < services.Options.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();
                // The first request of a turn is where a compaction may happen: the context is then whatever the previous turn
                // left, so a turn never loses the evidence it is in the middle of collecting. Later requests of the same turn
                // only compact at the emergency line near the window (or when the provider refuses the request as too long).
                await FoldIfNeededAsync(instance, tools, sink, urgent: turn > 0, ct).ConfigureAwait(false);

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
                            // A wait on the provider's local budget, and the pause before a retried attempt, are not content:
                            // `received` guards the overflow retry (one compact-and-resend only while nothing has been observed),
                            // and neither wait is anything that resending would duplicate. Both are forwarded as quiet status
                            // lines for the turn they are holding up.
                            received |= chunk is not BudgetWait and not RetryWait;
                            switch (chunk)
                            {
                                case BudgetWait wait:
                                    sink.Emit(new AgentBudgetWait(instance.Id, provider.Id, wait.Wait));
                                    break;
                                case RetryWait retry:
                                    sink.Emit(new AgentRetryWait(
                                        instance.Id, provider.Id, retry.Delay, retry.Attempt, retry.Total, retry.Reason));
                                    break;
                                case TextDelta text:
                                    instance.AppendText(text.Text);
                                    instance.AppendRunText(text.Text);
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
                        if (!await FoldAfterOverflowAsync(instance, tools, sink, ct).ConfigureAwait(false))
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
                // The reply's flat text is computed once and reused everywhere it is needed - the committed instance text,
                // the ledger's settle and the run's own result - so a host with a ledger never pays for it twice.
                var answerText = Text(assistant);
                instance.CommitText(answerText);
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

                    // The reply the model ended on is what the ledger keeps, exactly as the user saw it.
                    if (ledger is not null)
                    {
                        await SettleLedgerAsync(ledger, instance, answerText, lastSeq, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    sink.Emit(new AgentTurnCompleted(instance.Id, instance.Usage, stop, instance.StreamElapsed, turnStartedAt, DateTimeOffset.UtcNow, lastSeq));
                    return new AgentRunResult(answerText, instance.Usage, stop);
                }

                var execution = await ExecuteToolsAsync(instance, tools, calls, control, sink, ct).ConfigureAwait(false);
                // The turn's evidence grows with every batch the model ran, and it reads the batch's own results: the
                // spec ids come out of the result text already in memory, so no file is ever opened for them. A batch
                // that never returned (the turn was cancelled mid-run) pins nothing - its calls never happened.
                if (ledger is not null)
                {
                    (ledger.Evidence ??= new TurnLedgerEvidence()).Collect(calls, execution.Results);
                }
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
                            return await StopWithoutEvidenceAsync(instance, noEvidenceTurns, stop, turnStartedAt, sink, ct, ledger).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    noEvidenceTurns = 0;
                }

                previousCalls = calls;
            }

            // The turn budget ran out: the row still carries everything the run streamed across its requests, not just
            // the last request's buffer.
            if (ledger is { Settled: false })
            {
                await SettleLedgerAsync(ledger, instance, instance.RunText, instance.LastPersistedSeq, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw new AgentTurnLimitException(
                $"Agent '{instance.Id}' did not finish within {services.Options.MaxTurns} turns.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The finally settles the row with the cancellation as its reason.
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A provider failure or any other throw: the row the turn opened is settled, never left pending, with the text
            // the run streamed - a run that died in a later request keeps what its earlier ones said.
            if (ledger is { Settled: false })
            {
                await SettleLedgerAsync(ledger, instance, instance.RunText, instance.LastPersistedSeq, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            // The belt under every path above, the one that makes a pending row impossible: whatever way out the turn
            // found - including paths that rethrow - the row it opened is settled exactly once. The text is the run's
            // own accumulation: a turn cancelled before its first delta produced nothing, and the previous turn's
            // committed answer (what PartialText would fall back to) must never be recorded as this turn's.
            if (ledger is { Settled: false })
            {
                await SettleLedgerAsync(ledger, instance, instance.RunText, instance.LastPersistedSeq, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
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
        var messages = new List<ChatMessage>(instance.Messages.Count + 2);
        // The legend leads every request a ledger host sends - including a session's very first one, where the view has no
        // entry to put it in front of. See ManagedContext.Legend for why it cannot live inside the view.
        var lead = services.TurnLedger is null ? 0 : 1;
        if (lead == 1)
        {
            messages.Add(ChatMessage.User(ManagedContext.Legend));
        }

        foreach (var entry in instance.Messages)
        {
            messages.Add(entry.Message);
        }

        // Decided before the carrier and the dynamic text go on, so it names the same message the request used to end at: the
        // short-lived tail (and the carrier in front of it) stays outside the breakpoint, exactly as before the carrier existed.
        var cacheThrough = messages.Count >= 2 ? messages.Count - 2 : (int?)null;
        // The managed boundary: the last message that is identical in every request of this session. Null when the history
        // was not built from the ledger (a fresh instance, or a host without one).
        int? stable = instance.ManagedCount > 0 && instance.ManagedCount + lead <= messages.Count - 1
            ? instance.ManagedCount - 1 + lead
            : null;
        var cacheTtl = ResolveCacheTtl(instance);
        var dynamicText = DynamicText(instance);
        if (dynamicText.Length > 0)
        {
            messages.Add(new ChatMessage(MessageRole.Assistant, [new TextBlock(ContextCarrier)]));
            messages.Add(new ChatMessage(MessageRole.User, [new TextBlock(MarkedContext(dynamicText))]));
        }

        return new ChatRequest(
            instance.Prompt.StablePrefix,
            definitions,
            messages,
            instance.Definition.Model,
            new CacheHint(CacheSystemAndTools: true, cacheThrough, cacheTtl) { StableBoundaryIndex = stable })
        {
            CacheRoutingKey = instance.CacheState.RoutingKey ?? instance.SessionId,
            SessionId = instance.SessionId,
            IncludeToolImages = SendsToolImages(instance.Definition.Model),
        };
    }

    /// <summary>
    /// The one-line assistant message that stands between a request's own last message and its dynamic text. It exists for the
    /// cache, not for the model: writers merge adjacent same-role messages into one wire message, so a tail appended straight
    /// after the turn's opening question became one block with it — and the next request, where the tail sits after a tool
    /// result instead, then left that early block changed, which invalidated everything from there on (measured on a gateway
    /// that only credits whole matching blocks: the 21k system+tools was re-read for 0% of a prompt that had not changed).
    /// The role switch gives the tail its own block, so the prefix stays stable and only the newest block is new. Fixed bytes:
    /// a carrier whose text varied would be the same problem one message later.
    /// </summary>
    public const string ContextCarrier = "(harness context follows)";

    /// <summary>
    /// The markers around the one trailing message that carries a request's dynamic text. That message rides along
    /// unrecorded — no part of it reaches the transcript — so whoever reads the wire (the model, an analysis cutting turns
    /// out of a request log, a replay diff) has only the text itself to tell it from something the user typed. These two
    /// lines are that signal, and they are a fixed contract with every such reader.
    /// </summary>
    private const string ContextOpen = "<forge-context>";
    private const string ContextClose = "</forge-context>";

    private static string MarkedContext(string text) => string.Concat(ContextOpen, "\n", text, "\n", ContextClose);

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
    /// The trailing text of one request: whatever the host adds for that agent id. The id matters — a sub-agent's window and
    /// budget are its own, so the host must not answer with the root's numbers. Nothing here is stored or written to history:
    /// the tail rides one request and is gone.
    /// </summary>
    private string DynamicText(AgentInstance instance) => services.DynamicContext?.Invoke(instance.Id) ?? string.Empty;

    /// <summary>The turn's ledger row while it is open: the store, the begin record it was opened with, and the evidence
    /// collected so far. The turn number is null until the begin lands - the settle's own retry or the store's own
    /// allocation fills it in, so a handle exists for every turn no matter what the begin did.</summary>
    private sealed class LedgerTurn(ITurnLedgerStore store, TurnLedgerBegin begin)
    {
        public ITurnLedgerStore Store { get; } = store;

        /// <summary>The begin record, kept for the once-only retry the settle makes when the first write never landed.</summary>
        public TurnLedgerBegin Begin { get; } = begin;

        /// <summary>The number the row was filed under; null while the begin has not landed (or lost it) - the settle
        /// retrieves it, and a settle that still has none files the row under the store's own number.</summary>
        public long? Turn { get; set; }

        /// <summary>Created by the first tool batch of the turn: a turn that never ran a tool collects no evidence.</summary>
        public TurnLedgerEvidence? Evidence { get; set; }

        /// <summary>Set by the settle, so every path below the begin settles exactly once - the finally is the last taker.</summary>
        public bool Settled { get; set; }

        public long FromSeq => Begin.FromSeq;
    }

    /// <summary>
    /// Opens the turn's row. The number a caller already owns (the App's root turn, a delivered user-session turn) is
    /// handed over inside the begin record, and a caller without one has the store allocate it in the same insert - the
    /// reservation is the insert, not a read beside it. The handle exists before the one await that can fail: a begin
    /// that fails leaves the turn holding a handle with no number, and the settle still lands (retried once there, then
    /// filed under the store's own number). The write is deliberately not tied to the turn's token - a cancellation that
    /// arrives while the row is opening must not lose the row the turn is about to settle. Best effort either way: a
    /// store failure costs the row, never the turn.
    /// </summary>
    private async ValueTask<LedgerTurn?> BeginLedgerAsync(AgentInstance instance, ChatMessage input, long openingSeq, CancellationToken ct)
    {
        if (services.TurnLedger is not { } store)
        {
            return null;
        }

        var ledger = new LedgerTurn(
            store,
            new TurnLedgerBegin(
                instance.SessionId,
                instance.TurnSequence,
                Text(input),
                instance.Definition.Model.ProviderId,
                instance.Definition.Model.Model,
                instance.Definition.Name,
                instance.Depth,
                openingSeq,
                DateTimeOffset.UtcNow));
        try
        {
            ledger.Turn = await store.BeginAsync(ledger.Begin, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Every kind of begin failure - including one that arrived mid-write - leaves the handle in place with no
            // number: the settle retries the begin once and, failing that, files the row itself.
            Trace.TraceWarning($"turn ledger of '{instance.SessionId}' not opened: {exception.Message}");
        }

        return ledger;
    }

    /// <summary>
    /// Settles the turn's row with what the turn produced: the reply exactly as it was shown, and that reply as the entry will
    /// render it (a conclusion label the model echoed is unwrapped, so the entry cannot show two). A reply is free-form - the
    /// harness asks for no format and therefore has nothing to judge - so every settle is <see cref="TurnLedgerStatus.Settled"/>;
    /// what tells a harness-terminated turn apart is its text, which is only what the run streamed. When the begin never
    /// landed, it is retried once here - best effort - so the row carries the task text and its owner; a retry that fails too
    /// settles under a null number with <see cref="LedgerTurn.Begin"/> riding <see cref="TurnLedgerCompletion.Opened"/>, and the
    /// store files the row under its own number without losing the turn's identity. Best effort throughout, and idempotent
    /// through <see cref="LedgerTurn.Settled"/>: a store failure costs the row, never the turn.
    /// </summary>
    private async ValueTask SettleLedgerAsync(
        LedgerTurn ledger,
        AgentInstance instance,
        string answerText,
        long answerSeq,
        CancellationToken ct)
    {
        if (ledger.Settled)
        {
            return;
        }

        ledger.Settled = true;
        var conclusion = TurnReportTemplate.UnwrapConclusion(answerText);
        try
        {
            var turn = ledger.Turn;
            if (turn is null)
            {
                // The begin never landed: file it now, once, so the settled row carries the task text and its owner -
                // and a retry that fails too still settles, under a number the store picks itself.
                try
                {
                    turn = ledger.Turn = await ledger.Store.BeginAsync(ledger.Begin, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning($"turn ledger of '{instance.SessionId}' not opened on the settle's retry: {exception.Message}");
                }
            }

            // A turn that ran no tools carries the empty evidence - the seq range is the only pin it has.
            var evidence = ledger.Evidence is { } collected
                ? new TurnEvidence(collected.ReadFiles, collected.ChangedFiles, collected.Specs, ledger.FromSeq, instance.LastPersistedSeq)
                : new TurnEvidence([], [], [], ledger.FromSeq, instance.LastPersistedSeq);
            await ledger.Store.CompleteAsync(
                new TurnLedgerCompletion(
                    instance.SessionId,
                    turn,
                    TurnLedgerStatus.Settled,
                    answerText,
                    conclusion,
                    evidence,
                    answerSeq,
                    DateTimeOffset.UtcNow,
                    // The begin record travels with every settle: a store that has to create the row itself - no begin
                    // landed under any number - writes its task text, owner and from-seq from this instead of filing
                    // an anonymous row.
                    ledger.Begin),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Everything is a ledger failure here. The token this settle runs on is CancellationToken.None, so even an
            // OperationCanceledException can only be the store cancelling itself - swallowing everything is what keeps
            // a store failure from taking the turn's own event down with it.
            Trace.TraceWarning($"turn ledger of '{instance.SessionId}' not settled: {exception.Message}");
        }
    }

    /// <summary>The one-line shape of a failure for a validation detail: the message's first sentence, bounded.</summary>
    private static string ShortReason(Exception exception)
    {
        var text = (exception.Message ?? string.Empty).AsSpan();
        var cut = text.IndexOfAny(['.', '\n', '\r']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return text.Length <= 200 ? text.ToString() : string.Concat(text[..200].ToString(), "…");
    }


    /// <summary>
    /// <summary>
    /// Automatic folding before a request (forge-core "托管上下文"): when the managed history is over the line, the oldest
    /// entries are absorbed into the fold block - one UPDATE of the fold range, then one re-render. No model call, no
    /// summary, no loss of any stored fact: the folded turns stay readable through <c>read_context</c> and their raw
    /// messages through <c>read_history</c>. The turn in flight is untouched: its raw exchange is what the model is working
    /// with, and folding only ever removes finished turns from the view.
    /// </summary>
    private async ValueTask FoldIfNeededAsync(
        AgentInstance instance,
        ToolSelection tools,
        IAgentEventSink sink,
        bool urgent,
        CancellationToken ct)
    {
        var policy = CurrentCompaction;
        if (!policy.Enabled || services.TurnLedger is null
            || policy.BudgetFor(instance.Definition.Model) is not { } budget)
        {
            return;
        }

        var toolTokens = tools.DefinitionTokens(instance.Compaction, services.TokenCounter);
        var line = urgent ? budget.EmergencyTokens(policy.EmergencyRatio) : budget.TriggerTokens(policy.TriggerRatio);
        var current = ContextTokens(instance, toolTokens);
        if (current < line)
        {
            return;
        }

        var after = await FoldAsync(instance, current, policy.KeepRecentTurns, ct).ConfigureAwait(false);
        if (after < current)
        {
            sink.Emit(new AgentCompacted(instance.Id, current, after));
            ReportContext(instance, sink, after);
            if (after < line)
            {
                return;
            }
        }

        // Nothing left to fold (the newest turns plus the stable prefix already exceed the line): the instance is as small
        // as the managed context can be, and no further request can change that until something outside the history moves.
        sink.Emit(new AgentCompactionStalled(instance.Id, after, budget.TriggerTokens(policy.TriggerRatio)));
    }

    /// <summary>
    /// Folds every settled turn but the newest <paramref name="keepRecentTurns"/>, and answers the resulting size - a fold costs
    /// nothing but the entry text it stops carrying, so there is no reason to stop early: every turn kept back is one more turn
    /// that has to be read back with <c>read_context</c>, and every fold event rewrites the request prefix. The fold range only
    /// grows (the store's UPDATE is monotonic), so a later turn never unfolds what an earlier one folded.
    /// </summary>
    private async ValueTask<int> FoldAsync(AgentInstance instance, int current, int keepRecentTurns, CancellationToken ct)
    {
        if (services.TurnLedger is not { } store)
        {
            return current;
        }

        var rows = await store.ListAsync(instance.SessionId, ct).ConfigureAwait(false);
        if (rows.Count <= keepRecentTurns)
        {
            return current;
        }

        // Only settled turns fold: the turn in flight has no entry to fold into.
        var candidates = new List<TurnLedgerEntry>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.Folded && row.Status != TurnLedgerStatus.Pending)
            {
                candidates.Add(row);
            }
        }

        var foldable = Math.Max(0, candidates.Count - keepRecentTurns);
        if (foldable == 0)
        {
            return current;
        }

        await store.MarkFoldedAsync(instance.SessionId, candidates[foldable - 1].TurnSequence, CancellationToken.None)
            .ConfigureAwait(false);
        // The rebuild keeps everything past the last settled turn verbatim (see ReloadManagedAsync), which is exactly the turn
        // in flight: a fold can shrink the finished turns but can never rewrite what this turn has produced.
        await ReloadManagedAsync(instance, ct).ConfigureAwait(false);
        return ContextTokens(instance, instance.Compaction.ToolTokens);
    }

    /// <summary>
    /// Resets the instance's history to the managed view: the fold block plus one entry per settled turn, and then everything
    /// the session holds past that view verbatim - the turn in flight (its opening message, tool calls, results, steering) when
    /// a fold runs mid-turn, and the messages delivered while it was idle (a child's report, an interjection) when a turn
    /// starts. The rule that separates the two is the one that matters: a message whose stored seq is above the last settled
    /// turn belongs to nobody yet, so it is carried over; the exchange of a *finished* turn is already represented by its
    /// entry and is dropped.
    /// </summary>
    private async ValueTask ReloadManagedAsync(AgentInstance instance, CancellationToken ct)
    {
        if (services.TurnLedger is not { } store)
        {
            return;
        }

        var rows = await store.ListAsync(instance.SessionId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return;
        }

        var lastSeq = ManagedContext.LastSeq(rows);
        var managed = ManagedContext.Build(rows);
        var view = new List<AgentMessage>(managed.Count + 4);
        var seen = new HashSet<long>();
        foreach (var message in managed)
        {
            view.Add(new AgentMessage(message, 0));
        }

        // What the instance already carries past the view: the turn in flight, or a delivery that arrived while it was idle.
        foreach (var held in instance.Messages)
        {
            if (held.Seq > lastSeq && seen.Add(held.Seq))
            {
                view.Add(held);
            }
        }

        // And what only the store has (a session resumed in this process): the same range, deduplicated by seq.
        if (services.Store is { } history)
        {
            await foreach (var stored in history.ReadAsync(instance.SessionId, lastSeq + 1, null, ct).ConfigureAwait(false))
            {
                if (seen.Add(stored.Seq))
                {
                    view.Add(new AgentMessage(stored.Message, stored.Seq));
                }
            }
        }

        instance.ManagedCount = managed.Count;
        instance.ReplaceHistory(view);
    }

    /// <summary>
    /// The fold an overflow forces: every settled turn but the newest few, regardless of the target, then the caller
    /// resends once. False when nothing could be folded - the request itself is what does not fit, and folding the history
    /// cannot change that.
    /// </summary>
    private async Task<bool> FoldAfterOverflowAsync(AgentInstance instance, ToolSelection tools, IAgentEventSink sink, CancellationToken ct)
    {
        var before = ContextTokens(instance, tools.DefinitionTokens(instance.Compaction, services.TokenCounter));
        var after = await FoldAsync(instance, before, CurrentCompaction.KeepRecentTurns, ct).ConfigureAwait(false);
        if (after >= before)
        {
            return false;
        }

        sink.Emit(new AgentCompacted(instance.Id, before, after));
        ReportContext(instance, sink, after);
        return true;
    }

    /// <summary>
    /// Manual compaction (<c>/compact</c>): fold every settled turn but the newest few, regardless of the line and of
    /// <see cref="CompactionPolicy.Enabled"/>, and answer what it changed. Deterministic like the automatic path - the same
    /// stored rows always fold the same way - so the focus text a caller passes has nothing to steer and is ignored. Must
    /// not run while a turn of the same instance does.
    /// </summary>
    internal async Task<CompactionResult?> CompactNowAsync(AgentInstance instance, IAgentEventSink? sink, string? instructions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (services.TurnLedger is null)
        {
            return null;
        }

        var before = ContextTokens(instance, instance.Compaction.ToolTokens);
        var after = await FoldAsync(instance, before, CurrentCompaction.KeepRecentTurns, ct).ConfigureAwait(false);
        if (after >= before)
        {
            return null;
        }

        if (sink is not null)
        {
            sink.Emit(new AgentCompacted(instance.Id, before, after));
            // Outside a turn there is no tool selection: the count the last run cached stands in (0 before any run).
            ReportContext(instance, sink, ContextTokens(instance, instance.Compaction.ToolTokens));
        }

        return new CompactionResult(before, after);
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
            return (int)Math.Min(int.MaxValue, (long)state.LastPromptTokens + Estimate(messages, state.MessagesAtRequest));
        }

        return (int)Math.Min(
            int.MaxValue,
            (long)Estimate(messages, 0) + services.TokenCounter.Count(instance.Prompt.StablePrefix) + Math.Max(0, toolTokens));
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

    private int OwnTokens(AgentInstance instance) => Estimate(instance.Messages, 0);

    private int Estimate(List<AgentMessage> messages, int from)
    {
        if (from >= messages.Count)
        {
            return 0;
        }

        var slice = new List<ChatMessage>(messages.Count - from);
        for (var index = from; index < messages.Count; index++)
        {
            slice.Add(messages[index].Message);
        }

        return services.TokenCounter.Count(slice);
    }

    /// <summary>Usage recorded for another model no longer applies.</summary>
    private static CompactionState SyncModel(AgentInstance instance)
    {
        var state = instance.Compaction;
        var key = ModelKey(instance.Definition.Model);
        if (state.ModelKey != key)
        {
            state.ModelKey = key;
            state.ForgetUsage();
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
        CancellationToken ct,
        LedgerTurn? ledger = null)
    {
        var text = NoEvidenceText(instance, turns);
        instance.AppendText(text);
        instance.CommitText(text);
        // The note joins the text the run already streamed: it is delivered to the user as a delta of this turn, so
        // the row's answer text carries the whole run's words, the note included.
        instance.AppendRunText(text);
        var lastSeq = await PersistAsync(instance, new ChatMessage(MessageRole.Assistant, [new TextBlock(text)]), null, ct).ConfigureAwait(false);
        if (ledger is not null)
        {
            // The harness closed this turn; the row keeps the note as part of the run's own words.
            await SettleLedgerAsync(ledger, instance, instance.RunText, lastSeq, CancellationToken.None).ConfigureAwait(false);
        }

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
        await RunCallsAsync(
                calls,
                results,
                from,
                to,
                call => RunOneAsync(instance, tools, call, context, sink, ct))
            .ConfigureAwait(false);

        // The whole set has answered, each call into its own slot; only then does a cancelled turn end the run, so the tool
        // message it leaves behind names what every call actually did instead of marking them all cancelled.
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Runs one batch's calls concurrently and writes each outcome into <paramref name="results"/> at that call's index the
    /// moment it is known. The fan-out is total on purpose: a call stopped on its own becomes its own
    /// <see cref="CancelledByUserResult"/> and a failing one its own error result, so <see cref="Task.WhenAll(Task[])"/> is a
    /// rendezvous rather than a failure channel - one stopped call used to take the other three's finished results down with
    /// it, and the model was told all four were cancelled while three children were still running. Ending a cancelled turn
    /// is the caller's business, after the set has answered.
    /// </summary>
    internal static async Task RunCallsAsync(
        ToolCallBlock[] calls,
        ContentBlock?[] results,
        int from,
        int to,
        Func<ToolCallBlock, Task<ContentBlock>> run)
    {
        var batch = new Task[to - from];
        for (var index = from; index < to; index++)
        {
            batch[index - from] = RunIntoAsync(calls[index], results, index, run);
        }

        await Task.WhenAll(batch).ConfigureAwait(false);

        static async Task RunIntoAsync(
            ToolCallBlock call,
            ContentBlock?[] results,
            int index,
            Func<ToolCallBlock, Task<ContentBlock>> run)
        {
            try
            {
                results[index] = await run(call).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new ToolResultBlock(call.Id, CancelledByUserResult, IsError: true);
            }
            catch (Exception exception)
            {
                results[index] = new ToolResultBlock(call.Id, exception.Message, IsError: true);
            }
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

        var sideEffect = ToolRegistry.SideEffectOf(tool, call);
        // A read-only call is allowed before its scope is ever read, so that scope is never computed: doing it would parse the
        // arguments of every read/ls/find/grep for an answer nobody consults.
        var scope = sideEffect == ToolSideEffect.ReadOnly
            ? ToolCallScope.InsideWorkspace
            : ToolScopes.Of(tool, call, instance.WorkingDirectory);
        var request = new ApprovalRequest(
            call.Name,
            call.ArgumentsJson,
            sideEffect,
            instance.WorkingDirectory,
            Scope: scope);

        // The loop has committed to this call, whether or not the user still has to allow it: the row appears now, and says
        // which half it is in. Emitting this before the gate is what keeps a call that waits for the user on screen instead
        // of showing nothing until the answer arrives.
        var mode = services.Policy.Evaluate(request);
        var awaitingApproval = mode == ApprovalMode.Ask;
        sink.Emit(new AgentToolStarted(instance.Id, call, awaitingApproval));

        if (!await IsApprovedAsync(mode, request, ct).ConfigureAwait(false))
        {
            // Denied: the denied event is this call's terminal one (a denied call is never invoked), so no finished follows it.
            sink.Emit(new AgentApprovalDenied(instance.Id, call));
            return new ToolResultBlock(call.Id, "The user denied this tool call.", IsError: true);
        }

        // The call's own token, linked to the turn's: a turn cancel still reaches every call, and CancelToolCall reaches this
        // one only. Registered before the running event, so a stop pressed as soon as the row shows one finds it.
        var callSource = toolCalls.Begin(instance.Id, call.Id, ct);
        ToolResult result;
        try
        {
            if (awaitingApproval)
            {
                // It was waiting on the user; now it runs.
                sink.Emit(new AgentToolRunning(instance.Id, call));
            }

            // Only a call the policy put outside the workspace and the user allowed may resolve its paths outside it; a call
            // that stayed inside keeps the tools' own containment check as the second gate.
            var callContext = context with
            {
                Progress = tail => sink.Emit(new AgentToolProgress(instance.Id, call, tail)),
                OutOfWorkspaceAllowed = scope == ToolCallScope.OutsideWorkspace,
            };
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

    /// <summary>
    /// Answers whether the call may run. The caller has already evaluated the policy (that answer is what
    /// <see cref="AgentToolStarted"/> reports as <c>AwaitingApproval</c>), so only the asking branch is left here.
    /// </summary>
    private async ValueTask<bool> IsApprovedAsync(ApprovalMode mode, ApprovalRequest request, CancellationToken ct)
    {
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
        // A single text block is already the flat text - the shape of every message the loop composes - and the
        // ledger's begin reads the opening user message's text on the hot path of every turn, so the fast path is the
        // one that keeps a steady-state request free of the builder the multi-block shape needs.
        if (message.Content is [TextBlock only])
        {
            return only.Text;
        }

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
