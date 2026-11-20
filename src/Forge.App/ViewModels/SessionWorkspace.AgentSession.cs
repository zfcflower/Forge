using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.Core.Agents;
using Forge.Core.Providers;

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
        turnStartCounters = RootCounters.None;
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
        try
        {
            if (agentChildSessionId is null)
            {
                // The first message is also the session row: nothing exists in the store before it, so a draft that is
                // closed again leaves nothing behind.
                var outcome = await live
                    .StartUserChildAsync(definition, text, AgentSessionName, OnAgentSessionEvent, token)
                    .ConfigureAwait(true);
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
                lastTurnError = error;
                Transcript.EndTurn(DateTimeOffset.Now - turnStartedAt, error);
                AgentSessionTurnEnded?.Invoke(this);
            }
            finally
            {
                this.turn?.Dispose();
                this.turn = null;
                IsRunning = false;
                TurnCompleted?.Invoke(this);
                ScheduleNext();
            }
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

            Forge.App.Approval.UiThreadDispatcher.Instance.Post(() => MaterializeAgentSession(started.ChildSessionId));
            return;
        }

        if (own is not null && string.Equals(value.AgentId, own, StringComparison.Ordinal))
        {
            // This tab's own run: its lifecycle ends are the turn's, not a sub-agent row's, and everything else it says is
            // this conversation.
            if (value is AgentSubAgentFinished or AgentSubAgentQueued or AgentSubAgentRunning)
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
