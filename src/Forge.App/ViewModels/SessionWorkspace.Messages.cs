using System.Collections.ObjectModel;
using System.Text;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.Core.Agents;

namespace Forge.App.ViewModels;

/// <summary>
/// The session-messaging half of a session tab (forge-core "session messaging"/"reply receipt"):
/// the <c>@</c> session mentions that authorize delivery, the revocable marks above the input, running a message another
/// session delivered, and showing the receipt that comes back.
/// <para>A tab is the initiator when the user sends a message carrying a picked session mention, and the target when
/// <c>message_session</c> picks it: the root's own turns are run here, a user-driven session's turns are run here too while
/// its tab is open (Core drives them itself only when it is not). Receipts never start a turn - a busy tab takes them at the
/// next tool-batch boundary and an idle one drops them into the transcript as an unread message.</para>
/// </summary>
internal sealed partial class SessionWorkspaceViewModel
{
    /// <summary>
    /// Session mentions picked while this tab was still a draft: the child's session row (and with it the id a grant is
    /// written under) only exists once its first run announced itself, so the grants are written then.
    /// </summary>
    private readonly List<SessionMention> deferredMentions = [];

    /// <summary>Replaced by the shell whenever a session of this tree appears, disappears or is renamed.</summary>
    private IReadOnlyList<SessionMentionTarget> sessionMentionTargets = [];

    /// <summary>
    /// The shell's receipt path, <c>(initiatorSessionId, text)</c>: the window writes a receipt into the session's history and
    /// then draws the card, which is what a receipt delivered at a turn's tool-batch boundary gets. Null on a standalone view,
    /// which then draws the card alone.
    /// </summary>
    private Action<string, string>? receiptSink;

    /// <summary>The grants this session holds, shown above the input as one revocable mark each.</summary>
    public ObservableCollection<SessionAuthorizationMarkViewModel> SessionAuthorizations { get; } = [];

    public bool HasSessionAuthorizations => SessionAuthorizations.Count > 0;

    /// <summary>The runtime this tab runs on; null for a read-only view. The shell's message endpoint reads it.</summary>
    internal SessionRuntime? Runtime => runtime;

    /// <summary>
    /// The id the grants of this tab are written under: the root's session id on an ordinary tab, the child's own session id
    /// on an agent-session tab (the draft's placeholder id is never used - such a tab defers its mentions).
    /// </summary>
    private string InitiatorSessionId => agentSession is null ? Info.Id : agentChildSessionId ?? Info.Id;

    /// <summary>Replaces the <c>@</c> picker's session group; a popup already open on a mention token refills at once.</summary>
    internal void SetSessionMentionTargets(IReadOnlyList<SessionMentionTarget> targets)
    {
        sessionMentionTargets = targets ?? [];
        Composer.SetSessionMentions(sessionMentionTargets);
    }

    /// <summary>
    /// Re-reads the grants of this tab into the input area's marks. Called after a send, a revoke, a tab close and whenever the
    /// shell learns that the target ended or the root left Active.
    /// </summary>
    internal void RefreshSessionAuthorizations()
    {
        if (disposed)
        {
            return;
        }

        var wanted = runtime?.SessionMessageGrants(InitiatorSessionId) ?? [];
        SessionAuthorizations.Clear();
        foreach (var grant in wanted)
        {
            SessionAuthorizations.Add(new SessionAuthorizationMarkViewModel(grant, OnRevokeSessionAuthorization));
        }

        OnPropertyChanged(nameof(HasSessionAuthorizations));
    }

    /// <summary>The user pressed the revoke button of one mark: that pair is gone for good (a new <c>@</c> pick is a new grant).</summary>
    private void OnRevokeSessionAuthorization(SessionAuthorizationMarkViewModel mark)
    {
        runtime?.RevokeSessionMessage(InitiatorSessionId, mark.TargetSessionId);
        RefreshSessionAuthorizations();
    }

    /// <summary>
    /// <paramref name="targetSessionId"/> was archived or closed: its grants are dead with it, so its marks go too
    /// (forge-core: a grant lives until the target session ends).
    /// </summary>
    internal void ForgetMessageTarget(string targetSessionId)
    {
        if (string.IsNullOrEmpty(targetSessionId))
        {
            return;
        }

        runtime?.RevokeTargetSessionMessages(targetSessionId);
        RefreshSessionAuthorizations();
    }

