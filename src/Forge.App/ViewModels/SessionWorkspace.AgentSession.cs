using System.ComponentModel;
using System.Globalization;
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

/// <summary>
/// One reading of an agent session's child instance: the usage and model-streaming time it had when the turn began or ended.
/// Both counters only grow, so a turn's own share is the difference between two readings of the same instance (see
/// <see cref="SessionWorkspaceViewModel.AgentTurnStream"/>); the instance is kept so a rebuilt one is never differenced
/// against its predecessor. A class rather than a struct because the run thread writes the start reading and the UI thread
/// reads it whole.
/// </summary>
internal sealed record AgentSessionCounters(TokenUsage Usage, TimeSpan Stream, object Instance);

/// <summary>The texts an agent-session tab derives that are not localized sentences: the label separator and the default name.</summary>
internal static class AgentSessionLabel
{
    /// <summary>Between the root's title and the child's; a glyph, so it needs no translation.</summary>
    public const string Separator = " › ";

    /// <summary>Before a tab or row label whose reply the user has not looked at yet.</summary>
    public const string UnreadMark = "● ";

    /// <summary>The default child name: the agent kind, a space, then the ordinal within the root ("Worker 1").</summary>
    public static string Default(string kind, int ordinal) =>
        kind + " " + ordinal.ToString(CultureInfo.InvariantCulture);
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
    /// tokens and its outcome come from (forge-core: usage is per instance, so an agent session's numbers are its own run's
    /// and never the root instance's counters). Reset when each turn begins.
    /// </summary>
    private AgentSubAgentFinished? agentRunEnded;

    /// <summary>
    /// The child instance's counters as the running turn began, read from the run's start event (ahead of its first model
    /// request) or from the instance the tab already holds. The counters only grow, so the turn's own model-streaming time is
    /// the difference to the reading at its end (see <see cref="AgentTurnStream"/>); null before the first turn.
    /// </summary>
    private AgentSessionCounters? agentTurnStartCounters;

    /// <summary>
    /// The child instance's counters as they were last read. The tab's usage bar is fed from it, and a reading that is
    /// momentarily unavailable keeps this one instead of dropping the bar to zero.
    /// </summary>
    private AgentSessionCounters? agentLastCounters;

    /// <summary>
    /// Set when the run emitted its own turn-completed event, written on the run's thread before the run ends. That event
    /// already carries the instance's cumulative usage and stream time to the card and the strip, so the turn end must not
    /// add the same reading on top of it. Reset when each turn begins.
    /// </summary>
    private int agentTurnCompleted;

    private bool agentUnread;

    /// <summary>The child's own name, the store's <c>Title</c>: the default for its kind and ordinal until the user renames it.</summary>
    private string? agentName;

    /// <summary>
    /// The ordinal this tab's default name counts with: the shell's number for a draft, and for a tab opened from the store the
    /// number the stored default name carries (see <see cref="TryReadDefaultName"/>).
    /// </summary>
    private int agentOrdinal;

    /// <summary>
    /// True while <see cref="agentName"/> is still the name the agent kind and the ordinal derive, so a kind switch changes its
    /// prefix; a name the user typed clears it for good (forge-app "Agent 会话的界面与接线": the default name is
    /// "&lt;kind&gt; &lt;ordinal&gt;" and the prefix follows the kind, unless the user renamed it).
    /// </summary>
    private bool agentNameIsDerived;

    /// <summary>
    /// Names this tab wrote to the store on its own account (an agent-kind switch). The store echoes a rename back through
    /// <see cref="Rename"/>, and that echo says nothing about the user: reading it as a rename would end the follow on the very
    /// first kind switch.
    /// </summary>
    private readonly HashSet<string> agentSelfRenames = new(StringComparer.Ordinal);

    /// <summary>The root's title as a rename pushed it here; null until one arrives.</summary>
    private string? agentRootTitle;

    /// <summary>
    /// The live root title the shell can answer (the root's own tab, when it is open); null for a standalone view, which then has
    /// only the snapshot <see cref="AgentSessionLaunch.Root"/> carries.
    /// </summary>
    private readonly Func<string?>? liveRootTitle;

    /// <summary>True for a tab of a user-driven session under another session's root.</summary>
    public bool IsAgentSession => agentSession is not null;

