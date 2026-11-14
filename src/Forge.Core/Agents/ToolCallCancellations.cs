using System.Collections.Concurrent;

namespace Forge.Core.Agents;

/// <summary>
/// The tool calls of one agent tree that are running right now, each with its own <see cref="CancellationTokenSource"/>
/// linked to its turn's token, so one call can be stopped without stopping the turn (the resource inspector's stop button).
/// Keyed by (agent id, call id): call ids come from the provider and are only unique within one agent's turn.
/// <para>Lifetime: <see cref="Begin"/> registers the source before the call's start event is emitted (a stop pressed the
/// moment a row appears finds it); <see cref="End"/> removes exactly that source and disposes it. A stop that races the call's
/// end either cancels a source that is still registered (the call then reports "cancelled by the user") or finds nothing /
/// a disposed source and reports false; it never throws and never cancels a later call that reused the id.</para>
/// </summary>
internal sealed class ToolCallCancellations
{
    private readonly ConcurrentDictionary<(string AgentId, string CallId), CancellationTokenSource> running = new();

    /// <summary>How many calls are registered; zero whenever no call runs (the leak check).</summary>
    public int Count => running.Count;

    /// <summary>
    /// A new source linked to <paramref name="turn"/>, registered under the call. A duplicate key (a provider that repeats a
    /// call id inside one batch) still gets its own source, it just cannot be stopped individually; the turn still stops it.
    /// </summary>
    public CancellationTokenSource Begin(string agentId, string callId, CancellationToken turn)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(turn);
        running.TryAdd((agentId, callId), source);
        return source;
    }

    /// <summary>Removes <paramref name="source"/> if it is still the one registered for the call, then disposes it.</summary>
    public void End(string agentId, string callId, CancellationTokenSource source)
    {
        running.TryRemove(new KeyValuePair<(string, string), CancellationTokenSource>((agentId, callId), source));
        source.Dispose();
    }

    /// <summary>
    /// Requests cancellation of one running call. The token's callbacks (a process-tree kill, a child agent's cancellation)
    /// run on the thread pool, not on the caller, so the UI thread never waits for a kill. False when no such call runs.
    /// </summary>
    public bool Cancel(string agentId, string callId)
    {
        if (!running.TryGetValue((agentId, callId), out var source))
        {
            return false;
        }

        try
        {
            if (source.IsCancellationRequested)
            {
                return false;
            }

            _ = source.CancelAsync();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The call ended between the lookup and the cancel: there is nothing left to stop.
            return false;
        }
    }
}
