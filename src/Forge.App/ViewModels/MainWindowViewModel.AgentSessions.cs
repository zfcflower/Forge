using System.Globalization;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Workbench;
using Forge.Core.Storage;
using Forge.Workspace;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The shell's half of an agent session (forge-app "Agent 会话的界面与接线", forge-core "Agent 会话"): opening a draft tab under a
/// worktree's active root session, turning that draft into a stored session when its first message goes out, opening one
/// again from the tree or from the restored layout, and keeping the tab's label, the tree row, the unread mark and the
/// root's idle state in step with it.
/// <para>The tab itself is an ordinary <see cref="SessionWorkspaceViewModel"/> in agent-session mode, so it is the same
/// transcript, composer, queue and stop button as a root session; what the shell adds is the bookkeeping around the id the
/// session gets when its row is created.</para>
/// </summary>
internal sealed partial class MainWindowViewModel
{
    /// <summary>
    /// How a draft tab's session id is spelled before its row exists; nothing in the store carries this id. It still has to be
    /// a legal storage identifier: a draft tab is an ordinary session view, so the panels and the history replay ask the store
    /// about this id, and a rejected identifier would surface as a read failure in the transcript and in the checklist panel
    /// instead of the empty answer a row-less session deserves.
    /// </summary>
    private const string AgentDraftIdPrefix = "agent-draft-";