    /// <summary>
    /// The child's own name. Derived from the agent kind and the tab's ordinal, then whatever the user renamed it to; the store's
    /// <c>Title</c> always holds exactly this, so the tree row and a reopened tab read the same name.
    /// </summary>
    public string AgentSessionName => agentName ??= AgentSessionLabel.Default(AgentNameKind, agentOrdinal);

    /// <summary>
    /// The kind the default name's prefix comes from: the one a turn would run on, so the name and the run never disagree.
    /// </summary>
    private string AgentNameKind => AgentSessionDefinition()?.Name ?? AgentSessionKinds.Default;

    /// <summary>The child session's id once the first message created its row; null while the tab is a draft.</summary>
    public string? AgentChildSessionId => agentChildSessionId;

    /// <summary>The root session this tab's child hangs under; null for an ordinary session tab.</summary>
    public SessionInfo? AgentSessionRoot => agentSession?.Root;

    /// <summary>
    /// The root's title as it stands now. The shell's live root answers first (its own tab, when it is open), the title a rename
    /// pushed here second - a root whose own tab is closed has no view left to read - and the snapshot the tab was opened with
    /// last: a renamed root must not keep its old name as the prefix of a child tab's label.
    /// </summary>
    private string CurrentAgentRootTitle(AgentSessionLaunch launch) =>
        liveRootTitle?.Invoke() ?? agentRootTitle ?? launch.Root.Title;

