using System.Collections.Concurrent;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.ViewModels;

// The session's live tool calls: what Core's AgentToolProgress feeds, what the transcript's tool row shows inline and what
// the resource inspector (wave W8) lists. Split from SessionWorkspace.cs because that file is the shell and this is the
// one hot path the session has: every streamed snapshot of every running command passes through NoteToolTail.
//
// Threading, which is the whole design: start and finish arrive on the UI thread (they go through the frame coalescer like
// every other agent event), a streamed snapshot arrives on the tool's own output thread and is NOT dispatched - the
// producer already throttles to a few snapshots per second and a snapshot supersedes the one before it, so posting each of
// them would only queue work the UI then has to merge. The registry is a ConcurrentDictionary keyed by (agent, call); the
// snapshot path is a lookup plus one volatile write into the entry. Everything a bound control observes is raised from
// Tick/Finish, which only run on the UI thread.
internal sealed partial class SessionWorkspaceViewModel
{
    /// <summary>
    /// A snapshot published on the producer thread is not dispatched; the UI reads it on its next tick. Only a start event
    /// creates an entry, so a late snapshot can never bring back a row that the turn's end already cleared.
    /// </summary>
    private readonly ConcurrentDictionary<(string AgentId, string CallId), LiveToolViewModel> liveTools = new();

    /// <summary>
    /// A snapshot that arrived before the start event it belongs to was dispatched (the two come from different threads).
    /// Holds at most one string per such call; consumed by <see cref="TrackTool"/> and dropped with the turn.
    /// </summary>
    private readonly ConcurrentDictionary<(string AgentId, string CallId), string> pendingTails = new();

    /// <summary>
    /// The tool calls running right now, for the resource inspector. A <see cref="ConcurrentDictionary{TKey,TValue}.Values"/>
    /// read copies, and the inspector reads it on its own tick (at most once per 250 ms, by default once a second), so the
    /// copy is the cheap way to keep the producer thread free of the collection it would otherwise have to lock.
    /// </summary>
    internal ICollection<LiveToolViewModel> RunningTools => liveTools.Values;

    internal int RunningToolCount => liveTools.Count;

    /// <summary>
    /// UI thread: the live entry for a call that just started, created here (and only here) and seeded with a snapshot that
    /// reached the registry before this event did.
    /// <para>Refused for the session's own agent when no turn runs: a cancelled turn's start event can still be queued behind
    /// the turn's own end (the end is a higher-priority continuation than the coalescer's background flush), and a call that
    /// will never report a finish must not bring a row back after the end cleared it. A <em>child's</em> call is tracked even
    /// without a turn, because sub-agents also run outside one (<see cref="LaunchChildAsync"/>: the CI chain's Scout, a mailbox
    /// request); the child is asked for its own terminal event instead (see <see cref="FinishToolsOf"/>).</para>
    /// </summary>
    private LiveToolViewModel? TrackTool(string agentId, ToolCall call)
    {
        if (!IsRunning && !subAgentById.ContainsKey(agentId))
        {
            return null;
        }

        var key = (agentId, call.Id);
        if (!liveTools.TryGetValue(key, out var tracked))
        {
            var created = new LiveToolViewModel(agentId, () => AgentKindOf(agentId), call, DateTimeOffset.Now, Cancel);
            tracked = liveTools.GetOrAdd(key, created);
            if (pendingTails.TryRemove(key, out var pending))
            {
                tracked.RecordTail(pending);
            }
        }

        RaiseRunningResources();
        return tracked;
    }

    /// <summary>
    /// The tool's output thread: publish the newest snapshot of this call's output. Nothing on this path touches the UI or the
    /// sub-agent list (the label delegate is only invoked when a bound control reads it on the UI thread), and nothing here
    /// creates a row: a snapshot that beats its start event is parked for it.
    /// </summary>
    private void NoteToolTail(string agentId, ToolCall call, string tail)
    {
        var key = (agentId, call.Id);
        if (liveTools.TryGetValue(key, out var entry))
        {
            entry.RecordTail(tail);
            return;
        }

        pendingTails[key] = tail;

        // The start event may have created the entry between the two reads above; in that case the parked snapshot is moved
        // now and the start's own attempt finds nothing left to consume.
        if (liveTools.TryGetValue(key, out var raced) && pendingTails.TryRemove(key, out var late))
        {
            raced.RecordTail(late);
        }
    }