    /// <summary>The id a draft tab carries until the first message creates its row.</summary>
    internal static string NewAgentDraftId(string rootSessionId, int sequence) =>
        AgentDraftIdPrefix + rootSessionId + "-" + sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>Open agent-session tabs by the id their session has right now (the draft's id until its row exists).</summary>
    private readonly Dictionary<string, TabModel> agentTabs = new(StringComparer.Ordinal);

    /// <summary>Agent-session views of those tabs, so a tab whose view is running can be found on a turn end.</summary>
    private readonly Dictionary<SessionWorkspaceViewModel, TabModel> agentViews = [];

    /// <summary>Drafts opened under a root, so their default names count up before any of them has a row.</summary>
    private readonly Dictionary<string, int> agentDrafts = new(StringComparer.Ordinal);

    /// <summary>Drafts whose tab was closed while a turn ran: their view goes once that turn ends.</summary>
    private readonly HashSet<SessionWorkspaceViewModel> agentViewsClosing = [];

    private int agentSessionSequence;

    /// <summary>
    /// The tree's "new agent session" entry: opens a draft tab under the worktree's active root session. The root's runtime is
    /// loaded here when it is not (its host builds with the first turn), and the root's own tab is neither opened nor brought
    /// to the front.
    /// </summary>
    internal async Task OpenAgentSessionAsync(SessionInfo root, CancellationToken ct)
    {
        SessionInfo? current;
        try
        {
            current = await BackgroundRead.RunAsync(
                    token => composition.Sessions.GetSessionAsync(root.Id, token),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.OpenFailed(exception.Message)));
            AppLogs.Session.ZLogWarning($"agent session refused: {exception.Message}");
            return;
        }

        if (current is null || current.Status != SessionStatus.Active)
        {
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.RootNotActive(root.Title)));
            return;
        }

        try
        {
            // Creating it here (not on the first turn) is what makes the tab usable at once: the runtime is what the child
            // is started in, and nothing about it needs the root's tab.
            composition.Runtimes.Get(current);
        }
        catch (ObjectDisposedException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.OpenFailed(root.Title)));
            return;
        }

        int stored;
        try
        {
            var children = await BackgroundRead.RunAsync(
                    token => composition.Catalog.QueryAsync(new SessionQuery { ParentSessionId = current.Id }, token),
                    ct)
                .ConfigureAwait(true);
            stored = children.Count(child => string.Equals(child.ChildDriver, AgentSessionKinds.ChildDriverUser, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Tree.ZLogWarning($"could not count the agent sessions of '{current.Id}': {exception.Message}");
            stored = 0;
        }

        var open = OpenAgentSessionCount(current.Id);
        var ordinal = 1 + stored + open;
        var draftId = NewAgentDraftId(current.Id, ++agentSessionSequence);
        agentDrafts[current.Id] = open + 1;
        OpenAgentSessionTab(CreateAgentSessionView(current, current, ordinal, isDraft: true, draftId), activate: true);
    }

    /// <summary>
    /// Opens a stored agent session by its session id: the tab of a tree row, and the one the restored layout names. A tab
    /// that is already open is brought forward instead of opened twice, and a tab that was closed while its turn ran comes
    /// back as the same live view - that turn keeps streaming and its pending approval cards keep answering - never as a
    /// second view replayed from the store under the run that is still writing to it.
    /// </summary>
    internal async Task OpenAgentSessionByIdAsync(string childSessionId, CancellationToken ct)
    {
        // A live tab is brought forward without touching the store. A read-only one is not: a restore may have made the row
        // Active since it was opened, and that tab has to be replaced by the live view below.
        if (agentTabs.ContainsKey(childSessionId)
            && FindOpenAgentSessionView(childSessionId) is { IsReadOnlyView: false })
        {
            ActivateAgentSessionTab(childSessionId);
            return;
        }

        SessionInfo? child;
        try
        {
            child = await BackgroundRead.RunAsync(
                    token => composition.Sessions.GetSessionAsync(childSessionId, token),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.OpenFailed(exception.Message)));
            return;
        }

        if (child is null
            || child.ParentSessionId is not { } rootId
            || !string.Equals(child.ChildDriver, AgentSessionKinds.ChildDriverUser, StringComparison.Ordinal))
        {
            return;
        }

        SessionInfo? root;
        try
        {
            root = await BackgroundRead.RunAsync(
                    token => composition.Sessions.GetSessionAsync(rootId, token),
                    ct)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.OpenFailed(exception.Message)));
            return;
        }

        if (root is null)
        {
            // A child whose parent row is gone has nothing to name or replay it under.
            Status.SetWorkspaceProblem(LocText.Literal(AgentSessionStrings.RootNotActive(rootId)));
            return;
        }

        // A history agent session (Archived / Deleted) opens read-only, like a history root row: no runtime, no input area.
        // Its root may be archived too - restoring the child puts it back under the branch's active root (or makes it the
        // root itself when there is none).
        var readOnly = child.Status != SessionStatus.Active || root.Status != SessionStatus.Active;

        // A tab that was closed while its turn still ran owns the session: that live view holds the stream and the pending
        // approval cards, and archiving the row closed its tab without ending the run. Reopening it is the only way back,
        // whatever the stored status says - and it must come before any read-only tab or new view of the same session.
        if (FindClosedAgentSessionView(child.Id) is { } running)
        {
            if (FindOpenAgentSessionView(child.Id) is { } stale)
            {
                if (agentViews.TryGetValue(stale, out var staleTab))
                {
                    Workbench.CloseTab(staleTab);
                }
            }

            ReopenAgentSessionTab(running);
            return;
        }

        // A second open of this same agent session - a double-click, the row and the palette at once - may have filed its tab
        // while this one was reading the store. A read-only tab whose session a restore has since made Active is replaced by
        // the live view, carrying its draft: reusing it would leave the tab read-only for good.
        if (FindOpenAgentSessionView(child.Id) is { } open)
        {
            if (!open.IsReadOnlyView || readOnly)
            {
                ActivateAgentSessionTab(child.Id);
                return;
            }

            var carried = open.TakeComposerState();
            if (agentViews.TryGetValue(open, out var staleTab))
            {
                Workbench.CloseTab(staleTab);
            }

            var liveView = CreateAgentSessionView(child, root, ordinal: 0, isDraft: false, draftId: child.Id, readOnly: false);
            liveView.RestoreComposerState(carried);
            OpenAgentSessionTab(liveView, activate: true);
            try
            {
                await liveView.LoadHistoryAsync(ct).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
            {
                AppLogs.Session.ZLogWarning($"agent session history not replayed: {exception.Message}");
            }

            return;
        }

        var view = CreateAgentSessionView(child, root, ordinal: 0, isDraft: false, draftId: child.Id, readOnly);
        OpenAgentSessionTab(view, activate: true);
        try
        {
            await view.LoadHistoryAsync(ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            AppLogs.Session.ZLogWarning($"agent session history not replayed: {exception.Message}");
        }
    }

    /// <summary>The view of the tab filed under <paramref name="childSessionId"/>, if that tab is open; null otherwise.</summary>
    private SessionWorkspaceViewModel? FindOpenAgentSessionView(string childSessionId)
    {
        foreach (var view in agentViews.Keys)
        {
            if (string.Equals(view.SessionId, childSessionId, StringComparison.Ordinal))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the tab's view in agent-session mode and wires what the shell owns about it. <paramref name="readOnly"/> is a
    /// history row (or a child whose root left Active): no runtime, no turn gate, and its children for the transcript come
    /// from the store.
    /// </summary>
    private SessionWorkspaceViewModel CreateAgentSessionView(
        SessionInfo info,
        SessionInfo root,
        int ordinal,
        bool isDraft,
        string draftId,
        bool readOnly = false)
    {
        // A draft carries the parent link the store would write, so everything that reads it (the root's last activity, the
        // tree) needs no special case for "the row does not exist yet".
        var target = isDraft
            ? info with
            {
                Id = draftId,
                ParentSessionId = root.Id,
                ChildDriver = AgentSessionKinds.ChildDriverUser,
                Status = SessionStatus.Active,
            }
            : info;
        SessionWorkspaceViewModel? created = null;
        var session = new SessionWorkspaceViewModel(
            target,
            readOnly ? null : composition.Runtimes.Get(root),
            composition.Checkpoints,
            composition.Sessions,
            composition.Providers,
            OnSubAgentToolRequested,
            instrumentSources: instruments,
            preferences: composition.Preferences,
            openInspector: OpenInspector,
            openSession: () => ActivateAgentSessionTab(info.Id),
            agentSession: new AgentSessionLaunch(root, ordinal, isDraft),
            sessionMentions: () => created is null ? [] : SessionMentionTargetsFor(created),
            sessionReceipt: DeliverReceipt,
            // An agent session is a session like any other: its chip comes back where the user left it, and a pick made there
            // is remembered even when no turn follows it.
            sessionComposer: composerSelections,
            // The tab's label carries the root's name as a prefix, and the launch only carries a snapshot of it: a root renamed
            // after this tab was opened has its own tab (when it has one) answer with the title it holds now. Read the root
            // off the view, not off this closure: a promotion re-homes the tab under another root (RehomeTo).
            rootTitle: () => created is { } view ? CurrentRootTitle(view.AgentSessionRoot?.Id ?? root.Id) : null)
        {
            // A read-only child has no runtime to ask for its own children: the transcript replays them from the store.
            ChildQuery = readOnly
                ? (parent, token) => composition.Catalog.QueryAsync(new SessionQuery { ParentSessionId = parent }, token)
                : null,
        };
        created = session;
        if (!readOnly)
        {
            // The turn check is the root's (6.3): an agent session may not run under a root the store no longer holds as
            // active, in a workspace that is operating, or in a merge conflict.
            session.TurnGate = token => CheckTurnAsync(root.Id, token);
        }

        session.TurnCompleted += OnAgentSessionViewTurnCompleted;
        AttachAgentSessionView(session);
        return session;
    }

    /// <summary>
    /// Hooks what the shell owns about an agent-session view; <see cref="DetachAgentSessionView"/> is its counter, so a view
    /// whose tab was closed and then opened again gets exactly the subscriptions it had on screen.
    /// </summary>
    private void AttachAgentSessionView(SessionWorkspaceViewModel session)
    {
        session.AgentSessionCreated += OnAgentSessionCreated;
        session.AgentSessionTurnEnded += OnAgentSessionTurnEnded;
        session.TurnStarted += OnAgentSessionTurnStarted;
        session.TabTitleChanged += OnAgentSessionTitleChanged;
        session.AgentNameFollowedKind += OnAgentNameFollowedKind;
    }

    private void DetachAgentSessionView(SessionWorkspaceViewModel session)
    {
        session.AgentSessionCreated -= OnAgentSessionCreated;
        session.AgentSessionTurnEnded -= OnAgentSessionTurnEnded;
        session.TurnStarted -= OnAgentSessionTurnStarted;
        session.TabTitleChanged -= OnAgentSessionTitleChanged;
        session.AgentNameFollowedKind -= OnAgentNameFollowedKind;
    }

    /// <summary>
    /// The live view of an agent session whose tab was closed while its turn ran, by the child session id it holds now; null
    /// when there is none. It is the one view of that child session that exists, so reopening has to find it here: the view
    /// left <c>agentTabs</c>, <c>agentViews</c> and <c>sessions</c> when its tab closed, and only the closing set still holds it.
    /// </summary>
    private SessionWorkspaceViewModel? FindClosedAgentSessionView(string childSessionId)
    {
        foreach (var view in agentViewsClosing)
        {
            if (string.Equals(view.AgentChildSessionId, childSessionId, StringComparison.Ordinal))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts a closed-but-running agent-session view back on screen: it leaves the closing set (its turn end must no longer
    /// release it), gets the shell's subscriptions back and files itself under its tab again - no replay, because the run it
    /// shows is the one still in flight.
    /// </summary>
    private void ReopenAgentSessionTab(SessionWorkspaceViewModel session)
    {
        agentViewsClosing.Remove(session);
        AttachAgentSessionView(session);
        OpenAgentSessionTab(session, activate: true);
    }

    /// <summary>Puts the view in a main-group tab; a draft tab is not written to the layout until it has a session id.</summary>
    private void OpenAgentSessionTab(SessionWorkspaceViewModel session, bool activate)
    {
        var key = AgentTabKey(session);
        var tab = Workbench.FindTab(WorkbenchIds.SessionTab(key))
                   ?? Workbench.OpenTab(Workbench.MainTabs, WorkbenchIds.SessionTab(key), TabKind.Session, session, session.TabTitle);
        // The header's double-click rename: the child's own name, written through the same store path the tree row runs.
        tab.RenameCommand = new AsyncRelayCommand(
            ct => RenameAgentSessionTabAsync(session, ct),
            () => !disposed,
            OnCommandFailed);
        agentTabs[key] = tab;
        agentViews[session] = tab;
        if (session.AgentChildSessionId is { Length: > 0 } childId)
        {
            sessions[childId] = session;
            openTabs.Add(childId);
        }

        if (activate)
        {
            Workbench.ActivateTab(tab);
        }

        ScheduleLayoutSave();
    }

    /// <summary>The key a tab is filed under: the stored session id once there is one, the draft's id before that.</summary>
    private static string AgentTabKey(SessionWorkspaceViewModel session) => session.AgentChildSessionId ?? session.Info.Id;

    /// <summary>
    /// The double-click rename of an agent-session tab: the child's own name (never the composed "root › child" label) goes
    /// through the same store write as the tree row's rename, so the row, the tab and the <c>@</c> picker follow. A draft has
    /// no row yet: its name stays on the view and becomes the row's Title when the first message creates it.
    /// </summary>
    private async Task RenameAgentSessionTabAsync(SessionWorkspaceViewModel session, CancellationToken ct)
    {
        if (disposed || !session.IsAgentSession)
        {
            return;
        }

        var typed = await PromptTextAsync(ProjectStrings.RenameSessionTitle, session.AgentSessionName).ConfigureAwait(true);
        var title = typed?.Trim();
        if (string.IsNullOrEmpty(title) || string.Equals(title, session.AgentSessionName, StringComparison.Ordinal))
        {
            return;
        }

        if (session.AgentChildSessionId is { Length: > 0 } childId)
        {
            await Projects.RenameAsync(childId, title, ct).ConfigureAwait(true);
            return;
        }

        session.Rename(title);
    }

    /// <summary>The root's title as it stands now: the root's own tab, when it is open. Null when there is none, and the tab then
    /// keeps the title its launch carried.</summary>
    private string? CurrentRootTitle(string rootSessionId) =>
        sessions.TryGetValue(rootSessionId, out var root) ? root.Info.Title : null;

    /// <summary>
    /// The tab's name followed an agent-kind switch (forge-app "Agent 会话的界面与接线"): a stored session's row carries the new name
    /// from now on, so it goes through the same store path a rename takes and the tree row and the <c>@</c> picker follow. A draft has
    /// no row yet - its name becomes the row's <c>Title</c> when the first message creates it.
    /// </summary>
    private void OnAgentNameFollowedKind(SessionWorkspaceViewModel session, string name) => _ = WriteAgentKindNameAsync(session, name);

    private async Task WriteAgentKindNameAsync(SessionWorkspaceViewModel session, string name)
    {
        try
        {
            if (session.AgentChildSessionId is { Length: > 0 } childId)
            {
                await Projects.RenameAsync(childId, name, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The tab keeps the name on screen; the row keeps the old one until the next rename or kind switch.
            AppLogs.Session.ZLogWarning($"agent session name not stored: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            session.EndAgentKindRename(name);
        }
    }

    private void ActivateAgentSessionTab(string sessionId)
    {
        if (agentTabs.TryGetValue(sessionId, out var tab))
        {
            Workbench.ActivateTab(tab);
        }
    }

    /// <summary>Drafts still open under a root: their rows do not exist yet, so only this side knows about them.</summary>
    private int OpenAgentSessionCount(string rootSessionId) =>
        agentDrafts.TryGetValue(rootSessionId, out var drafts) ? drafts : 0;

    /// <summary>One draft of this root is gone (it became a row, or its tab was closed): its ordinal is free again.</summary>
    private void ReleaseAgentDraft(string rootSessionId)
    {
        if (!agentDrafts.TryGetValue(rootSessionId, out var open))
        {
            return;
        }

        if (open <= 1)
        {
            agentDrafts.Remove(rootSessionId);
            return;
        }

        agentDrafts[rootSessionId] = open - 1;
    }

    /// <summary>
    /// The first message created the session row: the tab moves to the stored id, the layout starts naming it, and the tree
    /// gets its row (the child session is in the store from now on, so a full refresh is what brings it in).
    /// <para>The row may land after its tab closed - a draft whose first turn ran on, closed while it did. That view is
    /// released at its turn's end and belongs in no open table, so only the store-side bookkeeping follows it here.</para>
    /// </summary>
    private void OnAgentSessionCreated(SessionWorkspaceViewModel session, string previousId)
    {
        var childId = session.AgentChildSessionId!;
        agentTabs.Remove(previousId);
        if (agentViews.TryGetValue(session, out var tab))
        {
            agentTabs[childId] = tab;
            tab.Title = session.TabTitle;
            openTabs.Add(childId);
            sessions[childId] = session;
            if (session.AgentSessionRoot is { } root)
            {
                // The draft became a row: it no longer counts as one of the pending drafts of its root.
                ReleaseAgentDraft(root.Id);
            }

            if (ReferenceEquals(session, activeSession))
            {
                // The tab's id just became the stored one: the panels (review comments, turns, git row) and the question
                // workbench read the active session's id, so they follow the tab instead of a draft id no row will ever carry.
                ApplyPanelContext();
                questionPanels?.OnActiveSessionChanged(session.SessionId);
            }
        }
        else
        {
            // The tab is gone: the id it would have been filed under goes with it, and the view stays out of the active
            // tables - a disposed view there would answer the tree, the palette and the layout as if a tab were on screen.
            agentTabs.Remove(childId);
        }

        if (session.AgentSessionRoot is { } createdRoot)
        {
            // The new row is a session the root's @ picker can offer from now on, and the tree draws it whether its tab is
            // still there or not: the row is in the store.
            mentionTargets.Remove(createdRoot.Id);
            RefreshSessionMentionsOf(createdRoot.Id);
        }

        _ = RefreshAgentSessionTreesAsync();
    }

    /// <summary>
    /// A turn of this tab ended: the unread mark goes up when the user is looking elsewhere, and the root's last activity
    /// follows its agent sessions - the idle-archive step reads exactly that stamp.
    /// </summary>
    private void OnAgentSessionTurnEnded(SessionWorkspaceViewModel session)
    {
        if (!ReferenceEquals(session, activeSession))
        {
            session.HasAgentUnread = true;
        }

        SetTreeRunning(session.SessionId, false);
        _ = TouchRootAsync(session);
        RefreshTreeAgents();
        Dispatcher.UIThread.Post(ScheduleLayoutSave);
    }

    /// <summary>
    /// Says the root session was active too: <c>sessions.last_active_at</c> is what the idle-archive step compares with its
    /// window, so a root whose agent sessions ran all day must not look idle.
    /// </summary>
    private async Task TouchRootAsync(SessionWorkspaceViewModel session)
    {
        if (session.Info.ParentSessionId is not { } rootId)
        {
            return;
        }

        try
        {
            await BackgroundRead.RunAsync(
                    token => composition.Catalog.TouchAsync(rootId, DateTimeOffset.UtcNow, token),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogs.Housekeeping.ZLogWarning($"could not touch the root session '{rootId}': {exception.Message}");
        }
    }

    /// <summary>A turn of this tab started: the tree row shows it running like any other session row.</summary>
    private void OnAgentSessionTurnStarted(SessionWorkspaceViewModel session, bool automation) =>
        SetTreeRunning(session.SessionId, true);

    /// <summary>The end of any turn of this tab: a view whose tab was closed while it ran goes here.</summary>
    private void OnAgentSessionViewTurnCompleted(SessionWorkspaceViewModel session) => ReleaseClosedAgentSession(session);

    /// <summary>
    /// A turn of a tab that is already closed ended: the view has nothing left to show, so it unsubscribes and goes - and the
    /// root's runtime goes with it when that turn was the last thing of the root still using it.
    /// </summary>
    private void ReleaseClosedAgentSession(SessionWorkspaceViewModel session)
    {
        if (session.IsRunning || !agentViewsClosing.Remove(session))
        {
            return;
        }

        session.TurnCompleted -= OnAgentSessionViewTurnCompleted;
        session.Dispose();
        ReleaseRootRuntimeIfUnused(session.AgentSessionRoot);
    }

    /// <summary>
    /// The root's runtime goes when nothing of that root needs it any more: none of its agent sessions has a tab on screen, no
    /// turn of a closed one is still in flight, and its own tab is not open to keep it. A runtime released under a running turn
    /// takes the host that turn's approvals and <c>ask_user</c> requests go through with it, so those would read as refused or
    /// cancelled instead of the turn ending on its own.
    /// </summary>
    private void ReleaseRootRuntimeIfUnused(SessionInfo? root)
    {
        if (root is null || HasAgentSessionsUsingRuntime(root.Id) || sessions.ContainsKey(root.Id))
        {
            return;
        }

        composition.Runtimes.Release(root.Id);
    }

    /// <summary>
    /// Whether an agent session of this root still needs the root's runtime: one with a tab on screen, or one whose tab was
    /// closed while a turn ran - that view is released at its turn's end and holds the runtime until then.
    /// </summary>
    private bool HasAgentSessionsUsingRuntime(string rootSessionId) =>
        HasOpenAgentSessions(rootSessionId) || HasClosingAgentSessions(rootSessionId);

    /// <summary>Whether a turn of a closed agent-session tab of this root is still in flight (its view is not released yet).</summary>
    private bool HasClosingAgentSessions(string rootSessionId)
    {
        foreach (var session in agentViewsClosing)
        {
            if (string.Equals(session.AgentSessionRoot?.Id, rootSessionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why the agent sessions under <paramref name="rootSessionId"/> are busy (lifecycle section 2, "the busy reasons are
    /// merged into the root"): every view of that root - the ones with a tab on screen and the ones whose tab was closed
    /// while a turn ran (their view lives until that turn ends) - contributes its <see cref="SessionWorkspaceViewModel.BusyReasons"/>
    /// (a running turn, queued messages, undelivered interjections), each reason once. An open but idle agent session
    /// contributes nothing: being open is not being busy.
    /// </summary>
    private List<string> AgentSessionBusyReasons(string rootSessionId)
    {
        var reasons = new List<string>(5);
        foreach (var session in agentViews.Keys.Concat(agentViewsClosing))
        {
            if (string.Equals(session.AgentSessionRoot?.Id, rootSessionId, StringComparison.Ordinal))
            {
                Merge(reasons, session.BusyReasons());
            }
        }

        return reasons;
    }

    private async Task RefreshAgentSessionTreesAsync()
    {
        try
        {
            await RefreshTreesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            AppLogs.Tree.ZLogWarning($"tree refresh after an agent session failed: {exception.Message}");
        }
    }

    private void OnAgentSessionTitleChanged(object? sender, EventArgs e)
    {
        if (sender is SessionWorkspaceViewModel session
            && agentViews.TryGetValue(session, out var tab)
            && !string.Equals(tab.Title, session.TabTitle, StringComparison.Ordinal))
        {
            tab.Title = session.TabTitle;
        }
    }

    /// <summary>
    /// A tab is going away: an agent-session tab disposes its own view (a draft one has no store row, so the session path
    /// would leave it behind), and it must unsubscribe from this window either way. A view whose tab closes while a turn runs
    /// keeps its <see cref="SessionWorkspaceViewModel.TurnCompleted"/> subscription: closing a tab stops nothing in flight, so
    /// that turn's end is what releases it (see <see cref="ReleaseClosedAgentSession"/>).
    /// </summary>
    private void DetachAgentSessionTab(TabModel tab)
    {
        if (tab.Content is not SessionWorkspaceViewModel session || !agentViews.Remove(session))
        {
            return;
        }

        DetachAgentSessionView(session);
        agentTabs.Remove(AgentTabKey(session));
        var root = session.AgentSessionRoot;
        if (session.AgentChildSessionId is { } childId)
        {
            openTabs.Remove(childId);
            sessions.Remove(childId);
            session.TabTitleChanged -= OnSessionTitleChanged;
            // A closed agent session takes every grant that named it as its target with it (forge-core "session messaging": a grant lives
            // until the target session ends): closing the tab is how the user ends that session here.
            OnMessageTargetClosed(childId);
            RefreshSessionMentionsOf(root?.Id ?? childId);
        }
        else if (root is not null)
        {
            // A draft that is closed leaves nothing behind: no row was ever written, and its ordinal is free again.
            ReleaseAgentDraft(root.Id);
        }

        if (session.IsRunning)
        {
            // The turn keeps running (a closed tab stops nothing that is already in flight): the view, its subscription and the
            // root's runtime all stay until that turn ends, which is what then releases them.
            agentViewsClosing.Add(session);
            return;
        }

        session.TurnCompleted -= OnAgentSessionViewTurnCompleted;
        session.Dispose();

        // The root's runtime is what this tab ran in; it goes with the last agent tab of that root, once the root's own tab is
        // not open to keep it (and never while a turn of another of them is still in flight).
        ReleaseRootRuntimeIfUnused(root);
    }

    /// <summary>Whether the agent session with this child id has a reply the user has not looked at (the tree row's mark).</summary>
    private bool IsAgentSessionUnread(string sessionId) =>
        sessions.TryGetValue(sessionId, out var view) && view.HasAgentUnread;

    /// <summary>
    /// A session was renamed from the tree: its open tab follows, whether it is the session's own tab or an agent session's
    /// (the store's Title is what the tab, the tree row and a later reopen all read). A renamed root also re-prefixes the labels of
    /// its agent sessions ("root › child"): their launch carries a snapshot of it and the root's own tab may well be closed, so the
    /// new title is pushed to them here.
    /// </summary>
    private void OnSessionRenamed(string sessionId, string title)
    {
        if (sessions.TryGetValue(sessionId, out var session))
        {
            // The dialog wrote it, so the tab records the source too: a later set_title then reads as kept instead of
            // silently overwriting a name a person chose.
            session.Rename(title, SessionTitleSource.User);
        }

        Retitle(sessionId, title);
    }

    /// <summary>
    /// Everything that shows a session's name, after the store holds a new one: the open agent sessions of a renamed root
    /// (their launch carries a snapshot of it, and "root › child" has to follow), the trees' rows, and the @ picker's names.
    /// A name no person typed - a derived placeholder, the model's <c>set_title</c> - arrives here the same way, so all three
    /// read one story per session.
    /// </summary>
    private void Retitle(string sessionId, string title)
    {
        foreach (var agent in agentViews.Keys)
        {
            if (string.Equals(agent.AgentSessionRoot?.Id, sessionId, StringComparison.Ordinal))
            {
                agent.RenameRoot(title);
            }
        }

        GeneralChats.RenameSession(sessionId, title);
        Projects.RenameSession(sessionId, title);

        // The @ picker keeps the names it read from the store: an agent session renamed while its tab is closed would still
        // be offered under its old name there, and an open popup would keep showing it.
        RenameMentionTarget(sessionId, title);
    }

    /// <summary>
    /// The roots whose agent sessions are open, for the idle-archive step: a root with an agent-session tab on screen is "open"
    /// even when its own tab is not, so the step must not archive it out from under that tab.
    /// </summary>
    private IReadOnlyList<SessionInfo> RootsWithOpenAgentSessions()
    {
        var roots = new List<SessionInfo>(agentViews.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in agentViews.Keys)
        {
            if (session.AgentSessionRoot is { } root && seen.Add(root.Id))
            {
                roots.Add(root);
            }
        }

        return roots;
    }
}