    /// <summary>The root session was renamed: this tab's "root › child" prefix follows it.</summary>
    internal void RenameRoot(string title)
    {
        if (agentSession is null || string.Equals(agentRootTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        agentRootTitle = title;
        RaiseAgentTitleChanged();
    }

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
                RaiseAgentTitleChanged();
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
        RaiseAgentTitleChanged();
    }

    /// <summary>The tab's label moved (the child's name, the root's, or the unread mark): the shell re-titles the tab with it.</summary>
    private void RaiseAgentTitleChanged()
    {
        OnPropertyChanged(nameof(Title));
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
    /// The tab's name followed an agent-kind switch. Args are this view and the new name: the shell writes a stored session's row
    /// through the same store path a rename takes, so the tree row and the <c>@</c> picker read the new name (a draft has no row
    /// and keeps it here).
    /// </summary>
    public event Action<SessionWorkspaceViewModel, string>? AgentNameFollowedKind;

    /// <summary>
    /// The agent and model this tab's child runs with right now: the kind the composer's agent chip holds, with the model, the
    /// effort and the provider its model chip holds (forge-app "Agent 会话的界面与接线": changing either rebuilds the instance
    /// before the next turn). Recomputed on every call rather than kept: the chips are the state, and a definition cached here
    /// would go stale the moment the user picks another kind or model. Null when no kind of this session can drive one (every
    /// one of them may change git, which only the root may do).
    /// </summary>
    private AgentDefinition? AgentSessionDefinition()
    {
        var kind = Composer.SelectedAgent.Definition is { } selected && CanBeUserSession(selected)
            ? selected
            : FallbackAgentKind();
        return kind is null ? null : kind with { Model = Composer.SelectedModel };
    }

    /// <summary>
    /// The kind used when the chip's selection cannot drive a session: the default kind (the implementing agent), else the first
    /// kind that may. The chip normally cannot land there - an agent-session tab lists only kinds it may run and greys the rest -
    /// so this covers a kind file that changed after the chip was built.
    /// </summary>
    private AgentDefinition? FallbackAgentKind()
    {
        AgentDefinition? fallback = null;
        foreach (var option in Composer.Agents)
        {
            if (option.Definition is not { } definition || !CanBeUserSession(definition))
            {
                continue;
            }

            if (string.Equals(definition.Name, AgentSessionKinds.Default, StringComparison.Ordinal))
            {
                return definition;
            }

            fallback ??= definition;
        }

        return fallback;
    }

    /// <summary>
    /// Whether an instance built from <paramref name="held"/> already runs what the composer's chips now say: the kind, the
    /// model, the reasoning effort and the provider. Only these four decide a rebuild - the advice description and the tool
    /// list come from the same kind file and are not something the chips move (forge-app "Agent 会话的界面与接线": a change of
    /// agent, model or effort is what rebuilds the instance before the next turn).
    /// </summary>
    private static bool SameAgentModel(AgentDefinition held, AgentDefinition wanted) =>
        string.Equals(held.Name, wanted.Name, StringComparison.Ordinal)
        && string.Equals(held.Model.Model, wanted.Model.Model, StringComparison.Ordinal)
        && held.Model.Thinking == wanted.Model.Thinking
        && string.Equals(held.Model.ProviderId ?? string.Empty, wanted.Model.ProviderId ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Puts the agent chip on the kind this tab's stored session ran with, so a reopened tab shows the agent that wrote the
    /// conversation instead of always the default one. A kind the session kinds no longer offer (or one that may not drive a
    /// session any more) keeps the composer's default and says why once.
    /// </summary>
    private void SelectAgentSessionKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            // A draft has no row and a row written before the kind was stored has none either: the composer's default stands.
            return;
        }

        if (Composer.Agents.FirstOrDefault(option => string.Equals(option.Key, kind, StringComparison.Ordinal)) is { IsAvailable: true } option)
        {
            Composer.SelectedAgent = option;
            return;
        }

        Transcript.AddNotice(LocText.Literal(AgentSessionStrings.KindFallback(kind, Composer.AgentLabel)), isError: true);
    }

    /// <summary>
    /// Whether a stored title is still the default name the derivation writes ("Worker 1"), answering the ordinal it counts with.
    /// The store keeps no "renamed" bit, so a tab opened from it recognises its own default name only by that shape - once; the
    /// flag decides everything after. A title whose prefix is neither the kind the row was created with nor a kind this tab runs
    /// is a name the user typed.
    /// </summary>
    private bool TryReadDefaultName(string? title, out int ordinal)
    {
        ordinal = 0;
        if (title?.LastIndexOf(' ') is not { } cut || cut <= 0)
        {
            return false;
        }

        if (!int.TryParse(title.AsSpan(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            return false;
        }

        var kind = title[..cut];
        ordinal = number;
        return string.Equals(kind, Info.AgentKind, StringComparison.Ordinal)
            || Composer.Agents.Any(option => string.Equals(option.Key, kind, StringComparison.Ordinal));
    }

    /// <summary>
    /// The composer's agent kind moved: while the name is still the one the kind and the ordinal derive, its prefix changes with
    /// it ("Worker 1" becomes "Scout 1", forge-app "Agent 会话的界面与接线"). A name the user typed is never touched. A stored
    /// session's new name goes to the store through the shell, so the tree row and the <c>@</c> picker follow; a draft keeps it on
    /// this view and it becomes the row's <c>Title</c> when the first message creates the row.
    /// </summary>
    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!agentNameIsDerived || !string.Equals(e.PropertyName, nameof(ComposerViewModel.SelectedAgent), StringComparison.Ordinal))
        {
            return;
        }

        var name = AgentSessionLabel.Default(AgentNameKind, agentOrdinal);
        if (string.Equals(name, AgentSessionName, StringComparison.Ordinal))
        {
            return;
        }

        agentName = name;
        OnPropertyChanged(nameof(AgentSessionName));
        RaiseAgentTitleChanged();
        if (agentChildSessionId is { Length: > 0 })
        {
            // Its own write: the store's echo of this name must not be read as a rename the user made (see Rename).
            agentSelfRenames.Add(name);
            AgentNameFollowedKind?.Invoke(this, name);
        }
    }

    /// <summary>The store write of a kind switch came back (or failed): its echo is no longer pending.</summary>
    internal void EndAgentKindRename(string name) => agentSelfRenames.Remove(name);

    /// <summary>
    /// The instance this turn runs on: the one the host holds for this tab's session, rebuilt from the store when the composer's
    /// chips now ask for another kind, model or provider. A rebuild discards the held instance and resumes the session - the
    /// history comes back through <c>SessionHistory.LoadAsync</c>, exactly like after a restart - so the conversation survives,
    /// the new prompt prefix makes the provider's cache miss once, and the counters start over at zero for the new instance
    /// (which is why the tab's usage baselines are dropped here: differencing readings of two instances is meaningless).
    /// </summary>
    private async Task<AgentInstance> ResolveAgentChildAsync(SessionRuntime live, AgentDefinition definition, CancellationToken ct)
    {
        // By the stored session id, not by the agent id the last run announced: a tab closed and opened again does not know that
        // id, yet the host still holds the instance it must not keep once the chips moved.
        var held = live.FindUserChild(agentChildSessionId!);
        if (held is not null && SameAgentModel(held.Definition, definition))
        {
            return held;
        }

        if (held is not null)
        {
            // Forget it first: the resume is idempotent, so a held instance would come straight back and the new definition would
            // never take effect. A run still on its way is untouched - the forget only drops the instance the next turn builds on.
            live.ForgetUserChild(held.Id);
        }

        agentInstance = null;
        Volatile.Write(ref agentAgentId, null);
        agentUsage.Remove("root");
        agentStream.Remove("root");
        return await live.ResumeUserChildAsync(agentChildSessionId!, WorkDir, definition, ct).ConfigureAwait(true);
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
        Interlocked.Exchange(ref agentTurnCompleted, 0);
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
                // One message is one run of the same child. The instance is the one this tab (or its host) already holds, and it
                // is rebuilt from the store when the chips now ask for another kind, model or provider: the change then takes
                // effect from this turn on, while the turn that already ran kept the definition it started with.
                var child = await ResolveAgentChildAsync(live, definition, token).ConfigureAwait(true);
                agentInstance = child;
                BeginAgentCounters();
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

                    // This turn's numbers out of the child's own instance: the reading it had when the turn began against the
                    // one it has now, never the root runtime's counters and never a wall clock. An instance the host no longer
                    // holds (a stopped run) leaves the stream time at zero and the row's outcome as the run reported it.
                    var counters = ReadAgentCounters();
                    var stream = AgentTurnStream(counters);
                    RecordAgentTurnMetrics(childSessionId, number, definition, error, stream);
                    if (!disposed && Volatile.Read(ref agentTurnCompleted) == 0)
                    {
                        // A run that stopped or failed emits no turn-completed event, so nothing has shown its spend or its
                        // streaming time yet: the instance reading fills that in. When the event did come it is the card's
                        // and the strip's source, and adding the same reading again would count the turn twice.
                        NoteAgentTurnUsage(counters);
                        NoteAgentTurnStream(counters, stream);
                    }

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
                // A receipt interjected into this turn that the loop never reached its boundary for: it goes through the
                // shell's idle path (stored, then drawn) instead of waiting for a turn the user never asked for. It runs
                // after the run is marked idle on purpose - while it looked busy, the shell would hand the receipt straight
                // back into the run that just ended - and before TurnCompleted, which may dispose this view (the tab was
                // closed while the turn ran) and would take the leftovers with it.
                TakeUndeliveredReplies();
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
    /// the numbers of its own run: the tokens are the ones its <see cref="AgentSubAgentFinished"/> carries (forge-core: usage is
    /// per instance), so the root instance's counters are never read here - charging the child with them would count the
    /// root's spend a second time. The stream time is the instance's own difference over the turn
    /// (<paramref name="streamTime"/>): the column records the model's streaming and never the run's wall-clock span, and a
    /// reading that was gone writes 0, which is a fact the table can hold (NOT NULL, no fallback).
    /// </summary>
    private void RecordAgentTurnMetrics(string childSessionId, int turn, AgentDefinition definition, LocText? error, TimeSpan streamTime)
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
                // The run's own tokens as the difference against an empty baseline, and this turn's model-streaming time as
                // the difference between the child instance's two readings (empty baseline: the difference is the span).
                run?.Usage ?? TokenUsage.Empty,
                TokenUsage.Empty,
                streamTime,
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
    /// The instance id of the tab's child: the one its run announced (written before the run's first request, so it is also
    /// what the run thread reads), else the one the tab holds for a resumed session that has not run here yet.
    /// </summary>
    private string? AgentChildAgentId => Volatile.Read(ref agentAgentId) ?? agentInstance?.Id;

    /// <summary>
    /// Takes the reading a turn is measured against: the child instance's cumulative counters as the run begins. The run's
    /// start event is the precise moment (it precedes the first model request); a later turn also reads it here, so a host
    /// that announces no start still measures the turn against the right reading.
    /// </summary>
    private void BeginAgentCounters()
    {
        if (ReadAgentCounters() is { } reading)
        {
            Volatile.Write(ref agentTurnStartCounters, reading);
        }
    }

    /// <summary>
    /// The child instance's cumulative counters, or null when this host no longer holds it (a stopped run, a released
    /// instance). Every successful reading is kept as the last one, so the tab's usage bar can show it rather than a zero.
    /// </summary>
    private AgentSessionCounters? ReadAgentCounters()
    {
        if (runtime is { } live && live.TryReadAgentCounters(AgentChildAgentId, out var counters) && counters.Instance is { } instance)
        {
            var reading = new AgentSessionCounters(counters.Usage, counters.Stream, instance);
            Volatile.Write(ref agentLastCounters, reading);
            return reading;
        }

        return null;
    }

    /// <summary>
    /// This turn's model-streaming time: the difference between the two readings of the same instance. Zero when either
    /// reading is missing or belongs to another instance - a turn's stream time is never the instance's lifetime and never the
    /// wall clock, so nothing is a better answer than a made-up one.
    /// </summary>
    private TimeSpan AgentTurnStream(AgentSessionCounters? end)
    {
        var start = Volatile.Read(ref agentTurnStartCounters);
        return end is { } streamed
               && start is { } began
               && ReferenceEquals(streamed.Instance, began.Instance)
               && streamed.Stream > began.Stream
            ? streamed.Stream - began.Stream
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Feeds the tab's own usage bar from the child instance, never from the root runtime's counters: this tab runs on the
    /// root's runtime, but the spend it shows is the child's. What this view already counted - the turn-completed events it
    /// saw - is the baseline, so a run that ended without one (a stop, a failure) still shows what it spent; a reading that is
    /// gone adds nothing, and the bar keeps the last numbers it had instead of dropping to zero.
    /// </summary>
    private void NoteAgentTurnUsage(AgentSessionCounters? counters)
    {
        if ((counters ?? Volatile.Read(ref agentLastCounters)) is not { } reading)
        {
            return;
        }

        var counted = agentUsage.TryGetValue("root", out var stored) ? stored : TokenUsage.Empty;
        var delta = new TokenUsage(
            Math.Max(0, reading.Usage.InputTokens - counted.InputTokens),
            Math.Max(0, reading.Usage.OutputTokens - counted.OutputTokens),
            Math.Max(0, reading.Usage.CacheReadTokens - counted.CacheReadTokens),
            Math.Max(0, reading.Usage.CacheWriteTokens - counted.CacheWriteTokens),
            Math.Max(0, reading.Usage.CacheWrite1hTokens - counted.CacheWrite1hTokens));
        if (delta.InputTokens == 0 && delta.OutputTokens == 0 && delta.CacheReadTokens == 0 && delta.CacheWriteTokens == 0
            && delta.CacheWrite1hTokens == 0)
        {
            return;
        }

        // The same per-agent bookkeeping a turn-completed event does, on the instance's cumulative scale: an event still queued
        // for the UI thread then finds nothing left to add, and the next one differences from this reading.
        agentUsage["root"] = reading.Usage;
        Instruments.AddTurn("root", delta);
    }

    /// <summary>
    /// Puts this turn's model-streaming time on the tab's turn card when the run emitted no turn-completed event of its own (a
    /// stop or a failure emits none), which would otherwise leave the divider without an output rate. The counter the event
    /// would difference from is kept on the instance's cumulative scale, so the next turn's event starts from the right place.
    /// </summary>
    private void NoteAgentTurnStream(AgentSessionCounters? counters, TimeSpan stream)
    {
        if (counters is not { } reading || stream <= TimeSpan.Zero)
        {
            return;
        }

        agentStream["root"] = reading.Stream;
        turnCard?.NoteStream(stream);
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

            // This turn's baseline, still ahead of the run's first model request: whatever the instance has streamed so far is
            // everything before this turn, so the row's stream time is the difference to the reading at the turn's end. A
            // draft's instance is new and reads zero, exactly like the root's first turn.
            BeginAgentCounters();

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

            if (value is AgentTurnCompleted)
            {
                // The run reports its own cumulative counters: the card and the strip take this turn's numbers from that event
                // (queued to the UI thread), so the turn end must not add the instance reading on top of it.
                Interlocked.Exchange(ref agentTurnCompleted, 1);
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
        // The row exists from now on, so it is the id the picker's mentions are granted under and the one the instance the
        // run just created is held by (a receipt that arrives before the next turn has to find it).
        agentInstance ??= runtime?.FindUserChild(childSessionId);
        FlushDeferredMentions();
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
