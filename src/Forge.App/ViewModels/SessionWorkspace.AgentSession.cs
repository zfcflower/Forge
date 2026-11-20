using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Notifications;
using Forge.Core.Agents;
using Forge.Core.Providers;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// What an agent-session tab carries that an ordinary session tab does not: the root session its child hangs under, the
/// ordinal its default name counts with, and whether it is still a draft.
/// <para>A draft tab has no session row yet: the row appears (through <c>AgentHost.StartUserChildAsync</c>) only when the
/// first message is sent, and its title is that message's default name. Closing a draft drops it without a trace.</para>
/// </summary>
internal sealed record AgentSessionLaunch(SessionInfo Root, int Ordinal, bool IsDraft);

/// <summary>The texts an agent-session tab derives that are not localized sentences: the label separator and the default name.</summary>
internal static class AgentSessionLabel
{
    /// <summary>Between the root's title and the child's; a glyph, so it needs no translation.</summary>
    public const string Separator = " › ";

    /// <summary>Before a tab or row label whose reply the user has not looked at yet.</summary>
    public const string UnreadMark = "● ";

    /// <summary>The default child name: the agent kind, a space, then the ordinal within the root ("Worker 1").</summary>
    public static string Default(string kind, int ordinal) =>
        kind + " " + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The agent-session half of a session tab: a user-driven child session of another session's root (forge-core "Agent 会话"),
/// shown in a main-group tab that runs on the same transcript, composer, queue and stop button as a root session.
/// <para>What changes is only who drives it: the first message creates the session row through
/// <c>AgentHost.StartUserChildAsync</c>, every later message is one more turn through <c>AgentHost.RunUserChildTurnAsync</c>,
/// and the stop button goes through the host's <c>SubAgentControl</c> (the same path the model's stop tool takes). Events of
/// that run arrive under the child's own agent id and are rewritten to <c>root</c> here, so this tab draws them as its own
/// conversation instead of as a sub-agent row.</para>
/// <para>Every turn is also the child's own: its checkpoint and its metrics row are keyed by the child's session id and
/// numbered among the child's turns from 1 (see <see cref="BeginAgentTurnAsync"/> and <see cref="RecordAgentTurnMetrics"/>),
/// so the "by turn" diff this tab shows is the child's work and its usage numbers are its own run's rather than the root
/// instance's.</para>
/// </summary>
internal sealed partial class SessionWorkspaceViewModel
{
    /// <summary>The root this tab's child hangs under; null for an ordinary session tab.</summary>
    private AgentSessionLaunch? agentSession;

    /// <summary>The live child instance, kept between turns; null until the first turn ended or a stored one was resumed.</summary>
    private AgentInstance? agentInstance;

    /// <summary>Agent id of the child's run as the start event named it, written off the UI thread.</summary>
    private string? agentAgentId;

    /// <summary>The kind this tab's child runs with; resolved once from the composer's kinds and kept for every turn.</summary>
    private AgentDefinition? agentDefinition;

    /// <summary>The stored child session's id; null while the tab is still a draft.</summary>
    private string? agentChildSessionId;

    /// <summary>
    /// The child's session row as the run announced it, completed on the run's thread by the start event: the draft's
    /// checkpoint has to be keyed by that id, and the run only names it after it has begun (see
    /// <see cref="RunAgentSessionTurnAsync"/>). Null outside the one turn that creates the row.
    /// </summary>
    private TaskCompletionSource<string>? agentChildCreated;

    /// <summary>
    /// The terminal event of the tab's own run, written on the run's thread and read at the turn end: it is where this turn's
    /// usage, elapsed time and outcome come from (forge-core: usage is per instance, so an agent session's numbers are its own
    /// run's and never the root instance's counters). Reset when each turn begins.
    /// </summary>
    private AgentSubAgentFinished? agentRunEnded;

    private bool agentUnread;

    /// <summary>The child's own name, the store's <c>Title</c>: the default for its kind and ordinal until the user renames it.</summary>
    private string? agentName;

    /// <summary>True for a tab of a user-driven session under another session's root.</summary>
    public bool IsAgentSession => agentSession is not null;