    /// <summary>The root this tab hangs under left <see cref="SessionStatus.Active"/>: every grant of its tree goes.</summary>
    internal void RevokeSessionMessages()
    {
        runtime?.RevokeRootSessionMessages();
        deferredMentions.Clear();
        RefreshSessionAuthorizations();
    }

    /// <summary>
    /// Turns the session mentions of one outgoing message into grants. Only a mention the user picked in the picker reaches
    /// here - typed <c>@text</c> never becomes one - which is what makes "the user pointed at it" the whole authorization.
    /// </summary>
    private void AuthorizeMentions(IReadOnlyList<SessionMention> mentions)
    {
        if (mentions.Count == 0 || runtime is null)
        {
            return;
        }

        if (agentSession is not null && agentChildSessionId is null)
        {
            // A draft's session row - and with it the id the grant belongs to - arrives with the first run.
            foreach (var mention in mentions)
            {
                deferredMentions.RemoveAll(held => string.Equals(held.SessionId, mention.SessionId, StringComparison.Ordinal));
                deferredMentions.Add(mention);
            }

            return;
        }

        var initiator = InitiatorSessionId;
        foreach (var mention in mentions)
        {
            runtime.GrantSessionMessage(initiator, mention.SessionId, mention.Name);
        }

        RefreshSessionAuthorizations();
    }

    /// <summary>
    /// The first run of a draft named its session row: the grants the user picked before it existed can be written now.
    /// </summary>
    private void FlushDeferredMentions()
    {
        if (deferredMentions.Count == 0 || runtime is null || agentChildSessionId is null)
        {
            return;
        }

        var initiator = agentChildSessionId;
        foreach (var mention in deferredMentions)
        {
            runtime.GrantSessionMessage(initiator, mention.SessionId, mention.Name);
        }

        deferredMentions.Clear();
        RefreshSessionAuthorizations();
    }

    /// <summary>
    /// Runs one turn of this tab for a message another session delivered (<c>message_session</c>). It goes through the same
    /// automation path every programmatic turn uses: the transcript shows it with its origin, the checkpoint and the metrics
    /// row are this session's own, and the answer is what the caller writes into the receipt. Null when the tab is busy -
    /// the caller keeps waiting, a delivery is queued, never refused.
    /// </summary>
    internal async Task<SessionTurnOutcome?> RunDeliveredTurnAsync(string message, string fromName)
    {
        if (disposed || isReadOnlyView || runtime is null || IsRunning || !IsAutomationIdle)
        {
            return null;
        }

        var previous = Transcript.Current;
        var result = await RunAutomationTurnAsync(message, AgentSessionStrings.DeliveredFrom(fromName), agent: null)
            .ConfigureAwait(true);
        var card = Transcript.Current;
        var ran = !ReferenceEquals(card, previous) && card is not null;
        var report = ran ? LastTextOf(card!) : string.Empty;
        var (status, reason) = result.Status switch
        {
            MailboxStatus.Completed => (SessionReplyStatus.Completed, (string?)null),
            MailboxStatus.TurnFailed => (SessionReplyStatus.Failed, result.Reason),
            // "Withdrawn" is a turn that was stopped or a session that could not take it any more: either way the answer
            // the initiator reads is "stopped", never a silent success.
            _ => (SessionReplyStatus.Stopped, result.Reason),
        };
        // The turn number is only this delivery's when a turn really ran: a rejected turn opened no card, and pointing the
        // receipt's change line at a number somebody else's work was recorded under would be worse than no change line at all.
        return new SessionTurnOutcome(report, status, reason, ran ? turnNumber : 0);
    }

