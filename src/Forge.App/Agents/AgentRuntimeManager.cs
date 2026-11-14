using Forge.Abstractions;

namespace Forge.App.Agents;

/// <summary>
/// Owns one <see cref="SessionRuntime"/> per open main session, so several main sessions run concurrently, each with its
/// own host, root agent and cancellation source. Runtimes are created on demand and released when the tab closes; the
/// app tears down whatever is left in reverse creation order.
/// </summary>
internal sealed class AgentRuntimeManager : IDisposable
{
    private readonly Dictionary<string, SessionRuntime> runtimes = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly AgentRuntimeServices services;
    private bool disposed;

    public AgentRuntimeManager(AgentRuntimeServices services) => this.services = services;

    public SessionRuntime Get(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runtimes.TryGetValue(session.Id, out var existing))
            {
                return existing;
            }

            var runtime = new SessionRuntime(services, session);
            runtimes[session.Id] = runtime;
            return runtime;
        }
    }

    /// <summary>Drops the runtime of a closed tab; a closed tab stops nothing that runs in the background, so this is only
    /// called when the session is archived or the app exits.</summary>
    public void Release(string sessionId)
    {
        SessionRuntime? runtime;
        lock (gate)
        {
            if (!runtimes.Remove(sessionId, out runtime))
            {
                return;
            }
        }

        runtime.Dispose();
    }

    /// <summary>Re-applies the sub-agent cap to every live runtime; called when the setting changes.</summary>
    public void RefreshSubAgentLimits()
    {
        SessionRuntime[] all;
        lock (gate)
        {
            all = [.. runtimes.Values];
        }

        foreach (var runtime in all)
        {
            runtime.RefreshSubAgentLimits();
        }
    }

    public void Dispose()
    {
        SessionRuntime[] all;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            all = [.. runtimes.Values];
            runtimes.Clear();
        }

        foreach (var runtime in all)
        {
            runtime.Dispose();
        }
    }
}