    /// <summary>
    /// The child's own name. Derived once from the agent kind and the tab's ordinal, then whatever the user renamed it to;
    /// the store's <c>Title</c> always holds exactly this, so the tree row and a reopened tab read the same name.
    /// </summary>
    public string AgentSessionName => agentName ??=
        AgentSessionLabel.Default(
            AgentSessionDefinition()?.Name ?? AgentSessionKinds.Default,
            agentSession?.Ordinal ?? 1);

    /// <summary>The child session's id once the first message created its row; null while the tab is a draft.</summary>
    public string? AgentChildSessionId => agentChildSessionId;

    /// <summary>The root session this tab's child hangs under; null for an ordinary session tab.</summary>
    public SessionInfo? AgentSessionRoot => agentSession?.Root;

    /// <summary>
    /// A reply arrived while this tab was not the one on screen: the tab's label and its tree row carry the mark until the
    /// tab is shown. Fed by the turn's own end, which is the signal the shell already has; no store read and no core change.
    /// </summary>
    public bool HasAgentUnread
    {
        get => agentUnread;
        internal set
        {
            if (Set(ref agentUnread, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                TabTitleChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// The session is on screen again: the mark goes. Called by the shell when this tab becomes the active one.
    /// </summary>
    internal void MarkAgentSeen()
    {
        agentUnread = false;
        OnPropertyChanged(nameof(HasAgentUnread));
        OnPropertyChanged(nameof(TabTitle));
        TabTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The first message created the session row: the shell re-keys the tab, the tree gets a row. Args are this view and the
    /// id the tab had as a draft.
    /// </summary>
    public event Action<SessionWorkspaceViewModel, string>? AgentSessionCreated;

    /// <summary>The child's run ended: the shell touches the root, marks the tab and refreshes the tree.</summary>
    public event Action<SessionWorkspaceViewModel>? AgentSessionTurnEnded;

    /// <summary>
    /// The agent and model this tab's child runs with, resolved from the composer's kinds: the launched one when the tab
    /// named it, else the default kind (the implementing agent), else the first kind that may drive a session. Null when no
    /// kind of this session can (every one of them may change git, which only the root may do).
    /// </summary>
    private AgentDefinition? AgentSessionDefinition()
    {
        if (agentDefinition is not null)
        {
            return agentDefinition;
        }

        AgentDefinition? fallback = null;
        foreach (var option in Composer.Agents)
        {
            if (option.Definition is not { } definition || !CanBeUserSession(definition))
            {
                continue;
            }

            if (string.Equals(definition.Name, AgentSessionKinds.Default, StringComparison.Ordinal))
            {
                return agentDefinition = definition;
            }

            fallback ??= definition;
        }

        return agentDefinition = fallback;
    }

    /// <summary>
    /// Whether an agent kind may drive a user session: the kind has to name its tools (an unrestricted agent could touch git,
    /// and only the root session changes git), and that list may hold no git write and no pull request tool.
    /// </summary>
    internal static bool CanBeUserSession(AgentDefinition definition) =>
        definition.ToolNames is { Count: > 0 } tools && !tools.Any(AgentSessionKinds.IsRootOnlyTool);

    /// <summary>One more message of this tab: the first creates the row, every later one is a turn of the same child.</summary>
    private async Task RunAgentSessionTurnAsync(ComposerPrompt prompt)
    {
        if (AgentSessionDefinition() is not { } definition)
        {
            // No kind of this session may drive a user session: the message never became a turn, so it goes back to the
            // composer instead of vanishing.
            Transcript.AddNotice(LocText.Literal(AgentSessionStrings.NoKind), isError: true);
            if (!HasDraft())
            {
                Draft = prompt.Text;
                Composer.RestoreAttachments(prompt.Attachments);
            }

            return;
        }

        var text = prompt.Text;
        if (!await PassesTurnGateAsync(prompt).ConfigureAwait(true))
        {
            return;
        }

        var live = runtime!;
        turnRan = true;
        turnRootFailure = null;
        turnRootQuota = null;
        TurnStarted?.Invoke(this, automationTurn);
        turnStartedAt = DateTimeOffset.Now;
        Interlocked.Exchange(ref lastActivityTicks, turnStartedAt.UtcTicks);
        Interlocked.Exchange(ref runningTools, 0);
        Interlocked.Exchange(ref agentRunEnded, null);
        turnToolCalls = 0;
        Transcript.AddUserMessage(text, turnStartedAt, prompt.Attachments);
        var card = Transcript.BeginTurn(turnStartedAt);
        turnCard = card;
        card.NoteModel(Composer.ProviderId is { Length: > 0 } provider ? provider : null, definition.Model.Model);
        turnNumber = card.Round;
        IsRunning = true;

        turn = new CancellationTokenSource();
        var token = turn.Token;
        LocText? error = null;

        // This turn's number in the child's own sequence: its checkpoint and its metrics row are keyed by the child's session
        // id and numbered from 1 among themselves (forge-core's agent-session decision), so neither the root instance's
        // counters nor its turn numbering enter here. Null while the draft has no session id to key anything by.
        int? turnNo = null;
        TaskCompletionSource<string>? created = null;
        try
        {
            if (agentChildSessionId is null)
            {
                // The first message is also the session row: nothing exists in the store before it, so a draft that is
                // closed again leaves nothing behind. The row's id is what the checkpoint is keyed by and the run only names
                // it on the wire, so the checkpoint opens from the start event - still ahead of the run's first request -
                // while the run itself is awaited below.
                created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref agentChildCreated, created);
                var running = live.StartUserChildAsync(
                    definition,
                    text,
                    prompt.Attachments,
                    AgentSessionName,
                    OnAgentSessionEvent,
                    token);
                await Task.WhenAny(created.Task, running).ConfigureAwait(true);
                if (created.Task.IsCompletedSuccessfully)
                {
                    turnNo = await BeginAgentTurnAsync(created.Task.Result, card.Round).ConfigureAwait(true);
                    turnNumber = turnNo.Value;
                }

                var outcome = await running.ConfigureAwait(true);
                if (outcome.IsError)
                {
                    error = LocText.Literal(outcome.Text);
                }
            }
            else
            {
                // One message is one run of the same child; the instance is rebuilt from the store only when this tab does
                // not hold it (a restart, or a tab opened from the tree after one was closed).
                var child = agentInstance ?? await live
                    .ResumeUserChildAsync(agentChildSessionId, WorkDir, definition, token)
                    .ConfigureAwait(true);
                agentInstance = child;
                turnNo = await BeginAgentTurnAsync(agentChildSessionId, card.Round).ConfigureAwait(true);
                turnNumber = turnNo.Value;
                await live.RunUserChildTurnAsync(child, text, prompt.Attachments, OnAgentSessionEvent, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            error = SessionStrings.Stopped;
        }
        catch (Exception exception) when (IsCheckpointFailure(exception) || exception is ProviderException)
        {
            error = LocText.Literal(exception.Message);
        }
        finally
        {
            try
            {
                // A stop the user asked for closes the run instead of raising here, so the card learns it from the run's own
                // end - the same way its metrics row does - instead of reading as a turn that went through.
                if (error is null && Volatile.Read(ref agentRunEnded) is { Status: SubAgentStatus.Stopped })
                {
                    error = SessionStrings.Stopped;
                }

                lastTurnError = error;
                Transcript.EndTurn(DateTimeOffset.Now - turnStartedAt, error);
                AgentSessionTurnEnded?.Invoke(this);

                // The turn's own numbers, keyed by the child's session id: one metrics row and one closed checkpoint per turn,
                // on success, failure and cancellation alike. The row the run created is read from the start event rather
                // than from the tab, which learns its id through the dispatcher and may not have been there yet.
                var childSessionId = created is { Task.IsCompletedSuccessfully: true } ? created.Task.Result : agentChildSessionId;
                if (childSessionId is { Length: > 0 })
                {
                    var number = turnNo ?? card.Round;
                    Instruments.NoteTurnEnded(number);
                    RecordAgentTurnMetrics(childSessionId, number, definition, error);
                    if (turnNo is not null)
                    {
                        await CompleteAgentTurnAsync(childSessionId, number).ConfigureAwait(true);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref agentChildCreated, null);
                this.turn?.Dispose();
                this.turn = null;
                IsRunning = false;
                TurnCompleted?.Invoke(this);
                ScheduleNext();
            }
        }
    }

    /// <summary>
    /// Opens this turn's checkpoint under the child's own session id and answers the turn number it was written with: the
    /// next one neither of the child's two tables has yet, so a reopened tab continues its own sequence instead of colliding
    /// with a stored turn, and numbered from 1 for a session that never ran. A store or git failure is a notice, never a lost
    /// turn.
    /// </summary>
    private async Task<int> BeginAgentTurnAsync(string childSessionId, int round)
    {
        var turn = Math.Max(1, round);
        if (checkpoints is null)
        {
            return turn;
        }

        try
        {
            var next = await BackgroundRead.RunAsync(t => checkpoints.NextTurnNumberAsync(childSessionId, t), CancellationToken.None)
                .ConfigureAwait(true);
            turn = Math.Max(turn, next);
            await Task.Run(() => checkpoints.BeginAsync(childSessionId, turn, WorkDir, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (IsCheckpointFailure(exception))
        {
            NoteCheckpointFailure(childSessionId, exception);
        }

        return turn;
    }

    /// <summary>Closes the checkpoint of a turn that ended, on success, failure and cancellation alike.</summary>
    private async Task CompleteAgentTurnAsync(string childSessionId, int turn)
    {
        if (checkpoints is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => checkpoints.CompleteAsync(childSessionId, turn, WorkDir, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (IsCheckpointFailure(exception))
        {
            NoteCheckpointFailure(childSessionId, exception);
        }
    }

    /// <summary>
    /// Hands the child's turn to the same metrics writer the root session uses, keyed by the child's own session id and with
    /// the numbers of its own run: the tokens and the elapsed time are the ones its <see cref="AgentSubAgentFinished"/> carries
    /// (forge-core: usage is per instance), so the root instance's counters are never read here - charging the child with
    /// them would count the root's spend a second time.
    /// </summary>
    private void RecordAgentTurnMetrics(string childSessionId, int turn, AgentDefinition definition, LocText? error)
    {
        if (metricsRecorder is not { Enabled: true } recorder)
        {
            return;
        }

        try
        {
            // The run's own terminal event says how the turn went: a stop the user asked for closes the run instead of raising
            // here, so the outcome the row records is read from there first and the turn's own error only fills the gap.
            var run = Interlocked.Exchange(ref agentRunEnded, null);
            LocText? outcome = run switch
            {
                { Status: SubAgentStatus.Stopped } => SessionStrings.Stopped,
                { Status: SubAgentStatus.Failed, Error: { Length: > 0 } failure } => LocText.Literal(failure),
                _ => error,
            };
            recorder.Record(TurnMetricsRowFactory.Build(
                childSessionId,
                turn,
                turnStartedAt,
                DateTimeOffset.UtcNow,
                outcome,
                run?.Error ?? turnRootFailure,
                // The run's own numbers as the difference against an empty baseline, and its elapsed time in the column a root
                // turn fills with the model's streaming time: a sub-agent run is measured as one span from spawn to finish
                // (queueing included), and that span is the only duration its finished event reports.
                run?.Usage ?? TokenUsage.Empty,
                TokenUsage.Empty,
                run?.Elapsed ?? TimeSpan.Zero,
                TimeSpan.Zero,
                turnToolCalls,
                definition.Model.ProviderId is { Length: > 0 } declared
                    ? declared
                    : Composer.ProviderId is { Length: > 0 } chosen
                        ? chosen
                        : null,
                definition.Model.Model));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The writer promises never to throw, but a row is never worth a turn: this is the belt to that suspender.
            AppLogs.Usage.ZLogWarning($"agent session metrics row not queued: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// One event of this tab's own run. The run announces itself under the root session's id and the child's own id, which
    /// this tab does not draw as a sub-agent row: the start names the child (that is how the draft learns its session id), the
    /// run's own lifecycle ends are absorbed, and everything the child says is rewritten to <c>root</c> so it lands in this
    /// tab's transcript.
    /// </summary>
    private void OnAgentSessionEvent(AgentEvent value)
    {
        var own = Volatile.Read(ref agentAgentId);
        if (value is AgentSubAgentStarted started
            && (own is null || string.Equals(started.ChildId, own, StringComparison.Ordinal)))
        {
            // The tab's own run announced itself (before anything else can: the start is emitted ahead of the loop). A run
            // started under the same session id names the child that is this tab, so it is the tab's identity, not a row.
            if (own is null)
            {
                Volatile.Write(ref agentAgentId, started.ChildId);
            }

            // The draft's first checkpoint is keyed by the row this run just created, so the id is handed over here, on the
            // run's thread and before its first request (see RunAgentSessionTurnAsync).
            if (!string.IsNullOrEmpty(started.ChildSessionId))
            {
                Volatile.Read(ref agentChildCreated)?.TrySetResult(started.ChildSessionId);
            }

            Forge.App.Approval.UiThreadDispatcher.Instance.Post(() => MaterializeAgentSession(started.ChildSessionId));
            return;
        }

        if (own is not null && string.Equals(value.AgentId, own, StringComparison.Ordinal))
        {
            // This tab's own run: its lifecycle ends are the turn's, not a sub-agent row's, and everything else it says is
            // this conversation.
            if (value is AgentSubAgentFinished finished)
            {
                // Where this turn's metrics row gets its tokens, elapsed time and outcome from (see RecordAgentTurnMetrics).
                Interlocked.Exchange(ref agentRunEnded, finished);
                return;
            }

            if (value is AgentSubAgentQueued or AgentSubAgentRunning)
            {
                return;
            }

            OnAgentEvent(value with { AgentId = "root" });
            return;
        }

        // A sub-agent the child spawned (depth 2): an ordinary agent row of this tab.
        OnAgentEvent(value);
    }

    /// <summary>
    /// The session row of this tab exists: its id replaces the draft's, so a reopen finds this tab instead of a second one,
    /// and the tree can draw the row. Late (a second start event of a later turn) is a no-op.
    /// </summary>
    private void MaterializeAgentSession(string childSessionId)
    {
        if (disposed || agentChildSessionId is not null || string.IsNullOrEmpty(childSessionId) || agentSession is not { } launch)
        {
            return;
        }

        var previous = Info.Id;
        agentChildSessionId = childSessionId;
        Info = Info with
        {
            Id = childSessionId,
            ParentSessionId = launch.Root.Id,
            ChildDriver = AgentSessionKinds.ChildDriverUser,
            AgentKind = AgentSessionDefinition()?.Name,
            Title = AgentSessionName,
        };
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(TabTitle));
        TabTitleChanged?.Invoke(this, EventArgs.Empty);
        AgentSessionCreated?.Invoke(this, previous);
        AgentTreeChanged?.Invoke();
    }
}

/// <summary>The two facts of an agent session's kind that the tab needs and the composer's chip columns do not own yet.</summary>
internal static class AgentSessionKinds
{
    /// <summary>The kind an agent session starts on: the implementing agent, the one the user's message is meant for.</summary>
    public const string Default = "Worker";

    /// <summary>
    /// What <c>sessions.child_driver</c> holds for a session the user opened under a root (forge-core
    /// <c>StorageSchema.ChildDriverUser</c>, which is internal to Core's assembly); every other child keeps null.
    /// </summary>
    public const string ChildDriverUser = "user";

    /// <summary>
    /// Tools only the root session may call: they change git (branch, sync) or publish a pull request. A kind whose whitelist
    /// names one of them cannot drive a user session, because only the root session owns the branch and the remote.
    /// </summary>
    private static readonly string[] RootOnlyTools = ["git_branch", "git_sync"];

    public static bool IsRootOnlyTool(string tool) =>
        RootOnlyTools.Contains(tool, StringComparer.Ordinal) || tool.StartsWith("pr_", StringComparison.Ordinal);
}