    /// <summary>The last prose block of one turn card: what the target's report line carries.</summary>
    private static string LastTextOf(TurnCardViewModel card)
    {
        var builder = new StringBuilder();
        foreach (var block in card.Blocks)
        {
            if (block is TurnTextViewModel text)
            {
                builder.Clear();
                builder.Append(text.CopyText);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Hands one receipt to this tab's running turn, so the loop delivers it at the next tool-batch boundary of the very turn
    /// that is running (<see cref="AgentInstance.Interject(string)"/>). False when no turn runs here.
    /// </summary>
    internal bool TryInterjectSessionMessage(string text)
    {
        if (disposed || isReadOnlyView || runtime is null || !IsRunning || !SessionReplyViewModel.IsReply(text))
        {
            return false;
        }

        return runtime.TryInterjectSessionMessage(agentSession is null ? SessionInfoId : agentChildSessionId ?? SessionInfoId, text);
    }

    /// <summary>The session id of this tab's own row; a draft agent session has none, so its receipts wait in the store path.</summary>
    private string SessionInfoId => Info.Id;

    /// <summary>
    /// Shows one receipt in this tab's transcript. <paramref name="unseen"/> marks the tab and its tree row as unread (the user
    /// is looking at another tab), which is the same mark an ended agent-session turn raises.
    /// </summary>
    internal void ShowSessionReply(string text, bool unseen)
    {
        if (disposed)
        {
            return;
        }

        Transcript.AddSessionReply(text, DateTimeOffset.Now);
        if (unseen && !HasAgentUnread)
        {
            HasAgentUnread = true;
            AgentTreeChanged?.Invoke();
        }
    }

    /// <summary>
    /// Puts one receipt the running turn never reached its boundary for in front of everybody who has to see it: the shell's
    /// path writes it into the session's history (the next turn's context has it, and a restart replays it as the card) and then
    /// draws it. <see cref="ShowSessionReply"/> alone only drew it, so the reply stopped at the screen: the next turn answered
    /// as if it never came, and a restart took the card away. This tab is idle whenever the caller runs (a turn that took its
    /// receipt at a boundary never leaves one behind), so the shell's append branch is the one that fires.
    /// </summary>
    private void DeliverSessionReply(string text)
    {
        if (disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // A draft agent session has no row yet, so there is no id a receipt could be stored under; its leftovers wait until
        // the session it belongs to exists.
        var initiator = agentSession is null ? Info.Id : agentChildSessionId;
        if (receiptSink is { } deliver && initiator is { Length: > 0 })
        {
            deliver(initiator, text);
            return;
        }

        ShowSessionReply(text, unseen: true);
    }

    /// <summary>
    /// Interjections of a finished agent-session turn that the loop never delivered. A receipt among them is not sent as a new
    /// turn (that is the unattended loop the feature forbids): it goes through the shell's path like the root's leftovers
    /// (<see cref="DeliverSessionReply"/>). The root instance's own inbox is never read here - it belongs to the root's tab.
    /// </summary>
    internal void TakeUndeliveredReplies()
    {
        if (disposed)
        {
            return;
        }

        AgentInstance? child = agentInstance;
        if (child is null)
        {
            return;
        }

        foreach (var leftover in child.TakeInterjections())
        {
            if (SessionReplyViewModel.IsReply(leftover.Text))
            {
                DeliverSessionReply(leftover.Text);
            }
        }
    }

    /// <summary>Puts session mentions into the picker of a tab the shell has just built.</summary>
    internal void RefreshSessionMentions()
    {
        if (disposed)
        {
            return;
        }

        SetSessionMentionTargets(sessionMentionSource?.Invoke() ?? []);
    }

    /// <summary>The shell's source of the sessions this tab may mention; null on a read-only view.</summary>
    private Func<IReadOnlyList<SessionMentionTarget>>? sessionMentionSource;

    /// <summary>Wires the sessions this tab may mention (the shell owns the list: open and stored sessions of the root).</summary>
    internal void SetSessionMentionSource(Func<IReadOnlyList<SessionMentionTarget>>? source)
    {
        sessionMentionSource = source;
        RefreshSessionMentions();
    }
}

/// <summary>
/// One revocable mark above the input ("Authorized: X"): the target of one grant of this session, with the button that withdraws
/// it. Reads its texts on every access so a language switch needs no rebuild of the collection.
/// </summary>
internal sealed class SessionAuthorizationMarkViewModel : NotifyingObject
{
    private readonly Action<SessionAuthorizationMarkViewModel> revoke;

    public SessionAuthorizationMarkViewModel(SessionGrant grant, Action<SessionAuthorizationMarkViewModel> revoke)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(revoke);
        Grant = grant;
        this.revoke = revoke;
        RevokeCommand = new RelayCommand(() => this.revoke(this));
    }

    public SessionGrant Grant { get; }

    public string TargetSessionId => Grant.TargetSessionId;

    /// <summary>"Authorized: X" in the current language.</summary>
    public string Text => AgentSessionStrings.AuthorizationGranted(Grant.TargetName);

    public string RevokeText => AgentSessionStrings.AuthorizationRevoke;

    public string RevokeTip => AgentSessionStrings.AuthorizationRevokeTip(Grant.TargetName);

    public RelayCommand RevokeCommand { get; }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(RevokeText));
        OnPropertyChanged(nameof(RevokeTip));
    }
}