    /// <summary>
    /// The child is gone (finished, stopped, or cancelled with its turn): nothing it started can still be running, and a
    /// cancelled call never reports its finish, so its entries leave here rather than waiting for one that will not come.
    /// </summary>
    private void FinishToolsOf(string agentId)
    {
        if (liveTools.IsEmpty && pendingTails.IsEmpty)
        {
            return;
        }

        var removed = false;
        foreach (var key in liveTools.Keys)
        {
            if (string.Equals(key.AgentId, agentId, StringComparison.Ordinal) && liveTools.TryRemove(key, out var entry))
            {
                entry.Finish();
                removed = true;
            }
        }

        foreach (var key in pendingTails.Keys)
        {
            if (string.Equals(key.AgentId, agentId, StringComparison.Ordinal))
            {
                pendingTails.TryRemove(key, out _);
            }
        }

        if (removed)
        {
            RaiseRunningResources();
        }
    }

    /// <summary>UI thread: the call returned; its live tail and stop button go away, its transcript row stays.</summary>
    private void FinishTool(string agentId, string callId)
    {
        var key = (agentId, callId);
        pendingTails.TryRemove(key, out _);
        if (liveTools.TryRemove(key, out var entry))
        {
            entry.Finish();
            RaiseRunningResources();
        }
    }

    /// <summary>
    /// The running child's kind for a tool row's agent label; null for the session's own agent. Read when a row resolves
    /// its label (UI thread), never at creation, so a language switch and a child that is still starting both land.
    /// </summary>
    private string? AgentKindOf(string agentId) =>
        string.Equals(agentId, "root", StringComparison.Ordinal) ? null : Find(agentId)?.Kind;

    /// <summary>
    /// Once per session tick: refresh the elapsed readings and re-cut the tails at the current line limit. This is the only
    /// caller of <see cref="LiveToolViewModel.Tick"/>; the inspector's own timer calls
    /// <see cref="LiveToolViewModel.RefreshTail"/>, so a duration only ever moves with the session clock.
    /// </summary>
    private void TickLiveTools()
    {
        if (liveTools.IsEmpty)
        {
            return;
        }

        var maxLines = appPreferences?.InspectorLogTailLines ?? AppPreferences.DefaultInspectorLogTailLines;
        var now = DateTimeOffset.Now;
        foreach (var entry in liveTools.Values)
        {
            entry.Tick(maxLines, now);
        }
    }

    /// <summary>
    /// A turn boundary (start or end): the session's own agent has no call in flight outside a turn, so its leftovers go - a
    /// cancelled call never gets its <c>AgentToolFinished</c>, which is why the boundary does not wait for one. A live child's
    /// entries stay: a sub-agent also runs outside a turn (<see cref="LaunchChildAsync"/>: the CI chain's Scout, a mailbox
    /// request), and a user sending a message must not blind the inspector to the command that child is still streaming. Its
    /// entries are cleared by its own terminal event (<see cref="FinishToolsOf"/>).
    /// </summary>
    private void ClearTurnTools() => ClearLiveTools(keepLiveChildren: true);

    /// <summary>
    /// Drops live entries and parked snapshots: with <paramref name="keepLiveChildren"/> for a turn boundary, which leaves every
    /// still-live child's entries to that child, and without it for <see cref="Dispose"/>, where no agent can be running any
    /// more because the session is going away.
    /// </summary>
    private void ClearLiveTools(bool keepLiveChildren = false)
    {
        if (liveTools.IsEmpty && pendingTails.IsEmpty)
        {
            return;
        }

        var removed = false;
        foreach (var key in liveTools.Keys)
        {
            if (keepLiveChildren && subAgentById.ContainsKey(key.AgentId))
            {
                continue;
            }

            if (liveTools.TryRemove(key, out var entry))
            {
                entry.Finish();
                removed = true;
            }
        }

        foreach (var key in pendingTails.Keys)
        {
            if (keepLiveChildren && subAgentById.ContainsKey(key.AgentId))
            {
                continue;
            }

            pendingTails.TryRemove(key, out _);
        }

        if (removed)
        {
            RaiseRunningResources();
        }
    }

    /// <summary>
    /// What the strip's resources menu shows beside its "running resources" row: exactly the rows the inspector lists —
    /// running calls plus every live sub-agent, a queued child included (it is listed, so it is counted; the sub-agent chip's
    /// own badge still counts only the ones that hold a run slot).
    /// </summary>
    private void RaiseRunningResources() =>
        Instruments.NoteRunningResources(RunningToolCount + subAgents.Count);
}